using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Starlight.Crypto.Client;
using Starlight.Ec2b;
using Starlight.Kcp;
using Starlight.Protobuf.Core;
using Starlight.Protocol;
using Starlight.Protocol.V70;
using StarlightExporter.Official;
using Xunit;

namespace StarlightExporter.OfficialTests;

public sealed class OfficialGateTests
{
    private static readonly int[] ExpectedTokenTags =
        [2, 3, 6, 7, 9, 116, 476, 578, 588, 726, 932, 986, 1397];
    private static readonly int[] ExpectedLoginSecurityTags =
        [2, 4, 6, 7, 8, 9, 10, 12, 13, 125, 137, 383, 670, 787, 894, 1160, 1174, 1581, 1747];

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
        OfficialCurrentRegion region = Region() with
        {
            SecretKey = Ec2bKeyGen.Create("nested-key-must-not-be-used"),
        };

        byte[] result = OfficialGateKeySchedule.DeriveInitialPad(region);
        byte[] expected = Ec2bHelper.Derive(region.ClientSecretKey);

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
    public void MetadataTraceIsBoundedAndCannotContainPacketBodiesOrSecrets()
    {
        var trace = new GateMetadataTrace();
        var metadata = new OfficialGatePacketMetadata(123, "PlayerDataNotify", 456);
        for (int index = 0; index < GateMetadataTrace.MaximumRecords; index++)
        {
            trace.Add(index, GateTracePhase.InitialSync, GateTraceDirection.ServerToClient, metadata);
        }

        OfficialConnectivityException exception = Assert.Throws<OfficialConnectivityException>(() =>
            trace.Add(4096, GateTracePhase.InitialSync, GateTraceDirection.ServerToClient, metadata));
        string json = JsonSerializer.Serialize(trace.Records[0]);

        Assert.Equal(OfficialConnectivityError.GatePacketInvalid, exception.Error);
        Assert.DoesNotContain("payload", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("protobuf", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ticket", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("seed", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pad", json, StringComparison.OrdinalIgnoreCase);
        Assert.False(trace.Records[0].Chunked);
        Assert.False(trace.Records[0].Duplicate);
        Assert.True(trace.Records[1].Duplicate);
        Assert.Equal("0000000000000000", trace.Records[0].FieldPresenceHash);
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
    public void PlayerLoginResponsePreservesV70Tag592ForPostLoginBranch()
    {
        OfficialCurrentRegion region = Region();
        using OfficialGateCipherState writer = OfficialGateCipherState.FromRegion(region);
        using OfficialGateCipherState reader = OfficialGateCipherState.FromRegion(region);
        var codec = new OfficialGatePacketCodec();
        var registry = new V70ProtocolRegistry();
        using var body = new MemoryStream();
        body.Write(registry.Serialize(new PlayerLoginRsp { TargetUid = 123456789 }));
        using (var output = new CodedOutputStream(body, leaveOpen: true))
        {
            output.WriteTag(592, WireFormat.WireType.Varint);
            output.WriteBool(true);
            output.Flush();
        }

        byte[] packetBytes = new GamePacket(22801, [], body.ToArray()).ToBytes();
        OfficialGatePacket decoded = codec.DecodeEncrypted(writer.Transform(packetBytes), reader);

        Assert.IsType<PlayerLoginRsp>(decoded.Message);
        Assert.True(decoded.PlayerLoginLimitedSocialCache);
    }

    [Fact]
    public void PinnedRsaKeysKeepGatePublicAndDispatchPrivateRolesDistinct()
    {
        using ClientCrypto crypto = ClientCrypto.Create(generateRsaKeys: false);
        using RSA gatePublic = OfficialRsaKeyProfile.ImportGatePublicKey(
            OfficialRsaKeyProfile.OsGlobalV70GatePublicKeyPem);
        RSA dispatchPrivate = crypto.ContentKeys[5];
        string gateFingerprint = Convert.ToHexStringLower(
            SHA256.HashData(gatePublic.ExportSubjectPublicKeyInfo()));
        string dispatchFingerprint = Convert.ToHexStringLower(
            SHA256.HashData(dispatchPrivate.ExportSubjectPublicKeyInfo()));

        Assert.Equal(OfficialRsaKeyProfile.OsGlobalV70GatePublicKeySpkiSha256,
            gateFingerprint);
        Assert.Equal("b640dbc7907dc0588d4ea11ea9ca1c313a8ab6974f5436f6b43dad456c72d84f",
            dispatchFingerprint);
        Assert.NotEqual(gateFingerprint, Convert.ToHexStringLower(
            SHA256.HashData(crypto.SigningKey!.ExportSubjectPublicKeyInfo())));
        Assert.NotEqual(gateFingerprint, dispatchFingerprint);
        Assert.NotEmpty(dispatchPrivate.ExportParameters(includePrivateParameters: true).D!);
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
            SyntheticTokenProfile());

        OfficialGatePacket packet = codec.DecodeEncrypted(exchange.EncodeRequest(codec, writer), reader);

        Assert.Equal("gate-ticket", Assert.IsType<GetPlayerTokenReq>(packet.Message).Ticket);
        Assert.DoesNotContain("gate-ticket", exchange.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultTokenExchangeEncryptsSeedForOfficialGateKey()
    {
        OfficialCurrentRegion region = Region();
        var codec = new OfficialGatePacketCodec();
        using OfficialGateCipherState writer = OfficialGateCipherState.FromRegion(region);
        using OfficialGateCipherState reader = OfficialGateCipherState.FromRegion(region);
        using OfficialPlayerTokenExchange exchange = OfficialPlayerTokenExchange.CreatePinned(
            ComboSession.Create("account-1", "token"), region, OfficialClientProfile.OsGlobalV70);
        using ClientCrypto syntheticServer = ClientCrypto.Create(generateRsaKeys: false);

        OfficialGatePacket packet = codec.DecodeEncrypted(exchange.EncodeRequest(codec, writer), reader);
        byte[] encryptedSeed = Convert.FromBase64String(
            Assert.IsType<GetPlayerTokenReq>(packet.Message).ClientRandKey);

        Assert.False(syntheticServer.TryDecryptWithSigningKey(encryptedSeed, out _));
    }

    [Fact]
    public void PlayerTokenBuilderPreservesInjectedSeedInBigEndianOrder()
    {
        const ulong clientSeed = 0x0102030405060708;
        OfficialCurrentRegion region = Region();
        var codec = new OfficialGatePacketCodec();
        using OfficialGateCipherState writer = OfficialGateCipherState.FromRegion(region);
        using OfficialGateCipherState reader = OfficialGateCipherState.FromRegion(region);
        using OfficialPlayerTokenExchange exchange = OfficialPlayerTokenExchange.CreatePinned(
            ComboSession.Create("account-1", "token"),
            region,
            SyntheticTokenProfile(),
            clientSeed);
        using ClientCrypto server = ClientCrypto.Create(generateRsaKeys: false);

        OfficialGatePacket packet = codec.DecodeEncrypted(exchange.EncodeRequest(codec, writer), reader);
        byte[] encryptedSeed = Convert.FromBase64String(
            Assert.IsType<GetPlayerTokenReq>(packet.Message).ClientRandKey);

        Assert.True(server.TryDecryptWithSigningKey(encryptedSeed, out byte[] clearSeed));
        Assert.Equal("0102030405060708", Convert.ToHexStringLower(clearSeed));
        CryptographicOperations.ZeroMemory(clearSeed);
        CryptographicOperations.ZeroMemory(encryptedSeed);
    }

    [Fact]
    public void PlayerTokenExchangeValidatesResponseAndActivatesRekey()
    {
        OfficialCurrentRegion region = Region(ticket: "gate-ticket");
        ComboSession session = ComboSession.Create(
            "account-1",
            "combo-token-value",
            accountType: 1,
            countryCode: "FR",
            expectedUid: 123456789);
        var codec = new OfficialGatePacketCodec();
        using OfficialGateCipherState clientCipher = OfficialGateCipherState.FromRegion(region);
        using OfficialGateCipherState serverCipher = OfficialGateCipherState.FromRegion(region);
        using OfficialPlayerTokenExchange exchange = OfficialPlayerTokenExchange.CreatePinned(
            session,
            region,
            SyntheticTokenProfile());
        using ClientCrypto serverCrypto = ClientCrypto.Create(generateRsaKeys: false);

        byte[] requestBytes = exchange.EncodeRequest(codec, clientCipher);
        Dictionary<int, int> tokenTags = ReadWireTags(new GamePacket(clientCipher.Transform(requestBytes)).Body);
        Assert.Equal(1, tokenTags[1397]);
        Assert.Equal(1, tokenTags[116]);
        Assert.Equal(1, tokenTags[476]);
        Assert.Equal(1, tokenTags[578]);
        Assert.Equal(
            ExpectedTokenTags,
            tokenTags.Keys.Order());
        // All DEFAULT fields in delivery_1/tables/get_player_token_req.tsv
        // are absent on the normal Windows path, not merely set to zero.
        foreach (int field in new[] { 1, 13, 301, 14, 4, 11, 955, 242, 10, 12, 1514 })
        {
            Assert.DoesNotContain(field, tokenTags.Keys);
        }
        OfficialGatePacket requestPacket = codec.DecodeEncrypted(requestBytes, serverCipher);
        GetPlayerTokenReq request = Assert.IsType<GetPlayerTokenReq>(requestPacket.Message);
        Assert.Equal("account-1", request.AccountUid);
        Assert.Equal("combo-token-value", request.AccountToken);
        Assert.Equal("gate-ticket", request.Ticket);
        Assert.Equal(5u, request.KeyId);
        Assert.Equal(0u, request.Uid);
        Assert.Equal(4u, request.Lang);
        Assert.Equal(3u, request.PlatformType);
        Assert.Equal(1u, request.AccountType);
        Assert.Equal(1u, request.ChannelId);
        Assert.Equal(0u, request.SubChannelId);
        Assert.Equal(string.Empty, request.CountryCode);
        Assert.False(request.IsGuest);
        Assert.NotEmpty(request.ClientRandKey);

        byte[] clientSeedCipher = Convert.FromBase64String(request.ClientRandKey);
        Assert.True(serverCrypto.TryDecryptWithSigningKey(clientSeedCipher, out byte[] clientSeedBytes));
        ulong clientSeed = BinaryPrimitives.ReadUInt64BigEndian(clientSeedBytes);
        const ulong serverSeed = 987654321;
        byte[] combinedSeed = new byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(combinedSeed, clientSeed ^ serverSeed);
        Assert.True(serverCrypto.TryEncryptPayload(combinedSeed, 5, out string serverRandKey));
        var response = new GetPlayerTokenRsp
        {
            Uid = 123456789,
            AccountUid = "account-1",
            Token = "session-token",
            ClientVersionRandomKey = "synthetic-random-key",
            SecurityCmdBuffer = ByteString.CopyFrom(new byte[] { 1, 2, 3 }),
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
        byte[] loginBytes = login.EncodeRequest(codec, clientCipher);
        Dictionary<int, int> loginTags = ReadWireTags(new GamePacket(clientCipher.Transform(loginBytes)).Body);
        Assert.Equal(1, loginTags[1747]);
        Assert.Equal(1, loginTags[787]);
        Assert.Equal(1, loginTags[383]);
        Assert.Equal(1, loginTags[894]);
        Assert.Equal(1, loginTags[137]);
        Assert.Equal(
            ExpectedLoginSecurityTags,
            loginTags.Keys.Order());
        Assert.Equal((1920, 1080), ReadVector2Int(
            new GamePacket(clientCipher.Transform(loginBytes)).Body, 137));
        // Exhausts the DEFAULT and CLOUD_ONLY rows of the 54-field matrix.
        foreach (int field in new[]
        {
            633, 758, 1033, 1989, 367, 5, 14, 895, 306, 1129, 1061,
            318, 63, 499, 3, 15, 1, 1084, 1531, 669, 11, 1051, 1313,
            257, 755, 516, 1640, 1237,
        })
        {
            Assert.DoesNotContain(field, loginTags.Keys);
        }
        OfficialGatePacket loginPacket = codec.DecodeEncrypted(loginBytes, serverCipher);
        PlayerLoginReq loginRequest = Assert.IsType<PlayerLoginReq>(loginPacket.Message);
        Assert.Equal("session-token", loginRequest.Token);
        Assert.Equal("OSRELWin7.0.0", loginRequest.ClientVersion);
        Assert.Equal(string.Empty, loginRequest.Platform);
        Assert.Equal("synthetic-device", loginRequest.DeviceInfo);
        Assert.Equal("synthetic-name", loginRequest.DeviceName);
        Assert.Equal("synthetic-uuid", loginRequest.DeviceUuid);
        Assert.Equal("Windows 11", loginRequest.SystemVersion);
        Assert.Equal(string.Empty, loginRequest.Checksum);
        Assert.Equal("7.0.0", loginRequest.ChecksumClientVersion);
        Assert.Equal("gzVWWBzx5Wdh9TgiBRmFt4McXx0=", loginRequest.ClientVersionHash);
        Assert.Equal(new byte[] { 1, 2, 3 }, loginRequest.SecurityCmdReply.ToByteArray());
        Assert.Equal("1030d792fc42_", loginRequest.UaPc);
        Assert.Equal(0u, loginRequest.TargetUid);
        Assert.Equal(70u, loginRequest.ClientDataVersion);
        Assert.Equal("hoyoverse", loginRequest.Cps);
        Assert.Equal(1u, loginRequest.AccountType);
        Assert.Equal(string.Empty, loginRequest.AccountUid);
        Assert.Equal(0u, loginRequest.ChannelId);
        Assert.Equal(0ul, loginRequest.LoginRand);

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
            SyntheticTokenProfile());
        byte[] responseBytes = codec.EncodeEncrypted(new GetPlayerTokenRsp
        {
            Retcode = -201,
            AccountUid = "account-1",
            KeyId = 5,
        }, cipher);
        OfficialGatePacket response = codec.DecodeEncrypted(responseBytes, cipher);

        OfficialConnectivityException exception = Assert.Throws<OfficialConnectivityException>(() =>
            exchange.CompleteResponse(response, cipher));

        Assert.Equal(OfficialConnectivityError.PlayerTokenRejected, exception.Error);
        Assert.Equal(-201, exception.Retcode);
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
            SyntheticTokenProfile());
        using ClientCrypto serverCrypto = ClientCrypto.Create(generateRsaKeys: false);

        OfficialGatePacket requestPacket = codec.DecodeEncrypted(
            exchange.EncodeRequest(codec, clientCipher),
            serverCipher);
        GetPlayerTokenReq request = Assert.IsType<GetPlayerTokenReq>(requestPacket.Message);
        byte[] clientSeedCipher = Convert.FromBase64String(request.ClientRandKey);
        Assert.True(serverCrypto.TryDecryptWithSigningKey(clientSeedCipher, out byte[] clientSeed));
        Assert.True(serverCrypto.TryEncryptPayload(clientSeed, 5, out string serverRandKey));
        byte[] responseBytes = codec.EncodeEncrypted(new GetPlayerTokenRsp
        {
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

    private static OfficialClientProfile SyntheticTokenProfile()
    {
        using ClientCrypto server = ClientCrypto.Create(generateRsaKeys: false);
        return OfficialClientProfile.OsGlobalV70 with
        {
            GateServerPublicKeyPem = server.SigningKey!.ExportSubjectPublicKeyInfoPem(),
        };
    }

    private static OfficialCurrentRegion Region(string seed = "gate-test", string ticket = "")
    {
        byte[] secret = Ec2bKeyGen.Create(seed);
        return new OfficialCurrentRegion
        {
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

    private static OfficialPlayerLoginProfile LoginProfile() => new()
    {
        DeviceInfo = "synthetic-device",
        DeviceName = "synthetic-name",
        DeviceUuid = "synthetic-uuid",
        SystemVersion = "Windows 11",
        DeviceFingerprint = "1234567890",
        SecurityLibraryMd5 = "0123456789abcdef0123456789abcdef",
        ScreenWidth = 1920,
        ScreenHeight = 1080,
    };

    private static Dictionary<int, int> ReadWireTags(byte[] body)
    {
        var tags = new Dictionary<int, int>();
        using var input = new CodedInputStream(body);
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            int number = WireFormat.GetTagFieldNumber(tag);
            tags[number] = tags.GetValueOrDefault(number) + 1;
            input.SkipLastField();
        }
        return tags;
    }

    private static (int X, int Y) ReadVector2Int(byte[] body, int expectedField)
    {
        using var input = new CodedInputStream(body);
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            if (WireFormat.GetTagFieldNumber(tag) == expectedField)
            {
                using var nested = new CodedInputStream(input.ReadBytes().ToByteArray());
                int x = 0;
                int y = 0;
                uint nestedTag;
                while ((nestedTag = nested.ReadTag()) != 0)
                {
                    switch (WireFormat.GetTagFieldNumber(nestedTag))
                    {
                        case 1:
                            x = nested.ReadInt32();
                            break;
                        case 2:
                            y = nested.ReadInt32();
                            break;
                        default:
                            nested.SkipLastField();
                            break;
                    }
                }
                return (x, y);
            }
            input.SkipLastField();
        }
        throw new InvalidDataException("The expected Vector2Int field is absent.");
    }
}
