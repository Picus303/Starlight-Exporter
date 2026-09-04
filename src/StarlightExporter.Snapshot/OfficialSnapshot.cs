namespace StarlightExporter.Snapshot;

public sealed record OfficialSnapshot
{
    public required SnapshotManifest Manifest { get; init; }
    public required SnapshotPlayer Player { get; init; }
    public required List<SnapshotMaterial> Materials { get; init; }
    public required List<SnapshotWeapon> Weapons { get; init; }
    public required List<SnapshotAvatar> Avatars { get; init; }
    public required List<SnapshotTeam> Teams { get; init; }
    public required List<UnsupportedRecord> Unsupported { get; init; }
}

public sealed record SnapshotManifest
{
    public required int SchemaVersion { get; init; }
    public required string StarlightCommit { get; init; }
    public required string ProtocolVersion { get; init; }
    public required DateTimeOffset CapturedAtUtc { get; init; }
    public required string Region { get; init; }
    public required uint OfficialUid { get; init; }
}

public sealed record SnapshotPlayer
{
    public required string Nickname { get; init; }
    public string Signature { get; init; } = string.Empty;
    public uint PictureId { get; init; }
    public uint NameCardId { get; init; }
    public required SnapshotBornState BornState { get; init; }
    public required uint BornAvatarId { get; init; }
    public required uint CurrentAvatarTeamId { get; init; }
}

public enum SnapshotBornState
{
    Pending,
    Complete,
}

public sealed record SnapshotMaterial(uint ItemId, ulong Guid, uint Count);

public sealed record SnapshotWeapon(
    uint ItemId,
    ulong Guid,
    uint Level,
    uint Refinement,
    uint PromoteLevel,
    uint AffixId,
    uint GadgetId);

public sealed record SnapshotAvatar(
    uint AvatarId,
    ulong Guid,
    uint Level,
    uint Constellation,
    long BornTime,
    ulong WeaponGuid);

public sealed record SnapshotTeam(
    uint TeamId,
    string Name,
    List<ulong> AvatarGuids,
    ulong CurrentAvatarGuid);

public sealed record UnsupportedRecord(string Category, string Identifier, string Reason);

