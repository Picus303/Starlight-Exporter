using Starlight.Protocol;
using StarlightExporter.Snapshot;
using System.Globalization;

namespace StarlightExporter.Official;

public sealed class OfficialSnapshotCollector
{
    private const uint AetherId = 10000005;
    private const uint LumineId = 10000007;

    public async Task<OfficialSnapshot> CollectAsync(
        OfficialCaptureContext context,
        IOfficialMessageSource source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(source);

        PlayerDataNotify? playerData = null;
        bool receivedStore = false;
        bool receivedAvatars = false;
        var items = new Dictionary<ulong, Item>();
        var avatars = new Dictionary<ulong, AvatarInfo>();
        var teams = new Dictionary<uint, AvatarTeam>();
        uint currentTeamId = 0;
        ulong chosenAvatarGuid = 0;

        await foreach (OfficialMessageEnvelope envelope in source.ReadAllAsync(cancellationToken))
        {
            switch (envelope.Message)
            {
                case PlayerDataNotify player:
                    playerData = player;
                    break;

                case PlayerStoreNotify store:
                    receivedStore = true;
                    foreach (Item item in store.ItemList)
                    {
                        if (item.Guid != 0)
                        {
                            items[item.Guid] = item;
                        }
                    }
                    break;

                case AvatarDataNotify avatarData:
                    receivedAvatars = true;
                    foreach (AvatarInfo avatar in avatarData.AvatarList)
                    {
                        if (avatar.Guid != 0)
                        {
                            avatars[avatar.Guid] = avatar;
                        }
                    }

                    foreach ((uint id, AvatarTeam team) in avatarData.AvatarTeamMap)
                    {
                        teams[id] = team;
                    }

                    if (avatarData.CurAvatarTeamId != 0)
                    {
                        currentTeamId = avatarData.CurAvatarTeamId;
                    }

                    if (avatarData.ChooseAvatarGuid != 0)
                    {
                        chosenAvatarGuid = avatarData.ChooseAvatarGuid;
                    }
                    break;
            }
        }

        var missing = new List<string>();
        if (playerData is null)
        {
            missing.Add(nameof(PlayerDataNotify));
        }
        if (!receivedStore)
        {
            missing.Add(nameof(PlayerStoreNotify));
        }
        if (!receivedAvatars)
        {
            missing.Add(nameof(AvatarDataNotify));
        }
        if (missing.Count > 0)
        {
            throw Failure(
                OfficialConnectivityError.SyncIncomplete,
                $"The synchronization is incomplete: {string.Join(", ", missing)} missing.");
        }

        var unsupported = new List<UnsupportedRecord>();
        List<SnapshotMaterial> materials = MapMaterials(items.Values, unsupported);
        List<SnapshotWeapon> weapons = MapWeapons(items.Values, unsupported);
        HashSet<ulong> weaponGuids = weapons.Select(weapon => weapon.Guid).ToHashSet();
        List<SnapshotAvatar> snapshotAvatars = MapAvatars(avatars.Values, weaponGuids, unsupported);
        HashSet<ulong> avatarGuids = snapshotAvatars.Select(avatar => avatar.Guid).ToHashSet();
        List<SnapshotTeam> snapshotTeams = MapTeams(
            teams,
            avatarGuids,
            currentTeamId,
            chosenAvatarGuid,
            unsupported);

        uint bornAvatarId = snapshotAvatars
            .Select(avatar => avatar.AvatarId)
            .FirstOrDefault(id => id is AetherId or LumineId);
        if (bornAvatarId == 0)
        {
            throw Failure(
                OfficialConnectivityError.SyncIncomplete,
                "The synchronized avatar list does not contain Aether or Lumine.");
        }

        if (string.IsNullOrWhiteSpace(playerData!.NickName))
        {
            throw Failure(
                OfficialConnectivityError.CapturedDataInvalid,
                "The synchronized player nickname is empty.");
        }

        OfficialProfileSupplement? profile = context.Profile;
        if (profile is null)
        {
            unsupported.Add(new UnsupportedRecord("profile", "signature", "Profile source not captured yet."));
            unsupported.Add(new UnsupportedRecord("profile", "pictureId", "Profile source not captured yet."));
            unsupported.Add(new UnsupportedRecord("profile", "nameCardId", "Profile source not captured yet."));
        }

        var snapshot = new OfficialSnapshot {
            Manifest = new SnapshotManifest {
                SchemaVersion = SnapshotContract.CurrentSchemaVersion,
                SourceProtocolVersion = SnapshotContract.SupportedSourceProtocolVersion,
                CapturedAtUtc = context.CapturedAtUtc,
                Region = context.Region,
                OfficialUid = context.OfficialUid,
            },
            Player = new SnapshotPlayer {
                Nickname = playerData.NickName,
                Signature = profile?.Signature ?? string.Empty,
                PictureId = profile?.PictureId ?? 0,
                NameCardId = profile?.NameCardId ?? 0,
                BornState = SnapshotBornState.Complete,
                BornAvatarId = bornAvatarId,
                CurrentAvatarTeamId = currentTeamId,
            },
            Materials = materials,
            Weapons = weapons,
            Avatars = snapshotAvatars,
            Teams = snapshotTeams,
            Unsupported = unsupported,
        };

        SnapshotValidationResult validation = SnapshotValidator.Validate(snapshot);
        if (!validation.IsValid)
        {
            throw Failure(
                OfficialConnectivityError.CapturedDataInvalid,
                $"The captured snapshot is invalid: {string.Join(", ", validation.Errors.Select(error => error.Code))}.");
        }

        return snapshot;
    }

    private static List<SnapshotMaterial> MapMaterials(
        IEnumerable<Item> items,
        List<UnsupportedRecord> unsupported)
    {
        var materials = new Dictionary<uint, SnapshotMaterial>();
        foreach (Item item in items.OrderBy(item => item.Guid))
        {
            if (item.Material is null)
            {
                continue;
            }

            if (item.ItemId == 0 || item.Guid == 0 || item.Material.Count == 0)
            {
                unsupported.Add(new UnsupportedRecord(
                    "material",
                    item.ItemId.ToString(CultureInfo.InvariantCulture),
                    "Material identity, GUID or count is invalid."));
                continue;
            }

            materials[item.ItemId] = new SnapshotMaterial(item.ItemId, item.Guid, item.Material.Count);
        }

        return [.. materials.Values.OrderBy(material => material.ItemId)];
    }

    private static List<SnapshotWeapon> MapWeapons(
        IEnumerable<Item> items,
        List<UnsupportedRecord> unsupported)
    {
        var weapons = new List<SnapshotWeapon>();
        foreach (Item item in items.OrderBy(item => item.Guid))
        {
            Weapon? weapon = item.Equip?.Weapon;
            if (weapon is null)
            {
                if (item.Equip?.Reliquary is not null || item.Furniture is not null)
                {
                    unsupported.Add(new UnsupportedRecord(
                        "inventory",
                        item.Guid.ToString(CultureInfo.InvariantCulture),
                        "Inventory detail is outside the Starlight MVP."));
                }
                continue;
            }

            KeyValuePair<uint, uint>? affix = weapon.AffixMap
                .OrderBy(pair => pair.Key)
                .Cast<KeyValuePair<uint, uint>?>()
                .FirstOrDefault();
            if (weapon.AffixMap.Count > 1)
            {
                unsupported.Add(new UnsupportedRecord(
                    "weapon-affix",
                    item.Guid.ToString(CultureInfo.InvariantCulture),
                    "Only the first affix can be represented by the MVP snapshot."));
            }

            uint refinement = affix is { } selected ? checked(selected.Value + 1) : 1;
            if (item.ItemId == 0 || item.Guid == 0 || weapon.Level is < 1 or > 90 || refinement > 5)
            {
                throw Failure(
                    OfficialConnectivityError.CapturedDataInvalid,
                    $"Captured weapon {item.Guid} has invalid identity, level or refinement.");
            }

            weapons.Add(new SnapshotWeapon(
                item.ItemId,
                item.Guid,
                weapon.Level,
                refinement,
                weapon.PromoteLevel,
                affix?.Key ?? 0,
                GadgetId: 0));
        }

        return weapons;
    }

    private static List<SnapshotAvatar> MapAvatars(
        IEnumerable<AvatarInfo> avatars,
        IReadOnlySet<ulong> weaponGuids,
        List<UnsupportedRecord> unsupported)
    {
        var result = new List<SnapshotAvatar>();
        foreach (AvatarInfo avatar in avatars.OrderBy(avatar => avatar.AvatarId))
        {
            ulong weaponGuid = avatar.EquipGuidList.FirstOrDefault(weaponGuids.Contains);
            if (weaponGuid == 0)
            {
                unsupported.Add(new UnsupportedRecord(
                    "avatar",
                    avatar.AvatarId.ToString(CultureInfo.InvariantCulture),
                    "No captured weapon GUID is equipped by this avatar."));
                continue;
            }

            uint level = ReadUnsignedProperty(avatar.PropMap, PlayerProperty.Level, defaultValue: 1);
            uint constellation = avatar.CoreProudSkillLevel;
            if (avatar.AvatarId == 0
                || avatar.Guid == 0
                || level is < 1 or > 90
                || constellation > 6)
            {
                throw Failure(
                    OfficialConnectivityError.CapturedDataInvalid,
                    $"Captured avatar {avatar.AvatarId} has invalid identity, level or constellation.");
            }

            result.Add(new SnapshotAvatar(
                avatar.AvatarId,
                avatar.Guid,
                level,
                constellation,
                avatar.BornTime,
                weaponGuid));
        }

        return result;
    }

    private static List<SnapshotTeam> MapTeams(
        IReadOnlyDictionary<uint, AvatarTeam> teams,
        IReadOnlySet<ulong> avatarGuids,
        uint currentTeamId,
        ulong chosenAvatarGuid,
        List<UnsupportedRecord> unsupported)
    {
        var result = new List<SnapshotTeam>();
        foreach ((uint teamId, AvatarTeam team) in teams.OrderBy(pair => pair.Key))
        {
            if (teamId is < 1 or > 4)
            {
                unsupported.Add(new UnsupportedRecord(
                    "team",
                    teamId.ToString(CultureInfo.InvariantCulture),
                    "Team ID is outside the four slots persisted by Starlight."));
                continue;
            }

            List<ulong> members = [.. team.AvatarGuidList
                .Where(avatarGuids.Contains)
                .Distinct()
                .Take(4)];
            if (members.Count == 0)
            {
                unsupported.Add(new UnsupportedRecord(
                    "team",
                    teamId.ToString(CultureInfo.InvariantCulture),
                    "Team has no captured supported avatar."));
                continue;
            }

            ulong current = teamId == currentTeamId && members.Contains(chosenAvatarGuid)
                ? chosenAvatarGuid
                : members[0];
            result.Add(new SnapshotTeam(teamId, team.TeamName, members, current));
        }

        return result;
    }

    private static uint ReadUnsignedProperty(
        Dictionary<uint, PropValue> properties,
        PlayerProperty property,
        uint defaultValue)
    {
        if (!properties.TryGetValue((uint)property, out PropValue? value))
        {
            return defaultValue;
        }

        return value.Val is >= 0 and <= uint.MaxValue ? (uint)value.Val : defaultValue;
    }

    private static OfficialConnectivityException Failure(
        OfficialConnectivityError error,
        string message) => new(error, message);
}
