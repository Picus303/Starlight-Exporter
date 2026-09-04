using Starlight.Game.Resources;
using Starlight.Rpc.Proto;
using StarlightExporter.Snapshot;

namespace StarlightExporter.Mapping;

public sealed class StarlightSnapshotMapper(GameData gameData)
{
    private const uint MaxTeamCount = 4;

    public StarlightMappingResult Map(OfficialSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var issues = new List<MappingIssue>();
        SnapshotValidationResult validation = SnapshotValidator.Validate(snapshot);
        issues.AddRange(validation.Errors.Select(error => new MappingIssue(
            MappingIssueSeverity.Error,
            error.Code,
            error.Message)));

        var profile = new NetPlayerProfile {
            Nickname = snapshot.Player.Nickname,
            Signature = snapshot.Player.Signature,
            PictureId = snapshot.Player.PictureId,
            NameCardId = snapshot.Player.NameCardId
        };
        var state = new NetPlayerState {
            BornState = snapshot.Player.BornState switch {
                SnapshotBornState.Pending => NetPlayerState.Types.PlayerBornState.Pending,
                SnapshotBornState.Complete => NetPlayerState.Types.PlayerBornState.Complete,
                _ => NetPlayerState.Types.PlayerBornState.Unspecified
            },
            BornAvatarId = snapshot.Player.BornAvatarId
        };

        if (!validation.IsValid)
        {
            return new StarlightMappingResult(profile, state, issues);
        }

        MapMaterials(snapshot.Materials, state, issues);
        HashSet<ulong> mappedWeaponGuids = MapWeapons(snapshot.Weapons, state, issues);
        HashSet<ulong> mappedAvatarGuids = MapAvatars(
            snapshot.Avatars,
            mappedWeaponGuids,
            state,
            issues);
        MapTeams(snapshot.Teams, mappedAvatarGuids, snapshot.Player.CurrentAvatarTeamId, state, issues);

        if (state.BornState == NetPlayerState.Types.PlayerBornState.Complete
            && state.Avatars.All(avatar => avatar.AvatarId != state.BornAvatarId))
        {
            issues.Add(Error(
                "BORN_AVATAR_UNSUPPORTED",
                $"Born avatar {state.BornAvatarId} could not be mapped with the target resources."));
        }

        return new StarlightMappingResult(profile, state, issues);
    }

    private void MapMaterials(
        IEnumerable<SnapshotMaterial> materials,
        NetPlayerState state,
        List<MappingIssue> issues)
    {
        foreach (SnapshotMaterial source in materials)
        {
            if (!gameData.MaterialData.TryGetValue(source.ItemId, out var resource)
                || !resource.IsInventoryMaterial)
            {
                issues.Add(Warning(
                    "UNSUPPORTED_ITEM",
                    $"Material {source.ItemId} is absent from or unsupported by the target resources."));
                continue;
            }

            uint stackLimit = Math.Max(resource.StackLimit, 1u);
            uint count = Math.Min(source.Count, stackLimit);
            if (count != source.Count)
            {
                issues.Add(Warning(
                    "STACK_CLAMPED",
                    $"Material {source.ItemId} count was clamped from {source.Count} to {count}."));
            }

            state.Materials.Add(new NetMaterial {
                ItemId = source.ItemId,
                Guid = source.Guid,
                Count = count
            });
        }
    }

    private HashSet<ulong> MapWeapons(
        IEnumerable<SnapshotWeapon> weapons,
        NetPlayerState state,
        List<MappingIssue> issues)
    {
        var mappedGuids = new HashSet<ulong>();

        foreach (SnapshotWeapon source in weapons)
        {
            if (!gameData.WeaponData.TryGetValue(source.ItemId, out var resource))
            {
                issues.Add(Warning(
                    "UNSUPPORTED_ITEM",
                    $"Weapon {source.ItemId} is absent from the target resources."));
                continue;
            }

            uint promoteLevel = Math.Min(source.PromoteLevel, 6u);
            if (promoteLevel != source.PromoteLevel)
            {
                issues.Add(Warning(
                    "WEAPON_PROMOTE_LEVEL_CLAMPED",
                    $"Weapon {source.Guid} promote level was clamped from {source.PromoteLevel} to {promoteLevel}."));
            }

            state.Weapons.Add(new NetWeapon {
                ItemId = source.ItemId,
                Guid = source.Guid,
                Level = source.Level,
                Refinement = source.Refinement,
                PromoteLevel = promoteLevel,
                AffixId = resource.SkillAffix.FirstOrDefault(),
                GadgetId = resource.GadgetId
            });
            mappedGuids.Add(source.Guid);
        }

        return mappedGuids;
    }

    private HashSet<ulong> MapAvatars(
        IEnumerable<SnapshotAvatar> avatars,
        HashSet<ulong> mappedWeaponGuids,
        NetPlayerState state,
        List<MappingIssue> issues)
    {
        var mappedGuids = new HashSet<ulong>();

        foreach (SnapshotAvatar source in avatars)
        {
            if (!CanCreateAvatar(source.AvatarId))
            {
                issues.Add(Warning(
                    "UNSUPPORTED_AVATAR",
                    $"Avatar {source.AvatarId} cannot be created with the target resources."));
                continue;
            }

            if (!mappedWeaponGuids.Contains(source.WeaponGuid))
            {
                issues.Add(Error(
                    "AVATAR_WEAPON_UNSUPPORTED",
                    $"Avatar {source.AvatarId} references weapon GUID {source.WeaponGuid}, which was not mapped."));
                continue;
            }

            uint bornTime;
            if (source.BornTime is < 0 or > uint.MaxValue)
            {
                bornTime = 0;
                issues.Add(Warning(
                    "BORN_TIME_INVALID",
                    $"Avatar {source.AvatarId} born time is outside the supported UInt32 range and was reset to zero."));
            }
            else
            {
                bornTime = (uint)source.BornTime;
            }

            state.Avatars.Add(new NetAvatar {
                AvatarId = source.AvatarId,
                Guid = source.Guid,
                Level = source.Level,
                Constellation = source.Constellation,
                BornTime = bornTime,
                WeaponGuid = source.WeaponGuid
            });
            mappedGuids.Add(source.Guid);
        }

        return mappedGuids;
    }

    private static void MapTeams(
        IEnumerable<SnapshotTeam> teams,
        HashSet<ulong> mappedAvatarGuids,
        uint requestedCurrentTeamId,
        NetPlayerState state,
        List<MappingIssue> issues)
    {
        Dictionary<uint, SnapshotTeam> sourceTeams = teams.ToDictionary(team => team.TeamId);
        bool completedSlots = false;

        for (uint teamId = 1; teamId <= MaxTeamCount; teamId++)
        {
            if (!sourceTeams.TryGetValue(teamId, out SnapshotTeam? source))
            {
                completedSlots = true;
                state.AvatarTeams.Add(CreateEmptyTeam(teamId));
                continue;
            }

            List<ulong> members = source.AvatarGuids
                .Where(mappedAvatarGuids.Contains)
                .Take(4)
                .ToList();
            ulong currentAvatarGuid = members.Contains(source.CurrentAvatarGuid)
                ? source.CurrentAvatarGuid
                : members.FirstOrDefault();
            string name = string.IsNullOrWhiteSpace(source.Name) ? $"Team {teamId}" : source.Name;

            if (members.Count != source.AvatarGuids.Count
                || currentAvatarGuid != source.CurrentAvatarGuid
                || !string.Equals(name, source.Name, StringComparison.Ordinal))
            {
                issues.Add(Warning(
                    "TEAM_REPAIRED",
                    $"Team {teamId} was adjusted to match the mapped avatar roster."));
            }

            var target = new NetAvatarTeam {
                TeamId = teamId,
                Name = name,
                CurrentAvatarGuid = currentAvatarGuid
            };
            target.AvatarGuids.Add(members);
            state.AvatarTeams.Add(target);
        }

        if (completedSlots)
        {
            issues.Add(Warning(
                "TEAM_SLOTS_COMPLETED",
                "Missing team slots were added so Starlight does not repair the state during login."));
        }

        NetAvatarTeam? requested = state.AvatarTeams
            .FirstOrDefault(team => team.TeamId == requestedCurrentTeamId && team.AvatarGuids.Count > 0);
        NetAvatarTeam? selected = requested
            ?? state.AvatarTeams.FirstOrDefault(team => team.AvatarGuids.Count > 0);

        state.CurrentAvatarTeamId = selected?.TeamId ?? 0;
        if (selected is null)
        {
            issues.Add(Error("CURRENT_TEAM_MISSING", "No non-empty team remains after mapping."));
        }
        else if (selected.TeamId != requestedCurrentTeamId)
        {
            issues.Add(Warning(
                "CURRENT_TEAM_REPAIRED",
                $"Current team was changed from {requestedCurrentTeamId} to {selected.TeamId}."));
        }
    }

    private bool CanCreateAvatar(uint avatarId) =>
        gameData.AvatarData.TryGetValue(avatarId, out var avatar)
        && gameData.AvatarSkillDepotData.ContainsKey(avatar.SkillDepotId)
        && gameData.WeaponData.ContainsKey(avatar.InitialWeapon)
        && gameData.Avatars.ContainsKey(avatarId);

    private static NetAvatarTeam CreateEmptyTeam(uint teamId) => new() {
        TeamId = teamId,
        Name = $"Team {teamId}"
    };

    private static MappingIssue Warning(string code, string message) =>
        new(MappingIssueSeverity.Warning, code, message);

    private static MappingIssue Error(string code, string message) =>
        new(MappingIssueSeverity.Error, code, message);
}
