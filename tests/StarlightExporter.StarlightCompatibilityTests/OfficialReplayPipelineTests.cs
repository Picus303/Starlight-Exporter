using Microsoft.Data.Sqlite;
using Starlight.Protocol;
using StarlightExporter.Official;
using StarlightExporter.Persistence;
using StarlightExporter.Snapshot;
using StarlightExporter.StarlightTarget;
using Xunit;

namespace StarlightExporter.Tests;

public sealed class OfficialReplayPipelineTests
{
    [Fact]
    public async Task SanitizedReplayProducesMappableStarlightDatabase()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "StarlightExporter.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            string replayPath = Path.Combine(directory, "capture.replay.json");
            string databasePath = Path.Combine(directory, "starlight.db");
            OfficialCaptureContext context = new(
                OfficialUid: 123456789,
                Region: "os_euro",
                CapturedAtUtc: new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero),
                Profile: new OfficialProfileSupplement("Synthetic profile", 10000005, 210001));

            await SanitizedReplaySerializer.WriteNewAsync(replayPath, context, CreateMessages());
            SanitizedReplaySource replay = await SanitizedReplaySerializer.ReadAsync(replayPath);
            OfficialSnapshot snapshot = await new OfficialSnapshotCollector().CollectAsync(
                replay.Context,
                replay);

            StarlightMappingResult mapping = new StarlightSnapshotMapper(TestGameData.Create()).Map(snapshot);
            Assert.True(mapping.IsSuccess);

            StarlightDatabaseWriteResult result = await StarlightDatabaseWriter.WriteNewAsync(
                new StarlightDatabaseWriteRequest(
                    databasePath,
                    snapshot.Manifest.OfficialUid,
                    PrivateAccountId: "1",
                    mapping.Profile,
                    mapping.State));

            Assert.Equal(123456789u, result.PlayerUid);
            Assert.Equal(1, result.MaterialCount);
            Assert.Equal(1, result.WeaponCount);
            Assert.Equal(1, result.AvatarCount);
            Assert.Equal(4, result.TeamCount);
            Assert.True(File.Exists(databasePath));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static OfficialMessageEnvelope[] CreateMessages()
    {
        var player = new PlayerDataNotify { NickName = "Traveler" };
        var weapon = new Weapon { Level = 20, PromoteLevel = 0 };
        weapon.AffixMap[11101] = 2;
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
            CoreProudSkillLevel = 2,
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

        return [
            new OfficialMessageEnvelope(1, player),
            new OfficialMessageEnvelope(2, store),
            new OfficialMessageEnvelope(3, avatars),
        ];
    }
}
