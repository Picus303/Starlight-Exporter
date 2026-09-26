using System.Runtime.CompilerServices;
using Starlight.Ec2b;
using Starlight.Protocol;
using StarlightExporter.Official;
using StarlightExporter.Snapshot;
using Xunit;

namespace StarlightExporter.OfficialTests;

public sealed class OfficialAccountCaptureFlowTests
{
    [Fact]
    public async Task CaptureDerivesPlayerUidFromGateAndProducesStarlightReadyBasics()
    {
        var session = ComboSession.Create("combo-account", "combo-token");
        var installation = new SdkInstallationState
        {
            DeviceId = "synthetic-device",
            DeviceFingerprint = "1234567890",
        };
        var sdk = new FakeSdkCombo(session, installation);
        var dispatch = new FakeDispatch(session);
        var gate = new FakeGate(session);
        var subject = new OfficialAccountCaptureFlow(
            sdk, dispatch, gate, OfficialClientProfile.OsGlobalV70);

        OfficialSnapshot snapshot = await subject.CaptureAsync(
            "unused-state-path", "unused-unique-id", FingerprintRuntime(),
            new UnusedAttributes(),
            _ => Task.FromResult(new OfficialPasswordCredentials("synthetic-email", "synthetic-password")),
            "os_euro", LoginProfile);

        Assert.Same(session, dispatch.ReceivedSession);
        Assert.Same(session, gate.ReceivedSession);
        Assert.Equal(987654321u, snapshot.Manifest.OfficialUid);
        Assert.Equal("os_euro", snapshot.Manifest.Region);
        Assert.Equal("Traveler", snapshot.Player.Nickname);
        Assert.Single(snapshot.Weapons);
        Assert.Single(snapshot.Avatars);
        Assert.Single(snapshot.Teams);
        Assert.True(SnapshotValidator.Validate(snapshot).IsValid);
        Assert.Equal(3, snapshot.Unsupported.Count(record => record.Category == "profile"));
    }

    [Fact]
    public async Task CaptureRejectsDifferentGateDeviceIdentityBeforeConnecting()
    {
        var session = ComboSession.Create("combo-account", "combo-token");
        var installation = new SdkInstallationState
        {
            DeviceId = "synthetic-device",
            DeviceFingerprint = "1234567890",
        };
        var gate = new FakeGate(session);
        var subject = new OfficialAccountCaptureFlow(
            new FakeSdkCombo(session, installation),
            new FakeDispatch(session), gate, OfficialClientProfile.OsGlobalV70);

        await Assert.ThrowsAsync<ArgumentException>(() => subject.CaptureAsync(
            "unused-state-path", "unused-unique-id", FingerprintRuntime(),
            new UnusedAttributes(),
            _ => Task.FromResult(new OfficialPasswordCredentials("synthetic-email", "synthetic-password")),
            "os_euro", state => LoginProfile(state) with { DeviceUuid = "different-device" }));

        Assert.Null(gate.ReceivedSession);
    }

    private static OfficialPlayerLoginProfile LoginProfile(SdkInstallationState state) => new()
    {
        DeviceInfo = "synthetic-machine",
        DeviceName = "synthetic-host",
        DeviceUuid = state.DeviceId,
        DeviceFingerprint = state.DeviceFingerprint!,
        SystemVersion = "synthetic-os",
        ScreenWidth = 1920,
        ScreenHeight = 1080,
    };

    private static OfficialDeviceFingerprintRuntime FingerprintRuntime() => new();

    private sealed class UnusedAttributes : IOfficialDeviceAttributeCollector
    {
        public ValueTask<string?> CollectAsync(string name, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The fake SDK flow must not collect attributes.");
    }

    private sealed class FakeSdkCombo(ComboSession session, SdkInstallationState installation)
        : IOfficialSdkComboFlow
    {
        public Task<OfficialSdkComboResult> RunAsync(
            string statePath,
            string deviceUniqueIdentifier,
            OfficialDeviceFingerprintRuntime runtime,
            IOfficialDeviceAttributeCollector attributes,
            Func<CancellationToken, Task<OfficialPasswordCredentials>> readCredentials,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new OfficialSdkComboResult(
                session,
                installation,
                new OfficialSdkBootstrapResult(true, true),
                OfficialDeviceFingerprintOrigin.ServerRefreshed,
                FingerprintRejectionRetcode: null));
    }

    private sealed class FakeDispatch(ComboSession expectedSession) : IOfficialDispatchClient
    {
        public ComboSession? ReceivedSession { get; private set; }

        public Task<OfficialRegionList> GetRegionsAsync(
            OfficialClientProfile profile,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Regional dispatch should use the Combo overload.");

        public Task<OfficialCurrentRegion> ResolveRegionAsync(
            OfficialClientProfile profile,
            string regionName,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Regional dispatch should use the Combo overload.");

        public Task<OfficialCurrentRegion> ResolveRegionAsync(
            OfficialClientProfile profile,
            string regionName,
            ComboSession session,
            CancellationToken cancellationToken = default)
        {
            Assert.Same(expectedSession, session);
            Assert.Equal("os_euro", regionName);
            ReceivedSession = session;
            byte[] key = Ec2bKeyGen.Create("synthetic-region");
            return Task.FromResult(new OfficialCurrentRegion
            {
                RegionName = regionName,
                GateServerIp = "192.0.2.1",
                GateServerPort = 22102,
                UseGateServerDomainName = false,
                GateServerDomainName = string.Empty,
                ClientSecretKey = key,
                SecretKey = key,
                ConnectGateTicket = OfficialSecret.Create(string.Empty),
                ClientDataVersion = 70,
                ClientSilenceDataVersion = 71,
                ClientDataMd5 = string.Empty,
                ClientSilenceDataMd5 = string.Empty,
                ClientVersionSuffix = string.Empty,
                ClientSilenceVersionSuffix = string.Empty,
                GameBiz = "hk4e_global",
                ResourceUrl = string.Empty,
                DataUrl = string.Empty,
            });
        }
    }

    private sealed class FakeGate(ComboSession expectedSession) : IOfficialGateSessionConnector
    {
        public ComboSession? ReceivedSession { get; private set; }

        public Task<IOfficialConnectedMessageSource> ConnectAsync(
            ComboSession session,
            OfficialCurrentRegion region,
            OfficialClientProfile clientProfile,
            OfficialPlayerLoginProfile loginProfile,
            OfficialGateSessionOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Assert.Same(expectedSession, session);
            Assert.Equal("synthetic-device", loginProfile.DeviceUuid);
            Assert.Equal("1234567890", loginProfile.DeviceFingerprint);
            ReceivedSession = session;
            return Task.FromResult<IOfficialConnectedMessageSource>(new FakeConnectedSource());
        }
    }

    private sealed class FakeConnectedSource : IOfficialConnectedMessageSource
    {
        public uint PlayerUid => 987654321;
        public string RegionName => "os_euro";

        public async IAsyncEnumerable<OfficialMessageEnvelope> ReadAllAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var weapon = new Weapon { Level = 20 };
            yield return new OfficialMessageEnvelope(1, new PlayerDataNotify { NickName = "Traveler" });
            yield return new OfficialMessageEnvelope(2, new PlayerStoreNotify
            {
                StoreType = (StoreType)1,
                ItemList =
                {
                    new Item
                    {
                        ItemId = 11101,
                        Guid = 200,
                        Equip = new Equip { Weapon = weapon },
                    },
                },
            });
            yield return new OfficialMessageEnvelope(3, new AvatarDataNotify
            {
                CurAvatarTeamId = 1,
                ChooseAvatarGuid = 300,
                AvatarList =
                {
                    new AvatarInfo
                    {
                        AvatarId = 10000005,
                        Guid = 300,
                        BornTime = 1_700_000_000,
                        EquipGuidList = { 200 },
                    },
                },
                AvatarTeamMap =
                {
                    [1] = new AvatarTeam { AvatarGuidList = { 300 } },
                },
            });
            await Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
