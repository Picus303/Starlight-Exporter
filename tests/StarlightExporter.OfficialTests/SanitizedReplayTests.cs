using Starlight.Protocol;
using StarlightExporter.Official;
using StarlightExporter.Snapshot;
using Xunit;

namespace StarlightExporter.OfficialTests;

public sealed class SanitizedReplayTests
{
    [Fact]
    public void ConnectivityExceptionDoesNotExposeInnerExceptionOrData()
    {
        const string secret = "must-not-escape";
        var exception = new OfficialConnectivityException(
            OfficialConnectivityError.ComboExchangeRejected,
            "Safe failure.",
            new InvalidOperationException(secret));

        Assert.Null(exception.InnerException);
        Assert.Empty(exception.Data);
        Assert.Equal(nameof(InvalidOperationException), exception.CauseType);
        Assert.Equal("COMBO_EXCHANGE_REJECTED", OfficialConnectivityDiagnostic.Code(exception.Error));
        Assert.DoesNotContain(secret, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, OfficialConnectivityDiagnostic.SafeMessage(exception.Error), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReplayRoundTripFeedsCollectorAndProducesValidSnapshot()
    {
        using var directory = new TemporaryDirectory();
        string replayPath = Path.Combine(directory.Path, "synthetic.replay.json");
        OfficialCaptureContext context = Context(withProfile: true);
        OfficialMessageEnvelope[] messages = CompleteMessages();

        await SanitizedReplaySerializer.WriteNewAsync(replayPath, context, messages);
        SanitizedReplaySource replay = await SanitizedReplaySerializer.ReadAsync(replayPath);
        var collector = new OfficialSnapshotCollector();
        OfficialSnapshot snapshot = await collector.CollectAsync(replay.Context, replay);

        Assert.True(SnapshotValidator.Validate(snapshot).IsValid);
        Assert.Equal(123456789u, snapshot.Manifest.OfficialUid);
        Assert.Equal("Traveler", snapshot.Player.Nickname);
        Assert.Equal("Synthetic profile", snapshot.Player.Signature);
        Assert.Equal(10000005u, snapshot.Player.BornAvatarId);
        Assert.Equal(1u, snapshot.Player.CurrentAvatarTeamId);

        SnapshotMaterial material = Assert.Single(snapshot.Materials);
        Assert.Equal(1001u, material.ItemId);
        Assert.Equal(5u, material.Count);

        SnapshotWeapon weapon = Assert.Single(snapshot.Weapons);
        Assert.Equal(11101u, weapon.ItemId);
        Assert.Equal(3u, weapon.Refinement);
        Assert.Equal(101u, weapon.AffixId);
        Assert.Equal(0u, weapon.GadgetId);

        SnapshotAvatar avatar = Assert.Single(snapshot.Avatars);
        Assert.Equal(50u, avatar.Level);
        Assert.Equal(2u, avatar.Constellation);
        Assert.Equal(200ul, avatar.WeaponGuid);

        SnapshotTeam team = Assert.Single(snapshot.Teams);
        Assert.Equal(new ulong[] { 300 }, team.AvatarGuids);
        Assert.Equal(300ul, team.CurrentAvatarGuid);
        Assert.Empty(snapshot.Unsupported);
    }

    [Fact]
    public async Task ReplayWithoutProfileRecordsExplicitUnsupportedFields()
    {
        var collector = new OfficialSnapshotCollector();
        var source = new InMemoryMessageSource(CompleteMessages());

        OfficialSnapshot snapshot = await collector.CollectAsync(Context(withProfile: false), source);

        Assert.Equal(3, snapshot.Unsupported.Count(record => record.Category == "profile"));
        Assert.Equal(string.Empty, snapshot.Player.Signature);
        Assert.Equal(0u, snapshot.Player.PictureId);
        Assert.Equal(0u, snapshot.Player.NameCardId);
    }

    [Fact]
    public async Task ReplayWriterRejectsAuthenticationMessages()
    {
        using var directory = new TemporaryDirectory();
        string replayPath = Path.Combine(directory.Path, "forbidden.replay.json");
        var messages = new[] {
            new OfficialMessageEnvelope(1, new GetPlayerTokenRsp { Token = "must-not-persist" }),
        };

        OfficialConnectivityException exception = await Assert.ThrowsAsync<OfficialConnectivityException>(() =>
            SanitizedReplaySerializer.WriteNewAsync(replayPath, Context(withProfile: false), messages));

        Assert.Equal(OfficialConnectivityError.ReplayInvalid, exception.Error);
        Assert.False(File.Exists(replayPath));
        Assert.DoesNotContain("must-not-persist", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReplayReaderRejectsUnknownJsonProperties()
    {
        using var directory = new TemporaryDirectory();
        string validPath = Path.Combine(directory.Path, "valid.replay.json");
        string invalidPath = Path.Combine(directory.Path, "invalid.replay.json");
        await SanitizedReplaySerializer.WriteNewAsync(validPath, Context(withProfile: true), CompleteMessages());
        string json = await File.ReadAllTextAsync(validPath);
        await File.WriteAllTextAsync(invalidPath, json.Replace(
            "\"schemaVersion\": 1,",
            "\"schemaVersion\": 1,\n  \"unexpected\": true,",
            StringComparison.Ordinal));

        OfficialConnectivityException exception = await Assert.ThrowsAsync<OfficialConnectivityException>(() =>
            SanitizedReplaySerializer.ReadAsync(invalidPath));

        Assert.Equal(OfficialConnectivityError.ReplayInvalid, exception.Error);
    }

    [Fact]
    public async Task ReplayReaderRejectsSensitivePropertiesWithoutLeakingTheirValue()
    {
        using var directory = new TemporaryDirectory();
        string validPath = Path.Combine(directory.Path, "valid.replay.json");
        string invalidPath = Path.Combine(directory.Path, "sensitive.replay.json");
        await SanitizedReplaySerializer.WriteNewAsync(validPath, Context(withProfile: true), CompleteMessages());
        string json = await File.ReadAllTextAsync(validPath);
        await File.WriteAllTextAsync(invalidPath, json.Replace(
            "\"schemaVersion\": 1,",
            "\"schemaVersion\": 1,\n  \"accessToken\": \"must-not-leak\",",
            StringComparison.Ordinal));

        OfficialConnectivityException exception = await Assert.ThrowsAsync<OfficialConnectivityException>(() =>
            SanitizedReplaySerializer.ReadAsync(invalidPath));

        Assert.Equal(OfficialConnectivityError.ReplayInvalid, exception.Error);
        Assert.DoesNotContain("must-not-leak", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReplayWriterRejectsOutOfOrderMessages()
    {
        using var directory = new TemporaryDirectory();
        OfficialMessageEnvelope[] messages = CompleteMessages();

        OfficialConnectivityException exception = await Assert.ThrowsAsync<OfficialConnectivityException>(() =>
            SanitizedReplaySerializer.WriteNewAsync(
                Path.Combine(directory.Path, "out-of-order.replay.json"),
                Context(withProfile: true),
                [messages[1], messages[0], messages[2]]));

        Assert.Equal(OfficialConnectivityError.ReplayInvalid, exception.Error);
    }

    [Fact]
    public async Task CollectorCombinesStoreFragmentsIdempotently()
    {
        OfficialMessageEnvelope[] complete = CompleteMessages();
        var firstStore = new PlayerStoreNotify {
            ItemList = {
                new Item { ItemId = 1001, Guid = 100, Material = new Material { Count = 1 } },
            },
        };
        var secondStore = new PlayerStoreNotify {
            ItemList = {
                new Item { ItemId = 1001, Guid = 100, Material = new Material { Count = 5 } },
                CreateWeapon(),
            },
        };
        var source = new InMemoryMessageSource([
            complete[0],
            new OfficialMessageEnvelope(2, firstStore),
            new OfficialMessageEnvelope(3, secondStore),
            new OfficialMessageEnvelope(4, complete[2].Message),
        ]);

        OfficialSnapshot snapshot = await new OfficialSnapshotCollector().CollectAsync(
            Context(withProfile: true),
            source);

        Assert.Equal(5u, Assert.Single(snapshot.Materials).Count);
        Assert.Single(snapshot.Weapons);
    }

    [Fact]
    public async Task CollectorReportsMissingSynchronizationCategory()
    {
        OfficialMessageEnvelope[] complete = CompleteMessages();
        var source = new InMemoryMessageSource([complete[0], complete[2]]);

        OfficialConnectivityException exception = await Assert.ThrowsAsync<OfficialConnectivityException>(() =>
            new OfficialSnapshotCollector().CollectAsync(Context(withProfile: true), source));

        Assert.Equal(OfficialConnectivityError.SyncIncomplete, exception.Error);
        Assert.Contains(nameof(PlayerStoreNotify), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReplayWriterRefusesExistingOutput()
    {
        using var directory = new TemporaryDirectory();
        string replayPath = Path.Combine(directory.Path, "existing.replay.json");
        await File.WriteAllTextAsync(replayPath, "existing");

        await Assert.ThrowsAsync<IOException>(() => SanitizedReplaySerializer.WriteNewAsync(
            replayPath,
            Context(withProfile: true),
            CompleteMessages()));

        Assert.Equal("existing", await File.ReadAllTextAsync(replayPath));
    }

    private static OfficialCaptureContext Context(bool withProfile) => new(
        OfficialUid: 123456789,
        Region: "os_euro",
        CapturedAtUtc: new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero),
        Profile: withProfile
            ? new OfficialProfileSupplement("Synthetic profile", 10000005, 210001)
            : null);

    private static OfficialMessageEnvelope[] CompleteMessages()
    {
        var player = new PlayerDataNotify { NickName = "Traveler" };
        var store = new PlayerStoreNotify {
            ItemList = {
                new Item { ItemId = 1001, Guid = 100, Material = new Material { Count = 5 } },
                CreateWeapon(),
            },
        };
        var avatar = new AvatarInfo {
            AvatarId = 10000005,
            Guid = 300,
            BornTime = 1_700_000_000,
            CoreProudSkillLevel = 2,
            EquipGuidList = { 200 },
            PropMap = {
                [(uint)PlayerProperty.Level] = PlayerProperty.Level.Value(50),
            },
        };
        var avatars = new AvatarDataNotify {
            CurAvatarTeamId = 1,
            ChooseAvatarGuid = 300,
            AvatarList = { avatar },
            AvatarTeamMap = {
                [1] = new AvatarTeam { TeamName = "Main", AvatarGuidList = { 300 } },
            },
        };

        return [
            new OfficialMessageEnvelope(1, player),
            new OfficialMessageEnvelope(2, store),
            new OfficialMessageEnvelope(3, avatars),
        ];
    }

    private static Item CreateWeapon()
    {
        var weapon = new Weapon { Level = 20, PromoteLevel = 0 };
        weapon.AffixMap[101] = 2;
        return new Item {
            ItemId = 11101,
            Guid = 200,
            Equip = new Equip { Weapon = weapon },
        };
    }

    private sealed class InMemoryMessageSource(IReadOnlyList<OfficialMessageEnvelope> messages)
        : IOfficialMessageSource
    {
        public async IAsyncEnumerable<OfficialMessageEnvelope> ReadAllAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (OfficialMessageEnvelope message in messages)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return message;
            }

            await Task.CompletedTask;
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"starlight-exporter-official-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
