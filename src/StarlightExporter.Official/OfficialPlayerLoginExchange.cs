using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Starlight.Protocol;

namespace StarlightExporter.Official;

public sealed record OfficialPlayerLoginProfile
{
    public required string DeviceInfo { get; init; }
    public required string DeviceName { get; init; }
    public required string DeviceUuid { get; init; }
    public required string SystemVersion { get; init; }
    public string DeviceFingerprint { get; init; } = string.Empty;
    public string SecurityLibraryMd5 { get; init; } = string.Empty;
    public int ScreenWidth { get; init; }
    public int ScreenHeight { get; init; }

    public override string ToString() => "OfficialPlayerLoginProfile { Device = [REDACTED] }";
}

public sealed class OfficialPlayerLoginExchange
{
    private readonly OfficialCurrentRegion _region;
    private readonly OfficialClientProfile _client;
    private readonly OfficialPlayerLoginProfile _login;
    private readonly OfficialPlayerTokenResult _token;
    private readonly TimeProvider _timeProvider;
    private bool _completed;

    public OfficialGatePacketMetadata? RequestMetadata { get; private set; }

    public OfficialPlayerLoginExchange(
        ComboSession session,
        OfficialCurrentRegion region,
        OfficialClientProfile client,
        OfficialPlayerLoginProfile login,
        OfficialPlayerTokenResult token,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(login);
        ArgumentNullException.ThrowIfNull(token);
        Validate(login);

        _region = region;
        _client = client;
        _login = login;
        _token = token;
        _timeProvider = timeProvider ?? TimeProvider.System;
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

        string randomKey = _token.ClientVersionRandomKey.Reveal();
        if (string.IsNullOrEmpty(randomKey))
        {
            throw Failure("GetPlayerToken did not provide a client version random key.");
        }
        byte[] versionMaterial = Encoding.ASCII.GetBytes(_client.Version + randomKey + "mhy2020");
        string versionHash;
        try
        {
            // The pinned V70 login contract specifies SHA-1 for this version marker.
#pragma warning disable CA5350
            versionHash = Convert.ToBase64String(SHA1.HashData(versionMaterial));
#pragma warning restore CA5350
        }
        finally
        {
            CryptographicOperations.ZeroMemory(versionMaterial);
        }
        uint timestamp = unchecked((uint)_timeProvider.GetUtcNow().ToUnixTimeMilliseconds());

        var request = new PlayerLoginReq
        {
            Token = _token.SessionToken.Reveal(),
            Cps = _client.Cps,
            Checksum = string.Empty,
            UaPc = _client.Uapc,
            Platform = string.Empty,
            ClientVersion = _client.Version,
            DeviceInfo = _login.DeviceInfo,
            DeviceName = _login.DeviceName,
            ClientVersionHash = versionHash,
            DeviceUuid = _login.DeviceUuid,
            CountryCode = string.Empty,
            AccountUid = string.Empty,
            ChecksumClientVersion = _client.GameVersion,
            SystemVersion = _login.SystemVersion,
            ChannelId = 0,
            LanguageType = _client.Language,
            SubChannelId = _client.SubChannelId,
            AccountType = _client.ChannelId,
            TargetUid = 0,
            LoginRand = 0,
            IsGuest = false,
            PlatformType = _client.Platform,
            ClientDataVersion = _region.ClientDataVersion,
            RegPlatform = 0,
            SecurityLibraryMd5 = _login.SecurityLibraryMd5,
            SecurityCmdReply = ByteString.CopyFrom(_token.SecurityCommandBuffer),
        };
        RequestMetadata = codec.Describe(request, deviceFingerprint: _login.DeviceFingerprint,
            loginTimestamp: timestamp, screenWidth: _login.ScreenWidth,
            screenHeight: _login.ScreenHeight);
        return codec.EncodeEncrypted(request, cipher, metadata,
            deviceFingerprint: _login.DeviceFingerprint, loginTimestamp: timestamp,
            screenWidth: _login.ScreenWidth, screenHeight: _login.ScreenHeight);
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
            throw new OfficialConnectivityException(
                OfficialConnectivityError.PlayerLoginRejected,
                $"The Gate rejected PlayerLogin with retcode {response.Retcode}.",
                retcode: response.Retcode);
        }
        if ((response.TargetUid != 0 && response.TargetUid != _token.PlayerUid)
            || response.IsDataNeedRelogin)
        {
            throw Failure("The PlayerLogin response is inconsistent or requires another login.");
        }

        _completed = true;
    }

    public override string ToString() =>
        "OfficialPlayerLoginExchange { PlayerUid = [REDACTED], SessionToken = [REDACTED] }";

    private static void Validate(OfficialPlayerLoginProfile profile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profile.DeviceInfo);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile.DeviceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile.DeviceUuid);
        ArgumentException.ThrowIfNullOrWhiteSpace(profile.SystemVersion);

        if (profile.DeviceInfo.Length > 512
            || profile.DeviceName.Length > 256
            || profile.DeviceUuid.Length > 128
            || profile.SystemVersion.Length > 128
            || profile.DeviceFingerprint.Length > 32
            || profile.SecurityLibraryMd5.Length > 32)
        {
            throw new ArgumentException("The PlayerLogin profile contains an oversized field.", nameof(profile));
        }
        if (profile.ScreenWidth <= 0 || profile.ScreenHeight <= 0)
        {
            throw new ArgumentException("The PlayerLogin display dimensions are invalid.", nameof(profile));
        }
    }

    private static OfficialConnectivityException Failure(string message) =>
        new(OfficialConnectivityError.PlayerLoginRejected, message);
}
