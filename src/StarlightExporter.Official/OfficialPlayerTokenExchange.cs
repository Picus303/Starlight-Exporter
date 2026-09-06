using System.Buffers.Binary;
using System.Security.Cryptography;
using Starlight.Crypto.Client;
using Starlight.Protocol;

namespace StarlightExporter.Official;

public sealed class OfficialPlayerTokenResult
{
    internal OfficialPlayerTokenResult(uint playerUid, string sessionToken)
    {
        PlayerUid = playerUid;
        SessionToken = OfficialSecret.Create(sessionToken);
    }

    public uint PlayerUid { get; }
    internal OfficialSecret SessionToken { get; }

    public override string ToString() =>
        $"OfficialPlayerTokenResult {{ PlayerUid = {PlayerUid}, SessionToken = [REDACTED] }}";
}

public sealed class OfficialPlayerTokenExchange : IDisposable
{
    private readonly ClientCrypto _crypto;
    private readonly ComboSession _session;
    private readonly OfficialCurrentRegion _region;
    private readonly OfficialClientProfile _profile;
    private ulong _clientSeed;
    private bool _completed;
    private bool _disposed;

    public OfficialGatePacketMetadata? RequestMetadata { get; private set; }

    private OfficialPlayerTokenExchange(
        ClientCrypto crypto,
        ComboSession session,
        OfficialCurrentRegion region,
        OfficialClientProfile profile,
        ulong clientSeed)
    {
        _crypto = crypto;
        _session = session;
        _region = region;
        _profile = profile;
        _clientSeed = clientSeed;
    }

    public static OfficialPlayerTokenExchange CreatePinned(
        ComboSession session,
        OfficialCurrentRegion region,
        OfficialClientProfile profile)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.KeyId is 0 or > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(profile), "The client key ID is invalid.");
        }
        Span<byte> seed = stackalloc byte[sizeof(ulong)];
        RandomNumberGenerator.Fill(seed);
        ulong clientSeed = BinaryPrimitives.ReadUInt64BigEndian(seed);
        CryptographicOperations.ZeroMemory(seed);
        return new OfficialPlayerTokenExchange(
            ClientCrypto.Create(generateRsaKeys: false),
            session,
            region,
            profile,
            clientSeed);
    }

    public byte[] EncodeRequest(
        OfficialGatePacketCodec codec,
        OfficialGateCipherState cipher,
        PacketHead? metadata = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed)
        {
            throw new InvalidOperationException("The player-token exchange is already complete.");
        }
        ArgumentNullException.ThrowIfNull(codec);
        ArgumentNullException.ThrowIfNull(cipher);

        RSA? signingKey = _crypto.SigningKey;
        if (signingKey is null)
        {
            throw CryptoFailure("The pinned client random-key RSA key is unavailable.");
        }

        Span<byte> seed = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(seed, _clientSeed);
        byte[] encryptedSeed;
        try
        {
            encryptedSeed = signingKey.Encrypt(seed, RSAEncryptionPadding.Pkcs1);
        }
        catch (CryptographicException exception)
        {
            throw CryptoFailure("The client random key cannot be encrypted.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
        }

        try
        {
            var request = new GetPlayerTokenReq
            {
                Ticket = _region.ConnectGateTicket.Reveal(),
                ClientRandKey = Convert.ToBase64String(encryptedSeed),
                AccountUid = _session.AccountUid,
                AccountToken = _session.AccountToken.Reveal(),
                Lang = _profile.Language,
                AccountType = _session.AccountType,
                Uid = _session.ExpectedUid ?? 0,
                PlatformType = _profile.Platform,
                KeyId = _profile.KeyId,
                IsGuest = _session.IsGuest,
                ChannelId = _profile.ChannelId,
                SubChannelId = _profile.SubChannelId,
                CountryCode = _session.CountryCode,
            };
            RequestMetadata = codec.Describe(request);
            return codec.EncodeEncrypted(request, cipher, metadata);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encryptedSeed);
        }
    }

    public OfficialPlayerTokenResult CompleteResponse(
        OfficialGatePacket packet,
        OfficialGateCipherState cipher)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed)
        {
            throw new InvalidOperationException("The player-token exchange is already complete.");
        }
        ArgumentNullException.ThrowIfNull(packet);
        ArgumentNullException.ThrowIfNull(cipher);

        if (packet.Message is not GetPlayerTokenRsp response)
        {
            throw new OfficialConnectivityException(
                OfficialConnectivityError.PlayerTokenRejected,
                "The Gate did not answer with GetPlayerTokenRsp.");
        }
        if (response.Retcode != 0)
        {
            throw new OfficialConnectivityException(
                OfficialConnectivityError.PlayerTokenRejected,
                $"The Gate rejected GetPlayerToken with retcode {response.Retcode}.");
        }
        if (response.Uid == 0
            || response.KeyId != _profile.KeyId
            || !string.Equals(response.AccountUid, _session.AccountUid, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(response.Token)
            || response.Token.Length > 4096
            || (_session.ExpectedUid is { } expectedUid && response.Uid != expectedUid))
        {
            throw new OfficialConnectivityException(
                OfficialConnectivityError.PlayerTokenRejected,
                "The GetPlayerToken response identity or key ID is inconsistent.");
        }

        byte[] encryptedCombinedSeed = [];
        byte[] signature = [];
        byte[] combinedSeed = [];
        byte[] sessionPad = [];

        try
        {
            encryptedCombinedSeed = DecodeBase64(
                response.ServerRandKey,
                "The server random key is invalid.");
            signature = DecodeBase64(response.Sign, "The server random-key signature is invalid.");
            if (!_crypto.TryDecryptContent((int)_profile.KeyId, encryptedCombinedSeed, out combinedSeed)
                || combinedSeed.Length != sizeof(ulong))
            {
                throw CryptoFailure("The server random key cannot be decrypted.");
            }

            RSA? signingKey = _crypto.SigningKey;
            if (signingKey is null
                || signature.Length != signingKey.KeySize / 8
                || !signingKey.VerifyData(
                    combinedSeed,
                    signature,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1))
            {
                throw CryptoFailure("The server random-key signature is invalid.");
            }

            ulong combined = BinaryPrimitives.ReadUInt64BigEndian(combinedSeed);
            ulong serverSeed = _clientSeed ^ combined;
            sessionPad = OfficialGateKeySchedule.GenerateSessionPad(serverSeed);
            cipher.ActivateSessionPadAfterTokenResponse(sessionPad);
            _clientSeed = 0;
            _completed = true;
            return new OfficialPlayerTokenResult(response.Uid, response.Token);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encryptedCombinedSeed);
            CryptographicOperations.ZeroMemory(signature);
            CryptographicOperations.ZeroMemory(combinedSeed);
            CryptographicOperations.ZeroMemory(sessionPad);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _clientSeed = 0;
        _crypto.Dispose();
        _disposed = true;
    }

    public override string ToString() =>
        $"OfficialPlayerTokenExchange {{ AccountUid = [REDACTED], KeyId = {_profile.KeyId}, Secrets = [REDACTED] }}";

    private static byte[] DecodeBase64(string value, string message)
    {
        try
        {
            return Convert.FromBase64String(value);
        }
        catch (FormatException exception)
        {
            throw CryptoFailure(message, exception);
        }
    }

    private static OfficialConnectivityException CryptoFailure(
        string message,
        Exception? innerException = null) =>
        new(OfficialConnectivityError.SessionRekeyFailed, message, innerException);
}
