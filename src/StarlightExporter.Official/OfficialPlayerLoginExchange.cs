using Starlight.Protocol;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace StarlightExporter.Official;

public sealed record OfficialPlayerLoginProfile
{
    public required string PlatformName { get; init; }
    public required string DeviceInfo { get; init; }
    public required string DeviceName { get; init; }
    public required string DeviceUuid { get; init; }
    public required string SystemVersion { get; init; }
    public required string Checksum { get; init; }
    public required string ChecksumClientVersion { get; init; }
    public string ClientVersionHash { get; init; } = string.Empty;
    public string UserAgent { get; init; } = string.Empty;
    public uint RegistrationPlatform { get; init; } = 3;
}

public sealed class OfficialPlayerLoginExchange
{
    private readonly ComboSession _session;
    private readonly OfficialCurrentRegion _region;
    private readonly OfficialClientProfile _client;
    private readonly OfficialPlayerLoginProfile _login;
    private readonly OfficialPlayerTokenResult _token;
    private bool _completed;

    public OfficialPlayerLoginExchange(
        ComboSession session,
        OfficialCurrentRegion region,
        OfficialClientProfile client,
        OfficialPlayerLoginProfile login,
        OfficialPlayerTokenResult token)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(login);
        ArgumentNullException.ThrowIfNull(token);
        Validate(login);

        _session = session;
        _region = region;
        _client = client;
        _login = login;
        _token = token;
    }

    public byte[] EncodeRequest(
        OfficialGatePacketCodec codec,
        OfficialGateCipherState cipher,
        PacketHead? metadata = null)
    {
        if (_completed)
        {
            throw new InvalidOperationException("The player-login exchange is already complete.");
        }
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentNullException.ThrowIfNull(cipher);

        Span<byte> random = stackalloc byte[sizeof(ulong)];
        RandomNumberGenerator.Fill(random);
        ulong loginRandom = BinaryPrimitives.ReadUInt64BigEndian(random);
        CryptographicOperations.ZeroMemory(random);

        var request = new PlayerLoginReq {
            Token = _token.SessionToken.Reveal(),
            Checksum = _login.Checksum,
            UaPc = _login.UserAgent,
            Platform = _login.PlatformName,
            ClientVersion = _client.Version,
            DeviceInfo = _login.DeviceInfo,
            DeviceName = _login.DeviceName,
            ClientVersionHash = _login.ClientVersionHash,
            DeviceUuid = _login.DeviceUuid,
            CountryCode = _session.CountryCode,
            AccountUid = _session.AccountUid,
            ChecksumClientVersion = _login.ChecksumClientVersion,
            SystemVersion = _login.SystemVersion,
            ChannelId = _client.ChannelId,
            LanguageType = _client.Language,
            SubChannelId = _client.SubChannelId,
            AccountType = _session.AccountType,
            TargetUid = _token.PlayerUid,
            LoginRand = loginRandom,
            IsGuest = _session.IsGuest,
            PlatformType = _client.Platform,
            ClientDataVersion = _region.ClientDataVersion,
            RegPlatform = _login.RegistrationPlatform,
        };
        return codec.EncodeEncrypted(request, cipher, metadata);
    }

    public void CompleteResponse(OfficialGatePacket packet)
    {
        if (_completed)
        {
            throw new InvalidOperationException("The player-login exchange is already complete.");
        }
        ArgumentNullException.ThrowIfNull(packet);

        if (packet.Message is not PlayerLoginRsp response)
        {
            throw Failure("The Gate did not answer with PlayerLoginRsp.");
        }
        if (response.Retcode != 0)
        {
            throw Failure($"The Gate rejected PlayerLogin with retcode {response.Retcode}.");
        }
        if ((response.TargetUid != 0 && response.TargetUid != _token.PlayerUid)
            || response.IsDataNeedRelogin)
        {
            throw Failure("The PlayerLogin response is inconsistent or requires another login.");
        }

        _completed = true;
    }

    public override string ToString() =>
        $"OfficialPlayerLoginExchange {{ PlayerUid = {_token.PlayerUid}, SessionToken = [REDACTED] }}";

    private static void Validate(OfficialPlayerLoginProfile profile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profile.PlatformName);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile.DeviceInfo);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile.DeviceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile.DeviceUuid);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile.SystemVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile.Checksum);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile.ChecksumClientVersion);

        if (profile.PlatformName.Length > 64
            || profile.DeviceInfo.Length > 512
            || profile.DeviceName.Length > 256
            || profile.DeviceUuid.Length > 128
            || profile.SystemVersion.Length > 128
            || profile.Checksum.Length > 256
            || profile.ChecksumClientVersion.Length > 256
            || profile.ClientVersionHash.Length > 256
            || profile.UserAgent.Length > 1024)
        {
            throw new ArgumentException("The PlayerLogin profile contains an oversized field.", nameof(profile));
        }
    }

    private static OfficialConnectivityException Failure(string message) =>
        new(OfficialConnectivityError.PlayerLoginRejected, message);
}
