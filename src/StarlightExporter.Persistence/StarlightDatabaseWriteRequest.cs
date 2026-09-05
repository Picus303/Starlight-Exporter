using Starlight.Rpc.Proto;

namespace StarlightExporter.Persistence;

public sealed record StarlightDatabaseWriteRequest(
    string OutputPath,
    uint PlayerUid,
    string PrivateAccountId,
    NetPlayerProfile Profile,
    NetPlayerState State);

public sealed record StarlightDatabaseWriteResult(
    string OutputPath,
    uint PlayerUid,
    string PrivateAccountId,
    int MaterialCount,
    int WeaponCount,
    int AvatarCount,
    int TeamCount);
