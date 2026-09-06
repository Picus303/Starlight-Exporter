using Starlight.Crypto.Client;
using Starlight.Ec2b;
using Starlight.Kcp;
using Starlight.Protocol;
using StarlightExporter.Official;
using System.Buffers.Binary;
using System.Security.Cryptography;
using Xunit;

namespace StarlightExporter.OfficialTests;

public sealed class OfficialGateTests
{
    [Fact]
    public void HandshakeUsesPinnedStarlightWireTypes()
    {
        byte[] connect = OfficialGateHandshake.CreateConnect();
        Assert.IsType<ConnectHandshake>(Handshake.Parse(connect));

        var expected = new ExchangeHandshake(conv: 123, token: 456);
        OfficialGateConnection connection = OfficialGateHandshake.ParseExchange(expected.ToByteArray());

        Assert.Equal(123u, connection.ConversationId);
        Assert.Equal(456u, connection.Token);
        Assert.DoesNotContain("456", connection.ToString(), StringComparison.Ordinal);
        byte[] disconnect = OfficialGateHandshake.CreateDisconnect(connection);
        DisconnectHandshake parsed = Assert.IsType<DisconnectHandshake>(Handshake.Parse(disconnect));
        Assert.Equal(DisconnectReason.ClientClose, parsed.Reason);
    }

    [Fact]
    public void HandshakeRejectsInvalidOrZeroExchange()
    {
        OfficialConnectivityException invalid = Assert.Throws<OfficialConnectivityException>(() =>
            OfficialGateHandshake.ParseExchange(new byte[20]));
        OfficialConnectivityException zero = Assert.Throws<OfficialConnectivityException>(() =>
            OfficialGateHandshake.ParseExchange(new ExchangeHandshake(conv: 0, token: 1).ToByteArray()));

        Assert.Equal(OfficialConnectivityError.GateHandshakeInvalid, invalid.Error);
        Assert.Equal(OfficialConnectivityError.GateHandshakeInvalid, zero.Error);
    }

    [Fact]
    public void InitialPadUsesStarlightEc2bDerivation()
    {
        OfficialCurrentRegion region = Region();

        byte[] result = OfficialGateKeySchedule.DeriveInitialPad(region);
        byte[] expected = Ec2bHelper.Derive(region.SecretKey);

        Assert.Equal(OfficialGateKeySchedule.PadLength, result.Length);
        Assert.Equal(expected, result);
        CryptographicOperations.ZeroMemory(result);
        CryptographicOperations.ZeroMemory(expected);
    }

    [Fact]
    public void CipherRoundTripsAndSwitchesAtomicallyToSessionPad()
    {
        using OfficialGateCipherState subject = OfficialGateCipherState.FromRegion(Region());
        byte[] plaintext = "gate-packet"u8.ToArray();
        byte[] initialCiphertext = subject.Transform(plaintext);

        Assert.Equal(plaintext, subject.Transform(initialCiphertext));

        byte[] nextPad = OfficialGateKeySchedule.GenerateSessionPad(123456789);
        subject.ActivateSessionPadAfterTokenResponse(nextPad);
        byte[] sessionCiphertext = subject.Transform(plaintext);

        Assert.NotEqual(initialCiphertext, sessionCiphertext);
        Assert.Equal(plaintext, subject.Transform(sessionCiphertext));
        Assert.NotEqual(plaintext, subject.Transform(initialCiphertext));
        CryptographicOperations.ZeroMemory(nextPad);
    }

    [Fact]
    public void V70PacketCodecRoundTripsWithoutHardCodedCommandId()
    {
        using OfficialGateCipherState cipher = OfficialGateCipherState.FromRegion(Region());
        var codec = new OfficialGatePacketCodec();
        var message = new PlayerDataNotify { NickName = "Traveler" };
        var metadata = new PacketHead { ClientSequenceId = 42, SentMs = 1234 };

        byte[] encrypted = codec.EncodeEncrypted(message, cipher, metadata);
        OfficialGatePacket decoded = codec.DecodeEncrypted(encrypted, cipher);

        Assert.Equal("V70", codec.ProtocolVersion);
        Assert.Equal(42u, decoded.Metadata.ClientSequenceId);
        Assert.Equal(1234u, decoded.Metadata.SentMs);
        Assert.Equal("Traveler", Assert.IsType<PlayerDataNotify>(decoded.Message).NickName);
        Assert.DoesNotContain("Traveler", decoded.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void PacketCodecRejectsWrongPadWithStableError()
    {
        using OfficialGateCipherState writer = OfficialGateCipherState.FromRegion(Region("writer"));
        using OfficialGateCipherState reader = OfficialGateCipherState.FromRegion(Region("reader"));
        var codec = new OfficialGatePacketCodec();
        byte[] encrypted = codec.EncodeEncrypted(new PlayerDataNotify { NickName = "private-name" }, writer);

        OfficialConnectivityException exception = Assert.Throws<OfficialConnectivityException>(() =>
            codec.DecodeEncrypted(encrypted, reader));

        Assert.Equal(OfficialConnectivityError.GatePacketInvalid, exception.Error);
        Assert.DoesNotContain("private-name", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ComboSessionStringRepresentationRedactsToken()
    {
        ComboSession session = ComboSession.Create("account-1", "combo-token-value", expectedUid: 123456789);

        Assert.DoesNotContain("account-1", session.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("combo-token-value", session.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void PlayerTokenExchangePreservesGateTicketThroughPinnedV70Registry()
    {
        OfficialCurrentRegion region = Region(ticket: "gate-ticket");
        var codec = new OfficialGatePacketCodec();
        using OfficialGateCipherState writer = OfficialGateCipherState.FromRegion(region);
        using OfficialGateCipherState reader = OfficialGateCipherState.FromRegion(region);
        using OfficialPlayerTokenExchange exchange = OfficialPlayerTokenExchange.CreatePinned(
            ComboSession.Create("account-1", "token"),
            region,
            OfficialClientProfile.OsGlobalV70);

        OfficialGatePacket packet = codec.DecodeEncrypted(exchange.EncodeRequest(codec, writer), reader);

        Assert.Equal("gate-ticket", Assert.IsType<GetPlayerTokenReq>(packet.Message).Ticket);
        Assert.DoesNotContain("gate-ticket", exchange.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void PlayerTokenExchangeValidatesResponseAndActivatesRekey()
    {
        OfficialCurrentRegion region = Region();
        ComboSession session = ComboSession.Create(
            "account-1",
            "combo-token-value",
            countryCode: "FR",
            expectedUid: 123456789);
        var codec = new OfficialGatePacketCodec();
        using OfficialGateCipherState clientCipher = OfficialGateCipherState.FromRegion(region);
        using OfficialGateCipherState serverCipher = OfficialGateCipherState.FromRegion(region);
        using OfficialPlayerTokenExchange exchange = OfficialPlayerTokenExchange.CreatePinned(
            session,
            region,
            OfficialClientProfile.OsGlobalV70);
        using ClientCrypto serverCrypto = ClientCrypto.Create(generateRsaKeys: false);

        byte[] requestBytes = exchange.EncodeRequest(codec, clientCipher);
        OfficialGatePacket requestPacket = codec.DecodeEncrypted(requestBytes, serverCipher);
        GetPlayerTokenReq request = Assert.IsType<GetPlayerTokenReq>(requestPacket.Message);
        Assert.Equal("account-1", request.AccountUid);
        Assert.Equal("combo-token-value", request.AccountToken);
        Assert.Equal(string.Empty, request.Ticket);
        Assert.Equal(5u, request.KeyId);
        Assert.Equal(123456789u, request.Uid);
        Assert.Equal(2u, request.Lang);
        Assert.Equal(3u, request.PlatformType);
        Assert.Equal(1u, request.AccountType);
        Assert.Equal(1u, request.ChannelId);
        Assert.Equal(3u, request.SubChannelId);
        Assert.Equal("FR", request.CountryCode);
        Assert.False(request.IsGuest);
        Assert.NotEmpty(request.ClientRandKey);

        byte[] clientSeedCipher = Convert.FromBase64String(request.ClientRandKey);
        Assert.True(serverCrypto.TryDecryptWithSigningKey(clientSeedCipher, out byte[] clientSeedBytes));
        ulong clientSeed = BinaryPrimitives.ReadUInt64BigEndian(clientSeedBytes);
        const ulong serverSeed = 987654321;
        byte[] combinedSeed = new byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(combinedSeed, clientSeed ^ serverSeed);
        Assert.True(serverCrypto.TryEncryptPayload(combinedSeed, 5, out string serverRandKey));
        var response = new GetPlayerTokenRsp {
            Uid = 123456789,
            AccountUid = "account-1",
            Token = "session-token",
            KeyId = 5,
            ServerRandKey = serverRandKey,
            Sign = serverCrypto.GenerateSignature(combinedSeed),
        };
        byte[] responseBytes = codec.EncodeEncrypted(response, serverCipher);
        OfficialGatePacket responsePacket = codec.DecodeEncrypted(responseBytes, clientCipher);

        OfficialPlayerTokenResult result = exchange.CompleteResponse(responsePacket, clientCipher);

        Assert.Equal(123456789u, result.PlayerUid);
        Assert.DoesNotContain("session-token", result.ToString(), StringComparison.Ordinal);
        byte[] negotiatedPad = OfficialGateKeySchedule.GenerateSessionPad(serverSeed);
        serverCipher.ActivateSessionPadAfterTokenResponse(negotiatedPad);
        byte[] nextPacket = codec.EncodeEncrypted(new PlayerDataNotify { NickName = "After rekey" }, serverCipher);
        OfficialGatePacket decodedNext = codec.DecodeEncrypted(nextPacket, clientCipher);
        Assert.Equal("After rekey", Assert.IsType<PlayerDataNotify>(decodedNext.Message).NickName);
        Assert.DoesNotContain("combo-token-value", exchange.ToString(), StringComparison.Ordinal);

        var login = new OfficialPlayerLoginExchange(
            session,
            region,
            OfficialClientProfile.OsGlobalV70,
            LoginProfile(),
            result);
        OfficialGatePacket loginPacket = codec.DecodeEncrypted(
            login.EncodeRequest(codec, clientCipher),
            serverCipher);
        PlayerLoginReq loginRequest = Assert.IsType<PlayerLoginReq>(loginPacket.Message);
        Assert.Equal("session-token", loginRequest.Token);
        Assert.Equal("OSRELWin7.0.0", loginRequest.ClientVersion);
        Assert.Equal("Windows", loginRequest.Platform);
        Assert.Equal("synthetic-device", loginRequest.DeviceInfo);
        Assert.Equal("synthetic-name", loginRequest.DeviceName);
        Assert.Equal("synthetic-uuid", loginRequest.DeviceUuid);
        Assert.Equal("Windows 11", loginRequest.SystemVersion);
        Assert.Equal("checksum", loginRequest.Checksum);
        Assert.Equal("version-checksum", loginRequest.ChecksumClientVersion);
        Assert.Equal("version-hash", loginRequest.ClientVersionHash);
        Assert.Equal("user-agent", loginRequest.UaPc);
        Assert.Equal(123456789u, loginRequest.TargetUid);
        Assert.Equal(70u, loginRequest.ClientDataVersion);
        Assert.NotEqual(0ul, loginRequest.LoginRand);

        OfficialGatePacket loginResponse = codec.DecodeEncrypted(
            codec.EncodeEncrypted(new PlayerLoginRsp { TargetUid = 123456789 }, serverCipher),
            clientCipher);
        login.CompleteResponse(loginResponse);
        Assert.DoesNotContain("session-token", login.ToString(), StringComparison.Ordinal);

        CryptographicOperations.ZeroMemory(clientSeedCipher);
        CryptographicOperations.ZeroMemory(clientSeedBytes);
        CryptographicOperations.ZeroMemory(combinedSeed);
        CryptographicOperations.ZeroMemory(negotiatedPad);
    }

    [Fact]
    public void PlayerTokenExchangeRejectsRetcodeBeforeRekey()
    {
        OfficialCurrentRegion region = Region();
        var codec = new OfficialGatePacketCodec();
        using OfficialGateCipherState cipher = OfficialGateCipherState.FromRegion(region);
        using OfficialPlayerTokenExchange exchange = OfficialPlayerTokenExchange.CreatePinned(
            ComboSession.Create("account-1", "token"),
            region,
            OfficialClientProfile.OsGlobalV70);
        byte[] responseBytes = codec.EncodeEncrypted(new GetPlayerTokenRsp {
            Retcode = -201,
            AccountUid = "account-1",
            KeyId = 5,
        }, cipher);
        OfficialGatePacket response = codec.DecodeEncrypted(responseBytes, cipher);

        OfficialConnectivityException exception = Assert.Throws<OfficialConnectivityException>(() =>
            exchange.CompleteResponse(response, cipher));

        Assert.Equal(OfficialConnectivityError.PlayerTokenRejected, exception.Error);
        Assert.Contains("retcode -201", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PlayerTokenExchangeRejectsInvalidSignatureWithoutChangingPad()
    {
        OfficialCurrentRegion region = Region();
        var codec = new OfficialGatePacketCodec();
        using OfficialGateCipherState clientCipher = OfficialGateCipherState.FromRegion(region);
        using OfficialGateCipherState serverCipher = OfficialGateCipherState.FromRegion(region);
        using OfficialPlayerTokenExchange exchange = OfficialPlayerTokenExchange.CreatePinned(
            ComboSession.Create("account-1", "token", expectedUid: 123456789),
            region,
            OfficialClientProfile.OsGlobalV70);
        using ClientCrypto serverCrypto = ClientCrypto.Create(generateRsaKeys: false);

        OfficialGatePacket requestPacket = codec.DecodeEncrypted(
            exchange.EncodeRequest(codec, clientCipher),
            serverCipher);
        GetPlayerTokenReq request = Assert.IsType<GetPlayerTokenReq>(requestPacket.Message);
        byte[] clientSeedCipher = Convert.FromBase64String(request.ClientRandKey);
        Assert.True(serverCrypto.TryDecryptWithSigningKey(clientSeedCipher, out byte[] clientSeed));
        Assert.True(serverCrypto.TryEncryptPayload(clientSeed, 5, out string serverRandKey));
        byte[] responseBytes = codec.EncodeEncrypted(new GetPlayerTokenRsp {
            Uid = 123456789,
            AccountUid = "account-1",
            Token = "session-token",
            KeyId = 5,
            ServerRandKey = serverRandKey,
            Sign = Convert.ToBase64String(new byte[256]),
        }, serverCipher);
        OfficialGatePacket response = codec.DecodeEncrypted(responseBytes, clientCipher);

        OfficialConnectivityException exception = Assert.Throws<OfficialConnectivityException>(() =>
            exchange.CompleteResponse(response, clientCipher));

        Assert.Equal(OfficialConnectivityError.SessionRekeyFailed, exception.Error);
        byte[] stillInitial = codec.EncodeEncrypted(new PlayerDataNotify { NickName = "Initial" }, serverCipher);
        Assert.IsType<PlayerDataNotify>(codec.DecodeEncrypted(stillInitial, clientCipher).Message);
        CryptographicOperations.ZeroMemory(clientSeedCipher);
        CryptographicOperations.ZeroMemory(clientSeed);
    }

    private static OfficialCurrentRegion Region(string seed = "gate-test", string ticket = "")
    {
        byte[] secret = Ec2bKeyGen.Create(seed);
        return new OfficialCurrentRegion {
            RegionName = "os_euro",
            GateServerIp = "192.0.2.1",
            GateServerPort = 22102,
            UseGateServerDomainName = true,
            GateServerDomainName = "gate.example.test",
            ClientSecretKey = secret.ToArray(),
            SecretKey = secret,
            ConnectGateTicket = OfficialSecret.Create(ticket),
            ClientDataVersion = 70,
            ClientSilenceDataVersion = 71,
            ClientDataMd5 = "data-md5",
            ClientSilenceDataMd5 = "silence-md5",
            ClientVersionSuffix = "suffix",
            ClientSilenceVersionSuffix = "silence-suffix",
            GameBiz = "hk4e_global",
            ResourceUrl = "https://resources.example.test/",
            DataUrl = "https://data.example.test/",
        };
    }

    private static OfficialPlayerLoginProfile LoginProfile() => new() {
        PlatformName = "Windows",
        DeviceInfo = "synthetic-device",
        DeviceName = "synthetic-name",
        DeviceUuid = "synthetic-uuid",
        SystemVersion = "Windows 11",
        Checksum = "checksum",
        ChecksumClientVersion = "version-checksum",
        ClientVersionHash = "version-hash",
        UserAgent = "user-agent",
    };
}
