using Starlight.Game.Resources;
using Starlight.Rpc.Proto;
using StarlightExporter.Snapshot;

namespace StarlightExporter.Mapping;

public sealed class StarlightSnapshotMapper(GameData gameData)
{
    private const int MaterialCountLimit = 2000;
    private const uint MaxTeamCount = 4;
    private const int WeaponCountLimit = 2000;

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

        ValidateMappedState(state, issues);

        return new StarlightMappingResult(profile, state, issues);
    }

    private void MapMaterials(
        IEnumerable<SnapshotMaterial> materials,
        NetPlayerState state,
        List<MappingIssue> issues)
    {
        int excludedByLimit = 0;
        foreach (SnapshotMaterial source in materials.OrderBy(material => material.ItemId))
        {
            if (!gameData.MaterialData.TryGetValue(source.ItemId, out var resource)
                || !resource.IsInventoryMaterial)
            {
                issues.Add(Warning(
                    "UNSUPPORTED_ITEM",
                    $"Material {source.ItemId} is absent from or unsupported by the target resources."));
                continue;
            }

            if (state.Materials.Count >= MaterialCountLimit)
            {
                excludedByLimit++;
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

        if (excludedByLimit > 0)
        {
            issues.Add(Warning(
                "MATERIAL_LIMIT_REACHED",
                $"{excludedByLimit} material stack(s) were excluded because Starlight accepts at most {MaterialCountLimit}."));
        }
    }

    private HashSet<ulong> MapWeapons(
        IEnumerable<SnapshotWeapon> weapons,
        NetPlayerState state,
        List<MappingIssue> issues)
    {
        var mappedGuids = new HashSet<ulong>();
        int excludedByLimit = 0;

        foreach (SnapshotWeapon source in weapons.OrderBy(weapon => weapon.Guid))
        {
            if (!gameData.WeaponData.TryGetValue(source.ItemId, out var resource))
            {
                issues.Add(Warning(
                    "UNSUPPORTED_ITEM",
                    $"Weapon {source.ItemId} is absent from the target resources."));
                continue;
            }

            if (state.Weapons.Count >= WeaponCountLimit)
            {
                excludedByLimit++;
                continue;
            }

            (uint minimumPromotion, uint maximumPromotion) = PromotionRangeFor(source.Level);
            uint promoteLevel = Math.Clamp(source.PromoteLevel, minimumPromotion, maximumPromotion);
            if (promoteLevel != source.PromoteLevel)
            {
                issues.Add(Warning(
                    "WEAPON_PROMOTE_LEVEL_REPAIRED",
                    $"Weapon {source.Guid} promote level was adjusted from {source.PromoteLevel} to {promoteLevel} for level {source.Level}."));
            }

            uint affixId = resource.SkillAffix.FirstOrDefault();
            if (resource.SkillAffix.Count == 0)
            {
                issues.Add(Warning(
                    "WEAPON_AFFIX_MISSING",
                    $"Weapon resource {source.ItemId} has no skill affix; affix ID zero will be used."));
            }
            else if (resource.SkillAffix.Count > 1)
            {
                issues.Add(Warning(
                    "WEAPON_AFFIX_AMBIGUOUS",
                    $"Weapon resource {source.ItemId} has multiple skill affixes; {affixId} was selected to match Starlight."));
            }

            if (source.AffixId != affixId || source.GadgetId != resource.GadgetId)
            {
                issues.Add(Warning(
                    "WEAPON_METADATA_REPLACED",
                    $"Weapon {source.Guid} metadata was replaced with values from the target resources."));
            }

            state.Weapons.Add(new NetWeapon {
                ItemId = source.ItemId,
                Guid = source.Guid,
                Level = source.Level,
                Refinement = source.Refinement,
                PromoteLevel = promoteLevel,
                AffixId = affixId,
                GadgetId = resource.GadgetId
            });
            mappedGuids.Add(source.Guid);
        }

        if (excludedByLimit > 0)
        {
            issues.Add(Warning(
                "WEAPON_LIMIT_REACHED",
                $"{excludedByLimit} weapon(s) were excluded because Starlight accepts at most {WeaponCountLimit}."));
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

        foreach (SnapshotAvatar source in avatars.OrderBy(avatar => avatar.AvatarId))
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

    private static (uint Minimum, uint Maximum) PromotionRangeFor(uint level) => level switch {
        < 20 => (0, 0),
        20 => (0, 1),
        < 40 => (1, 1),
        40 => (1, 2),
        < 50 => (2, 2),
        50 => (2, 3),
        < 60 => (3, 3),
        60 => (3, 4),
        < 70 => (4, 4),
        70 => (4, 5),
        < 80 => (5, 5),
        80 => (5, 6),
        _ => (6, 6)
    };

    private static void ValidateMappedState(NetPlayerState state, List<MappingIssue> issues)
    {
        bool invalid = state.Materials.Count > MaterialCountLimit
            || state.Materials.Any(item => item.ItemId == 0 || item.Guid == 0 || item.Count == 0)
            || state.Weapons.Count > WeaponCountLimit
            || state.Weapons.Any(item => item.ItemId == 0 || item.Guid == 0)
            || state.Avatars.Any(avatar => avatar.AvatarId == 0
                || avatar.Guid == 0
                || state.Weapons.All(weapon => weapon.Guid != avatar.WeaponGuid))
            || state.AvatarTeams.Count != MaxTeamCount
            || state.AvatarTeams.Select(team => team.TeamId).Distinct().Count() != MaxTeamCount
            || state.AvatarTeams.Any(team => team.TeamId is < 1 or > MaxTeamCount
                || team.AvatarGuids.Any(guid => state.Avatars.All(avatar => avatar.Guid != guid))
                || team.AvatarGuids.Count > 0 && !team.AvatarGuids.Contains(team.CurrentAvatarGuid))
            || state.AvatarTeams.All(team => team.TeamId != state.CurrentAvatarTeamId
                || team.AvatarGuids.Count == 0);

        if (invalid)
        {
            issues.Add(Error(
                "STATE_INVARIANT_FAILED",
                "The mapped player state does not satisfy the invariants required by the pinned Starlight modules."));
        }
    }

    private static NetAvatarTeam CreateEmptyTeam(uint teamId) => new() {
        TeamId = teamId,
        Name = $"Team {teamId}"
    };

    private static MappingIssue Warning(string code, string message) =>
        new(MappingIssueSeverity.Warning, code, message);

    private static MappingIssue Error(string code, string message) =>
        new(MappingIssueSeverity.Error, code, message);
}
