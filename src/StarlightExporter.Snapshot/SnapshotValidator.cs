namespace StarlightExporter.Snapshot;

public sealed record SnapshotValidationError(string Code, string Message);

public sealed record SnapshotValidationResult(IReadOnlyList<SnapshotValidationError> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

public static class SnapshotValidator
{
    public static SnapshotValidationResult Validate(OfficialSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var errors = new List<SnapshotValidationError>();

        ValidateManifest(snapshot.Manifest, errors);
        ValidatePlayer(snapshot.Player, errors);
        ValidateMaterials(snapshot.Materials, errors);
        ValidateWeapons(snapshot.Weapons, errors);
        ValidateAvatars(snapshot.Avatars, snapshot.Weapons, errors);
        ValidateTeams(snapshot.Teams, snapshot.Avatars, snapshot.Player.CurrentAvatarTeamId, errors);
        ValidateBornAvatar(snapshot.Player, snapshot.Avatars, errors);
        AddIf(snapshot.Unsupported.Count > SnapshotContract.MaximumUnsupportedRecords,
            "UNSUPPORTED_RECORD_LIMIT_EXCEEDED",
            $"At most {SnapshotContract.MaximumUnsupportedRecords} unsupported records are accepted.",
            errors);

        return new SnapshotValidationResult(errors);
    }

    private static void ValidateManifest(
        SnapshotManifest manifest,
        ICollection<SnapshotValidationError> errors)
    {
        AddIf(manifest.SchemaVersion != SnapshotContract.CurrentSchemaVersion,
            "SCHEMA_VERSION_UNSUPPORTED",
            $"Expected schema version {SnapshotContract.CurrentSchemaVersion}, got {manifest.SchemaVersion}.",
            errors);
        AddIf(!string.Equals(
                manifest.SourceProtocolVersion,
                SnapshotContract.SupportedSourceProtocolVersion,
                StringComparison.Ordinal),
            "SOURCE_PROTOCOL_VERSION_UNSUPPORTED",
            $"Expected source protocol {SnapshotContract.SupportedSourceProtocolVersion}.",
            errors);
        AddIf(manifest.OfficialUid == 0, "UID_MISSING", "Official UID must be non-zero.", errors);
        AddIf(string.IsNullOrWhiteSpace(manifest.Region), "REGION_MISSING", "Region is required.", errors);
    }

    private static void ValidatePlayer(
        SnapshotPlayer player,
        ICollection<SnapshotValidationError> errors)
    {
        AddIf(string.IsNullOrWhiteSpace(player.Nickname),
            "NICKNAME_MISSING", "Nickname is required.", errors);
        AddIf(player.Nickname.Length > 16,
            "NICKNAME_TOO_LONG", "Nickname cannot exceed 16 characters.", errors);
        AddIf(player.Signature.Length > 50,
            "SIGNATURE_TOO_LONG", "Signature cannot exceed 50 characters.", errors);
    }

    private static void ValidateMaterials(
        List<SnapshotMaterial> materials,
        ICollection<SnapshotValidationError> errors)
    {
        AddIf(materials.Count > SnapshotContract.MaximumMaterials,
            "MATERIAL_LIMIT_EXCEEDED",
            $"At most {SnapshotContract.MaximumMaterials} materials are accepted.",
            errors);
        AddDuplicateErrors(materials.Select(material => material.ItemId),
            "MATERIAL_ITEM_ID_DUPLICATE", "material item ID", errors);

        foreach (SnapshotMaterial material in materials)
        {
            AddIf(material.ItemId == 0, "MATERIAL_ITEM_ID_MISSING", "Material item ID must be non-zero.", errors);
            AddIf(material.Guid == 0, "MATERIAL_GUID_MISSING", "Material GUID must be non-zero.", errors);
            AddIf(material.Count == 0, "MATERIAL_COUNT_INVALID", "Material count must be positive.", errors);
        }
    }

    private static void ValidateWeapons(
        List<SnapshotWeapon> weapons,
        ICollection<SnapshotValidationError> errors)
    {
        AddIf(weapons.Count > SnapshotContract.MaximumWeapons,
            "WEAPON_LIMIT_EXCEEDED",
            $"At most {SnapshotContract.MaximumWeapons} weapons are accepted.",
            errors);
        AddDuplicateErrors(weapons.Select(weapon => weapon.Guid),
            "WEAPON_GUID_DUPLICATE", "weapon GUID", errors);

        foreach (SnapshotWeapon weapon in weapons)
        {
            AddIf(weapon.ItemId == 0, "WEAPON_ITEM_ID_MISSING", "Weapon item ID must be non-zero.", errors);
            AddIf(weapon.Guid == 0, "WEAPON_GUID_MISSING", "Weapon GUID must be non-zero.", errors);
            AddIf(weapon.Level is < 1 or > 90, "WEAPON_LEVEL_INVALID", "Weapon level must be between 1 and 90.", errors);
            AddIf(weapon.Refinement is < 1 or > 5, "WEAPON_REFINEMENT_INVALID", "Weapon refinement must be between 1 and 5.", errors);
        }
    }

    private static void ValidateAvatars(
        List<SnapshotAvatar> avatars,
        List<SnapshotWeapon> weapons,
        ICollection<SnapshotValidationError> errors)
    {
        AddIf(avatars.Count > SnapshotContract.MaximumAvatars,
            "AVATAR_LIMIT_EXCEEDED",
            $"At most {SnapshotContract.MaximumAvatars} avatars are accepted.",
            errors);
        AddDuplicateErrors(avatars.Select(avatar => avatar.Guid),
            "AVATAR_GUID_DUPLICATE", "avatar GUID", errors);
        AddDuplicateErrors(avatars.Select(avatar => avatar.AvatarId),
            "AVATAR_ID_DUPLICATE", "avatar ID", errors);

        HashSet<ulong> weaponGuids = weapons.Select(weapon => weapon.Guid).ToHashSet();
        foreach (SnapshotAvatar avatar in avatars)
        {
            AddIf(avatar.AvatarId == 0, "AVATAR_ID_MISSING", "Avatar ID must be non-zero.", errors);
            AddIf(avatar.Guid == 0, "AVATAR_GUID_MISSING", "Avatar GUID must be non-zero.", errors);
            AddIf(avatar.Level is < 1 or > 90, "AVATAR_LEVEL_INVALID", "Avatar level must be between 1 and 90.", errors);
            AddIf(avatar.Constellation > 6, "AVATAR_CONSTELLATION_INVALID", "Avatar constellation cannot exceed 6.", errors);
            AddIf(avatar.BornTime is < 0 or > uint.MaxValue,
                "AVATAR_BORN_TIME_INVALID",
                $"Avatar {avatar.AvatarId} born time must fit in an unsigned 32-bit value.",
                errors);
            AddIf(!weaponGuids.Contains(avatar.WeaponGuid),
                "AVATAR_WEAPON_MISSING",
                $"Avatar {avatar.AvatarId} references missing weapon GUID {avatar.WeaponGuid}.",
                errors);
        }
    }

    private static void ValidateTeams(
        List<SnapshotTeam> teams,
        List<SnapshotAvatar> avatars,
        uint currentTeamId,
        ICollection<SnapshotValidationError> errors)
    {
        AddIf(teams.Count > 4, "TEAM_COUNT_INVALID", "At most four teams are supported.", errors);
        AddDuplicateErrors(teams.Select(team => team.TeamId),
            "TEAM_ID_DUPLICATE", "team ID", errors);

        HashSet<ulong> avatarGuids = avatars.Select(avatar => avatar.Guid).ToHashSet();
        foreach (SnapshotTeam team in teams)
        {
            AddIf(team.TeamId is < 1 or > 4, "TEAM_ID_INVALID", "Team ID must be between 1 and 4.", errors);
            AddIf(team.AvatarGuids.Count is < 1 or > 4,
                "TEAM_SIZE_INVALID", $"Team {team.TeamId} must contain between one and four avatars.", errors);
            AddDuplicateErrors(team.AvatarGuids,
                "TEAM_AVATAR_DUPLICATE", $"avatar GUID in team {team.TeamId}", errors);

            foreach (ulong avatarGuid in team.AvatarGuids)
            {
                AddIf(!avatarGuids.Contains(avatarGuid),
                    "TEAM_AVATAR_MISSING",
                    $"Team {team.TeamId} references missing avatar GUID {avatarGuid}.",
                    errors);
            }

            AddIf(!team.AvatarGuids.Contains(team.CurrentAvatarGuid),
                "CURRENT_AVATAR_NOT_IN_TEAM",
                $"Current avatar for team {team.TeamId} must be a member of that team.",
                errors);
        }

        AddIf(teams.All(team => team.TeamId != currentTeamId),
            "CURRENT_TEAM_MISSING",
            $"Current team {currentTeamId} does not exist.",
            errors);
    }

    private static void ValidateBornAvatar(
        SnapshotPlayer player,
        List<SnapshotAvatar> avatars,
        ICollection<SnapshotValidationError> errors)
    {
        if (player.BornState != SnapshotBornState.Complete)
        {
            AddIf(avatars.Count > 0,
                "BORN_STATE_INCONSISTENT",
                "A snapshot with avatars must have a complete born state.",
                errors);
            return;
        }

        AddIf(player.BornAvatarId is not 10000005 and not 10000007,
            "BORN_AVATAR_INVALID",
            "Born avatar must be Aether (10000005) or Lumine (10000007).",
            errors);
        AddIf(avatars.All(avatar => avatar.AvatarId != player.BornAvatarId),
            "BORN_AVATAR_MISSING",
            "Born avatar must be present in the avatar collection.",
            errors);
    }

    private static void AddDuplicateErrors<T>(
        IEnumerable<T> values,
        string code,
        string label,
        ICollection<SnapshotValidationError> errors)
        where T : notnull
    {
        foreach (T value in values.GroupBy(value => value).Where(group => group.Count() > 1).Select(group => group.Key))
        {
            errors.Add(new SnapshotValidationError(code, $"Duplicate {label}: {value}."));
        }
    }

    private static void AddIf(
        bool condition,
        string code,
        string message,
        ICollection<SnapshotValidationError> errors)
    {
        if (condition)
        {
            errors.Add(new SnapshotValidationError(code, message));
        }
    }
}
