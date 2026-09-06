using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Starlight.Crypto.Client;
using Starlight.Ec2b;
using Starlight.Kcp;
using Starlight.Protocol;
using StarlightExporter.Official;
using StarlightExporter.Snapshot;
using Xunit;

namespace StarlightExporter.OfficialTests;

public sealed class LiveGateMessageSourceTests
{
    [Fact]
    public async Task TokenProbeStopsAfterRekeyAndDisconnectsCleanly()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        OfficialCurrentRegion region = Region(port);
        Task serverTask = RunProbeGateServerAsync(
            server,
            region,
            expectLogin: false,
            timeout.Token);

        GateTokenProbeResult result = await OfficialGateProbeClient.ProbeTokenAsync(
            ComboSession.Create("account-1", "combo-token", expectedUid: 123456789),
            region,
            OfficialClientProfile.OsGlobalV70,
            ProbeOptions(),
            timeout.Token);
        await serverTask;

        Assert.True(result.HandshakeSucceeded);
        Assert.True(result.SessionRekeySucceeded);
        Assert.True(result.PlayerUidMatchesExpected);
        Assert.Equal(2, result.Trace.Count);
        Assert.Equal("GetPlayerTokenReq", result.Trace[0].MessageType);
        Assert.Equal("GetPlayerTokenRsp", result.Trace[1].MessageType);
        Assert.DoesNotContain(result.Trace, record => record.MessageType == "PlayerLoginReq");
    }

    [Fact]
    public async Task LoginProbeSendsPlayerLoginOnceAndStopsBeforeCollection()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        OfficialCurrentRegion region = Region(port);
        Task serverTask = RunProbeGateServerAsync(
            server,
            region,
            expectLogin: true,
            timeout.Token);

        GateLoginProbeResult result = await OfficialGateProbeClient.ProbeLoginAsync(
            ComboSession.Create("account-1", "combo-token", expectedUid: 123456789),
            region,
            OfficialClientProfile.OsGlobalV70,
            LoginProfile(),
            ProbeOptions(),
            timeout.Token);
        await serverTask;

        Assert.True(result.Token.SessionRekeySucceeded);
        Assert.True(result.PlayerLoginResponseReceived);
        Assert.True(result.PlayerUidMatches);
        Assert.False(result.ReloginRequired);
        Assert.Equal(4, result.Trace.Count);
        Assert.Single(result.Trace, record => record.MessageType == "PlayerLoginReq");
        Assert.Single(result.Trace, record => record.MessageType == "PlayerLoginRsp");
        Assert.Equal(Enumerable.Range(1, 4).Select(value => (long)value),
            result.Trace.Select(record => record.Sequence));
    }

    [Fact]
    public async Task SyntheticGateSessionFeedsTheSnapshotCollectorEndToEnd()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        OfficialCurrentRegion region = Region(port);
        Task serverTask = RunGateServerAsync(server, region, timeout.Token);
        ComboSession session = ComboSession.Create(
            "account-1",
            "combo-token",
            countryCode: "FR",
            expectedUid: 123456789);
        var metadataTrace = new GateMetadataTrace();

        Task<LiveGateMessageSource> sourceTask = LiveGateMessageSource.ConnectAsync(
            session,
            region,
            OfficialClientProfile.OsGlobalV70,
            LoginProfile(),
            new OfficialGateSessionOptions
            {
                Transport = new OfficialKcpTransportOptions
                {
                    HandshakeTimeout = TimeSpan.FromMilliseconds(250),
                    IdleTimeout = TimeSpan.FromSeconds(5),
                },
                PlayerTokenTimeout = TimeSpan.FromSeconds(2),
                PlayerLoginTimeout = TimeSpan.FromSeconds(2),
                SynchronizationTimeout = TimeSpan.FromSeconds(2),
                SynchronizationQuiescence = TimeSpan.FromMilliseconds(50),
                MetadataTrace = metadataTrace,
            },
            timeout.Token);
        await serverTask;
        await using LiveGateMessageSource source = await sourceTask;

        var collector = new OfficialSnapshotCollector();
        OfficialSnapshot snapshot = await collector.CollectAsync(
            new OfficialCaptureContext(
                source.PlayerUid,
                source.RegionName,
                new DateTimeOffset(2026, 9, 6, 12, 0, 0, TimeSpan.Zero)),
            source,
            timeout.Token);

        Assert.Equal(123456789u, snapshot.Manifest.OfficialUid);
        Assert.Equal("loopback", snapshot.Manifest.Region);
        Assert.Equal("Traveler", snapshot.Player.Nickname);
        Assert.Single(snapshot.Materials);
        Assert.Single(snapshot.Weapons);
        Assert.Single(snapshot.Avatars);
        Assert.Single(snapshot.Teams);
        Assert.DoesNotContain("combo-token", source.ToString(), StringComparison.Ordinal);
        Assert.Contains(metadataTrace.Records, record => record.MessageType == "GetPlayerTokenReq");
        Assert.Contains(metadataTrace.Records, record => record.MessageType == "PlayerLoginRsp");
        Assert.Contains(metadataTrace.Records, record => record.MessageType == "PlayerDataNotify");
        Assert.Contains(metadataTrace.Records, record => record.MessageType == "PlayerStoreNotify");
        Assert.Contains(metadataTrace.Records, record => record.MessageType == "AvatarDataNotify");
    }

    [Fact]
    public async Task SyntheticGateSessionFailsWhenSynchronizationCategoryIsMissing()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        OfficialCurrentRegion region = Region(port);
        Task serverTask = RunGateServerAsync(server, region, timeout.Token, includeAvatars: false);
        Task<LiveGateMessageSource> sourceTask = LiveGateMessageSource.ConnectAsync(
            ComboSession.Create("account-1", "combo-token", expectedUid: 123456789),
            region,
            OfficialClientProfile.OsGlobalV70,
            LoginProfile(),
            new OfficialGateSessionOptions
            {
                Transport = new OfficialKcpTransportOptions
                {
                    HandshakeTimeout = TimeSpan.FromMilliseconds(250),
                    IdleTimeout = TimeSpan.FromSeconds(5),
                },
                PlayerTokenTimeout = TimeSpan.FromSeconds(2),
                PlayerLoginTimeout = TimeSpan.FromSeconds(2),
                SynchronizationTimeout = TimeSpan.FromMilliseconds(100),
                SynchronizationQuiescence = TimeSpan.FromMilliseconds(20),
            },
            timeout.Token);
        await serverTask;
        await using LiveGateMessageSource source = await sourceTask;

        OfficialConnectivityException exception = await Assert.ThrowsAsync<OfficialConnectivityException>(
            async () => await new OfficialSnapshotCollector().CollectAsync(
                new OfficialCaptureContext(
                    source.PlayerUid,
                    source.RegionName,
                    DateTimeOffset.UtcNow),
                source,
                timeout.Token));

        Assert.Equal(OfficialConnectivityError.SyncIncomplete, exception.Error);
    }

    private static async Task RunGateServerAsync(
        UdpClient server,
        OfficialCurrentRegion region,
        CancellationToken cancellationToken,
        bool includeAvatars = true)
    {
        UdpReceiveResult connect = await server.ReceiveAsync(cancellationToken);
        Assert.IsType<ConnectHandshake>(Handshake.Parse(connect.Buffer));
        await server.SendAsync(
            new ExchangeHandshake(conv: 123, token: 456).ToByteArray(),
            connect.RemoteEndPoint,
            cancellationToken);

        var outbound = new List<byte[]>();
        var peer = new OfficialKcpPeer(123, 456, OfficialKcpWireVersion.HoyoV1, outbound.Add);
        var codec = new OfficialGatePacketCodec();
        using OfficialGateCipherState cipher = OfficialGateCipherState.FromRegion(region);
        using ClientCrypto crypto = ClientCrypto.Create(generateRsaKeys: false);

        OfficialGatePacket tokenPacket = codec.DecodeEncrypted(
            await ReceiveMessageAsync(server, connect.RemoteEndPoint, peer, outbound, cancellationToken),
            cipher);
        GetPlayerTokenReq tokenRequest = Assert.IsType<GetPlayerTokenReq>(tokenPacket.Message);
        Assert.Equal("account-1", tokenRequest.AccountUid);
        Assert.Equal("combo-token", tokenRequest.AccountToken);

        byte[] encryptedClientSeed = Convert.FromBase64String(tokenRequest.ClientRandKey);
        Assert.True(crypto.TryDecryptWithSigningKey(encryptedClientSeed, out byte[] clientSeedBytes));
        ulong clientSeed = BinaryPrimitives.ReadUInt64BigEndian(clientSeedBytes);
        const ulong serverSeed = 987654321;
        byte[] combinedSeed = new byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(combinedSeed, clientSeed ^ serverSeed);
        Assert.True(crypto.TryEncryptPayload(combinedSeed, 5, out string serverRandKey));
        SendMessage(
            server,
            connect.RemoteEndPoint,
            peer,
            outbound,
            codec.EncodeEncrypted(new GetPlayerTokenRsp
            {
                Uid = 123456789,
                AccountUid = tokenRequest.AccountUid,
                Token = tokenRequest.AccountToken,
                KeyId = tokenRequest.KeyId,
                ServerRandKey = serverRandKey,
                Sign = crypto.GenerateSignature(combinedSeed),
            }, cipher));

        byte[] sessionPad = OfficialGateKeySchedule.GenerateSessionPad(serverSeed);
        cipher.ActivateSessionPadAfterTokenResponse(sessionPad);

        OfficialGatePacket loginPacket = codec.DecodeEncrypted(
            await ReceiveMessageAsync(server, connect.RemoteEndPoint, peer, outbound, cancellationToken),
            cipher);
        PlayerLoginReq login = Assert.IsType<PlayerLoginReq>(loginPacket.Message);
        Assert.Equal(123456789u, login.TargetUid);
        Assert.Equal("combo-token", login.Token);
        Assert.Equal("version-hash", login.ClientVersionHash);
        Assert.Equal("user-agent", login.UaPc);

        Starlight.Protobuf.Core.IMessage[] synchronization = InitialSynchronization();
        int messageCount = includeAvatars ? synchronization.Length : synchronization.Length - 1;
        foreach (Starlight.Protobuf.Core.IMessage message in synchronization.Take(messageCount))
        {
            peer.Send(codec.EncodeEncrypted(message, cipher));
        }
        peer.Send(codec.EncodeEncrypted(new PlayerLoginRsp { TargetUid = 123456789 }, cipher));
        peer.Update(Environment.TickCount64 + 20);
        Flush(server, connect.RemoteEndPoint, outbound);

        CryptographicOperations.ZeroMemory(encryptedClientSeed);
        CryptographicOperations.ZeroMemory(clientSeedBytes);
        CryptographicOperations.ZeroMemory(combinedSeed);
        CryptographicOperations.ZeroMemory(sessionPad);
    }

    private static async Task RunProbeGateServerAsync(
        UdpClient server,
        OfficialCurrentRegion region,
        bool expectLogin,
        CancellationToken cancellationToken)
    {
        UdpReceiveResult connect = await server.ReceiveAsync(cancellationToken);
        Assert.IsType<ConnectHandshake>(Handshake.Parse(connect.Buffer));
        await server.SendAsync(
            new ExchangeHandshake(conv: 123, token: 456).ToByteArray(),
            connect.RemoteEndPoint,
            cancellationToken);

        var outbound = new List<byte[]>();
        var peer = new OfficialKcpPeer(123, 456, OfficialKcpWireVersion.HoyoV1, outbound.Add);
        var codec = new OfficialGatePacketCodec();
        using OfficialGateCipherState cipher = OfficialGateCipherState.FromRegion(region);
        using ClientCrypto crypto = ClientCrypto.Create(generateRsaKeys: false);

        OfficialGatePacket tokenPacket = codec.DecodeEncrypted(
            await ReceiveMessageAsync(server, connect.RemoteEndPoint, peer, outbound, cancellationToken),
            cipher);
        GetPlayerTokenReq tokenRequest = Assert.IsType<GetPlayerTokenReq>(tokenPacket.Message);
        byte[] encryptedClientSeed = Convert.FromBase64String(tokenRequest.ClientRandKey);
        Assert.True(crypto.TryDecryptWithSigningKey(encryptedClientSeed, out byte[] clientSeedBytes));
        ulong clientSeed = BinaryPrimitives.ReadUInt64BigEndian(clientSeedBytes);
        const ulong serverSeed = 987654321;
        byte[] combinedSeed = new byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(combinedSeed, clientSeed ^ serverSeed);
        Assert.True(crypto.TryEncryptPayload(combinedSeed, 5, out string serverRandKey));
        SendMessage(
            server,
            connect.RemoteEndPoint,
            peer,
            outbound,
            codec.EncodeEncrypted(new GetPlayerTokenRsp
            {
                Uid = 123456789,
                AccountUid = tokenRequest.AccountUid,
                Token = tokenRequest.AccountToken,
                KeyId = tokenRequest.KeyId,
                ServerRandKey = serverRandKey,
                Sign = crypto.GenerateSignature(combinedSeed),
            }, cipher));

        byte[] sessionPad = OfficialGateKeySchedule.GenerateSessionPad(serverSeed);
        cipher.ActivateSessionPadAfterTokenResponse(sessionPad);

        if (expectLogin)
        {
            OfficialGatePacket loginPacket = codec.DecodeEncrypted(
                await ReceiveMessageAsync(
                    server,
                    connect.RemoteEndPoint,
                    peer,
                    outbound,
                    cancellationToken),
                cipher);
            Assert.IsType<PlayerLoginReq>(loginPacket.Message);
            SendMessage(
                server,
                connect.RemoteEndPoint,
                peer,
                outbound,
                codec.EncodeEncrypted(new PlayerLoginRsp { TargetUid = 123456789 }, cipher));
        }

        await WaitForDisconnectAsync(
            server,
            connect.RemoteEndPoint,
            peer,
            outbound,
            cipher,
            codec,
            expectLogin,
            cancellationToken);

        CryptographicOperations.ZeroMemory(encryptedClientSeed);
        CryptographicOperations.ZeroMemory(clientSeedBytes);
        CryptographicOperations.ZeroMemory(combinedSeed);
        CryptographicOperations.ZeroMemory(sessionPad);
    }

    private static async Task WaitForDisconnectAsync(
        UdpClient server,
        IPEndPoint endpoint,
        OfficialKcpPeer peer,
        List<byte[]> outbound,
        OfficialGateCipherState cipher,
        OfficialGatePacketCodec codec,
        bool loginAlreadyObserved,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            UdpReceiveResult datagram = await server.ReceiveAsync(cancellationToken);
            if (datagram.Buffer.Length == 20
                && Handshake.Parse(datagram.Buffer) is DisconnectHandshake)
            {
                return;
            }

            IReadOnlyList<byte[]> messages = peer.Input(datagram.Buffer, Environment.TickCount64);
            Flush(server, endpoint, outbound);
            foreach (byte[] message in messages)
            {
                OfficialGatePacket packet = codec.DecodeEncrypted(message, cipher);
                if (!loginAlreadyObserved)
                {
                    Assert.IsNotType<PlayerLoginReq>(packet.Message);
                }
            }
        }
    }

    private static async Task<byte[]> ReceiveMessageAsync(
        UdpClient server,
        IPEndPoint endpoint,
        OfficialKcpPeer peer,
        List<byte[]> outbound,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            UdpReceiveResult datagram = await server.ReceiveAsync(cancellationToken);
            IReadOnlyList<byte[]> messages = peer.Input(datagram.Buffer, Environment.TickCount64);
            Flush(server, endpoint, outbound);
            if (messages.Count != 0)
            {
                return Assert.Single(messages);
            }
        }
    }

    private static void SendMessage(
        UdpClient server,
        IPEndPoint endpoint,
        OfficialKcpPeer peer,
        List<byte[]> outbound,
        byte[] message)
    {
        peer.Send(message);
        peer.Update(Environment.TickCount64 + 10);
        Flush(server, endpoint, outbound);
    }

    private static void Flush(UdpClient server, IPEndPoint endpoint, List<byte[]> outbound)
    {
        foreach (byte[] datagram in outbound)
        {
            server.Client.SendTo(datagram, endpoint);
        }
        outbound.Clear();
    }

    private static Starlight.Protobuf.Core.IMessage[] InitialSynchronization()
    {
        var weapon = new Weapon { Level = 20, PromoteLevel = 0 };
        weapon.AffixMap[101] = 2;
        var store = new PlayerStoreNotify
        {
            ItemList = {
                new Item { ItemId = 1001, Guid = 100, Material = new Material { Count = 5 } },
                new Item {
                    ItemId = 11101,
                    Guid = 200,
                    Equip = new Equip { Weapon = weapon },
                },
            },
        };
        var avatar = new AvatarInfo
        {
            AvatarId = 10000005,
            Guid = 300,
            BornTime = 1_700_000_000,
            EquipGuidList = { 200 },
            PropMap = {
                [(uint)PlayerProperty.Level] = PlayerProperty.Level.Value(50),
            },
        };
        var avatars = new AvatarDataNotify
        {
            CurAvatarTeamId = 1,
            ChooseAvatarGuid = 300,
            AvatarList = { avatar },
            AvatarTeamMap = {
                [1] = new AvatarTeam { TeamName = "Main", AvatarGuidList = { 300 } },
            },
        };

        return [new PlayerDataNotify { NickName = "Traveler" }, store, avatars];
    }

    private static OfficialPlayerLoginProfile LoginProfile() => new()
    {
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

    private static OfficialGateProbeOptions ProbeOptions() => new()
    {
        Transport = new OfficialKcpTransportOptions
        {
            HandshakeTimeout = TimeSpan.FromMilliseconds(250),
            IdleTimeout = TimeSpan.FromSeconds(5),
        },
        PlayerTokenTimeout = TimeSpan.FromSeconds(2),
        PlayerLoginTimeout = TimeSpan.FromSeconds(2),
    };

    private static OfficialCurrentRegion Region(int port)
    {
        byte[] secret = Ec2bKeyGen.Create("live-gate-test");
        return new OfficialCurrentRegion
        {
            RegionName = "loopback",
            GateServerIp = IPAddress.Loopback.ToString(),
            GateServerPort = checked((uint)port),
            UseGateServerDomainName = false,
            GateServerDomainName = string.Empty,
            ClientSecretKey = secret.ToArray(),
            SecretKey = secret,
            ConnectGateTicket = OfficialSecret.Create(string.Empty),
            ClientDataVersion = 70,
            ClientSilenceDataVersion = 71,
            ClientDataMd5 = "data-md5",
            ClientSilenceDataMd5 = "silence-md5",
            ClientVersionSuffix = "suffix",
            ClientSilenceVersionSuffix = "silence-suffix",
            GameBiz = "hk4e_global",
            ResourceUrl = string.Empty,
            DataUrl = string.Empty,
        };
    }
}
