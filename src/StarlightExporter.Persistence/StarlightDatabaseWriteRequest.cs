using StarlightExporter.Mapping;

namespace StarlightExporter.Persistence;

public sealed record StarlightDatabaseWriteRequest(
    string OutputPath,
    uint PlayerUid,
    string PrivateAccountId,
    StarlightMappingResult Mapping);

public sealed record StarlightDatabaseWriteResult(
    string OutputPath,
    uint PlayerUid,
    string PrivateAccountId,
    int MaterialCount,
    int WeaponCount,
    int AvatarCount,
    int TeamCount);

