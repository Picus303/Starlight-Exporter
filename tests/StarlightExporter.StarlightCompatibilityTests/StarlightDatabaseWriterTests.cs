using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Starlight.DbGate;
using Starlight.DbGate.Models;
using Starlight.Game.Player;
using Starlight.Game.Resources;
using Starlight.Protocol;
using Starlight.Rpc.Proto;
using StarlightExporter.Persistence;
using StarlightExporter.Snapshot;
using StarlightExporter.StarlightTarget;
using Xunit;

namespace StarlightExporter.Tests;

public sealed class StarlightDatabaseWriterTests
{
    [Fact]
    public void PlayerUidAllocatorSeparatesPreservedAndPrivateRanges()
    {
        Assert.Equal(765432100u, PlayerUidAllocator.Resolve(PlayerUidMode.Preserve, 765432100));
        Assert.Equal(
            PlayerUidAllocator.FirstAllocatedUid,
            PlayerUidAllocator.Resolve(PlayerUidMode.Allocate, 765432100));
        Assert.Equal(
            100000003u,
            PlayerUidAllocator.Resolve(PlayerUidMode.Allocate, 765432100, [100000000, 100000002, 42]));
        Assert.Throws<InvalidOperationException>(() =>
            PlayerUidAllocator.Resolve(PlayerUidMode.Preserve, 765432100, [765432100]));
    }

    [Fact]
    public async Task PrivateAccountDatabaseIsCreatedAtomicallyAndCanBeValidated()
    {
        const string password = "private-test-password";
        string testDirectory = CreateTestDirectory();
        string databasePath = Path.Combine(testDirectory, "accounts.db");

        try
        {
            PrivateAccountWriteResult result = await PrivateAccountDatabaseWriter.WriteNewAsync(
                databasePath,
                "traveler-test",
                password);

            Assert.Equal(1u, result.AccountId);
            Assert.Equal("traveler-test", result.Username);
            Assert.True(File.Exists(databasePath));
            Assert.Single(Directory.EnumerateFiles(testDirectory));
            PrivateAccountValidationResult validation = await PrivateAccountValidator.ValidateExistsAsync(
                databasePath,
                result.AccountId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Assert.True(validation.IsValid);
            Assert.DoesNotContain(password, validation.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(
                password,
                System.Text.Encoding.Latin1.GetString(await File.ReadAllBytesAsync(databasePath)),
                StringComparison.Ordinal);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task PrivateAccountDatabaseRejectsAnIncompatiblePasswordWithoutArtifacts()
    {
        string testDirectory = CreateTestDirectory();
        string databasePath = Path.Combine(testDirectory, "accounts.db");

        try
        {
            ArgumentException exception = await Assert.ThrowsAsync<ArgumentException>(() =>
                PrivateAccountDatabaseWriter.WriteNewAsync(databasePath, "traveler-test", "short"));

            Assert.Contains("8-50", exception.Message, StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateFileSystemEntries(testDirectory));
        }
        finally
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task MappedSnapshotIsWrittenReadAndAcceptedByModules()
    {
        string testDirectory = CreateTestDirectory();

        try
        {
            GameData data = TestGameData.Create();
            OfficialSnapshot snapshot = await OfficialSnapshotSerializer.ReadAsync(FixturePath("minimal-valid.json"));
            StarlightMappingResult mapping = new StarlightSnapshotMapper(data).Map(snapshot);
            Assert.True(mapping.IsSuccess);
            string databasePath = Path.Combine(testDirectory, "starlight.db");

            StarlightDatabaseWriteResult result = await StarlightDatabaseWriter.WriteNewAsync(
                new StarlightDatabaseWriteRequest(
                    databasePath,
                    snapshot.Manifest.OfficialUid,
                    PrivateAccountId: "1",
                    mapping.Profile,
                    mapping.State));

            Assert.Equal(Path.GetFullPath(databasePath), result.OutputPath);
            Assert.Equal(snapshot.Manifest.OfficialUid, result.PlayerUid);
            Assert.Equal("1", result.PrivateAccountId);
            Assert.Equal(expected: 1, result.MaterialCount);
            Assert.Equal(expected: 1, result.WeaponCount);
            Assert.Equal(expected: 1, result.AvatarCount);
            Assert.Equal(expected: 4, result.TeamCount);
            Assert.True(File.Exists(databasePath));

            NetPlayer stored = await ReadPlayerAsync(databasePath, result.PlayerUid);
            Assert.Equal(result.PlayerUid, stored.Uid);
            Assert.Equal(result.PrivateAccountId, stored.AccountId);
            Assert.Equal(mapping.Profile.ToByteArray(), stored.Profile.ToByteArray());
            Assert.Equal(mapping.State.ToByteArray(), stored.State.ToByteArray());

            byte[] stateBeforeModules = stored.State.ToByteArray();
            var (player, sent) = TestStarlightPlayer.Create(data, stored.State, stored.Profile);
            await player.Module<InventoryModule>().OnLogin();
            await player.Module<AvatarModule>().OnLogin();
            player.Module<TeamModule>().OnLogin();

            Assert.Equal(stateBeforeModules, player.State.ToByteArray());
            Assert.DoesNotContain(sent, message => message is AvatarEquipChangeNotify);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ExistingOutputIsRejectedWithoutModification()
    {
        string testDirectory = CreateTestDirectory();

        try
        {
            string databasePath = Path.Combine(testDirectory, "starlight.db");
            await File.WriteAllTextAsync(databasePath, "existing");
            StarlightMappingResult mapping = await CreateMappingAsync();

            IOException error = await Assert.ThrowsAsync<IOException>(() =>
                StarlightDatabaseWriter.WriteNewAsync(new StarlightDatabaseWriteRequest(
                    databasePath,
                    PlayerUid: 765432100,
                    PrivateAccountId: "1",
                    mapping.Profile,
                    mapping.State)));

            Assert.Contains("already exists", error.Message, StringComparison.Ordinal);
            Assert.Equal("existing", await File.ReadAllTextAsync(databasePath));
        }
        finally
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task NullStateIsRejectedBeforeCreatingAFile()
    {
        string testDirectory = CreateTestDirectory();

        try
        {
            string databasePath = Path.Combine(testDirectory, "starlight.db");
            StarlightMappingResult valid = await CreateMappingAsync();

            await Assert.ThrowsAsync<ArgumentNullException>(() =>
                StarlightDatabaseWriter.WriteNewAsync(new StarlightDatabaseWriteRequest(
                    databasePath,
                    PlayerUid: 765432100,
                    PrivateAccountId: "1",
                    valid.Profile,
                    State: null!)));

            Assert.False(File.Exists(databasePath));
            Assert.Empty(Directory.EnumerateFiles(testDirectory));
        }
        finally
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ZeroUidIsRejectedBeforeCreatingAFile()
    {
        string testDirectory = CreateTestDirectory();

        try
        {
            string databasePath = Path.Combine(testDirectory, "starlight.db");
            StarlightMappingResult mapping = await CreateMappingAsync();

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                StarlightDatabaseWriter.WriteNewAsync(new StarlightDatabaseWriteRequest(
                    databasePath,
                    PlayerUid: 0,
                    PrivateAccountId: "1",
                    mapping.Profile,
                    mapping.State)));

            Assert.Empty(Directory.EnumerateFileSystemEntries(testDirectory));
        }
        finally
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task CancellationLeavesNoDatabaseArtifact()
    {
        string testDirectory = CreateTestDirectory();

        try
        {
            string databasePath = Path.Combine(testDirectory, "starlight.db");
            StarlightMappingResult mapping = await CreateMappingAsync();
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                StarlightDatabaseWriter.WriteNewAsync(
                    new StarlightDatabaseWriteRequest(
                        databasePath,
                        PlayerUid: 765432100,
                        PrivateAccountId: "1",
                        mapping.Profile,
                        mapping.State),
                    cancellation.Token));

            Assert.Empty(Directory.EnumerateFileSystemEntries(testDirectory));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    private static async Task<StarlightMappingResult> CreateMappingAsync()
    {
        OfficialSnapshot snapshot = await OfficialSnapshotSerializer.ReadAsync(FixturePath("minimal-valid.json"));
        return new StarlightSnapshotMapper(TestGameData.Create()).Map(snapshot);
    }

    private static async Task<NetPlayer> ReadPlayerAsync(string databasePath, uint uid)
    {
        var options = new DbContextOptionsBuilder<StarlightDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder {
                DataSource = databasePath,
                Pooling = false
            }.ToString())
            .Options;
        await using var db = new StarlightDbContext(options);
        Player player = await db.Players
            .AsNoTracking()
            .Include(candidate => candidate.Profile)
            .SingleAsync(candidate => candidate.Id == uid);
        return player.Serialize();
    }

    private static string FixturePath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);

    private static string CreateTestDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "StarlightExporter.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
