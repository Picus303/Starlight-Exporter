using System.Text.Json;
using Microsoft.Data.Sqlite;
using Starlight.Protocol;
using StarlightExporter.Cli;
using StarlightExporter.Official;
using StarlightExporter.Persistence;
using StarlightExporter.StarlightTarget;
using Xunit;

namespace StarlightExporter.Tests;

[Collection("Real resources")]
public sealed class CliApplicationTests
{
    [Fact]
    public async Task BuildDatabasePublishesDatabaseAndReportAtomically()
    {
        string testDirectory = Path.Combine(
            Path.GetTempPath(),
            "StarlightExporter.Tests",
            Guid.NewGuid().ToString("N"));
        string resourcesDirectory = Path.Combine(testDirectory, "resources");
        string accountDatabasePath = Path.Combine(testDirectory, "accounts.db");
        string outputDirectory = Path.Combine(testDirectory, "output");
        Directory.CreateDirectory(testDirectory);
        TestResourceDirectory.Create(resourcesDirectory);
        await TestAccountDatabase.CreateAsync(accountDatabasePath, 1);
        using var output = new StringWriter();
        using var error = new StringWriter();

        try
        {
            int exitCode = await CliApplication.RunAsync(
                [
                    "build-db",
                    FixturePath("minimal-valid.json"),
                    "--resources", resourcesDirectory,
                    "--output", outputDirectory,
                    "--private-account-id", "1",
                    "--accounts-db", accountDatabasePath
                ],
                output,
                error);

            Assert.Equal(CliApplication.Success, exitCode);
            Assert.Contains("Private account 1 exists", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("WARNING TEAM_SLOTS_COMPLETED", error.ToString(), StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(outputDirectory, "starlight.db")));
            string reportPath = Path.Combine(outputDirectory, "import-report.json");
            Assert.True(File.Exists(reportPath));

            string reportJson = await File.ReadAllTextAsync(reportPath);
            using JsonDocument report = JsonDocument.Parse(reportJson);
            Assert.Equal("success-with-warnings", report.RootElement.GetProperty("result").GetString());
            Assert.Equal(expected: 2, report.RootElement.GetProperty("sourceSnapshotSchemaVersion").GetInt32());
            Assert.Equal("V70", report.RootElement.GetProperty("sourceProtocolVersion").GetString());
            Assert.Equal(
                StarlightTargetMetadata.Current.StarlightCommit,
                report.RootElement.GetProperty("targetStarlightCommit").GetString());
            Assert.Equal(
                StarlightTargetMetadata.Current.ProtocolCommit,
                report.RootElement.GetProperty("targetProtocolCommit").GetString());
            Assert.Equal(JsonValueKind.Null, report.RootElement.GetProperty("targetResourcesRevision").ValueKind);
            Assert.Equal(expected: 765432100u, report.RootElement.GetProperty("privateUid").GetUInt32());
            Assert.Equal("1", report.RootElement.GetProperty("privateAccountId").GetString());
            Assert.Equal(expected: 4, report.RootElement.GetProperty("imported").GetProperty("teams").GetInt32());
            JsonElement moduleValidation = report.RootElement.GetProperty("moduleValidation");
            Assert.True(moduleValidation.GetProperty("isCompatible").GetBoolean());
            Assert.True(moduleValidation.GetProperty("statePreserved").GetBoolean());
            Assert.True(moduleValidation.GetProperty("storeNotificationMatches").GetBoolean());
            Assert.True(moduleValidation.GetProperty("avatarNotificationMatches").GetBoolean());
            Assert.Empty(moduleValidation.GetProperty("repairNotifications").EnumerateArray());
            Assert.DoesNotContain("password", reportJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("token", reportJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("cookie", reportJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("authorization", reportJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("privateKey", reportJson, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(
                ["import-report.json", "starlight.db"],
                Directory.EnumerateFiles(outputDirectory).Select(path => Path.GetFileName(path)!).Order().ToArray());
            Assert.Empty(Directory.EnumerateDirectories(testDirectory, ".*.tmp"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task InspectValidSnapshotReturnsSuccess()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        int exitCode = await CliApplication.RunAsync(
            ["inspect", FixturePath("minimal-valid.json")],
            output,
            error);

        Assert.Equal(CliApplication.Success, exitCode);
        Assert.Contains("Official UID: 765432100", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task InspectWithResourcesPerformsMappingPreflightWithoutWritingFiles()
    {
        string testDirectory = Path.Combine(
            Path.GetTempPath(),
            "StarlightExporter.Tests",
            Guid.NewGuid().ToString("N"));
        string resourcesDirectory = Path.Combine(testDirectory, "resources");
        Directory.CreateDirectory(testDirectory);
        TestResourceDirectory.Create(resourcesDirectory);
        using var output = new StringWriter();
        using var error = new StringWriter();

        try
        {
            int exitCode = await CliApplication.RunAsync(
                ["inspect", FixturePath("minimal-valid.json"), "--resources", resourcesDirectory],
                output,
                error);

            Assert.Equal(CliApplication.Success, exitCode);
            Assert.Contains("Mapped: 1 materials, 1 weapons, 1 avatars, 4 teams", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Target compatibility: accepted", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Module compatibility: accepted", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("WARNING TEAM_SLOTS_COMPLETED", error.ToString(), StringComparison.Ordinal);
            Assert.Equal(
                ["resources"],
                Directory.EnumerateFileSystemEntries(testDirectory).Select(path => Path.GetFileName(path)!).ToArray());
        }
        finally
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    [RealResourcesFact]
    public async Task PinnedRealResourceArchiveLoadsWhenAvailable()
    {
        string archivePath = RealResourceArchive.Find()!;

        LoadedStarlightGameData loaded = await StarlightGameDataLoader.LoadAsync(archivePath);

        Assert.True(loaded.Data.MaterialData.Count > 100);
        Assert.True(loaded.Data.WeaponData.Count > 100);
        Assert.True(loaded.Data.AvatarData.Count > 50);
        Assert.Contains(expected: 11101u, loaded.Data.WeaponData.Keys);
        Assert.Contains(expected: 10000005u, loaded.Data.AvatarData.Keys);
        Assert.Equal("8d7ee68210c5b9a8375db80b858b186a6f6d3731", loaded.ResourcesRevision);
    }

    [Fact]
    public async Task BuildDatabaseWithMissingResourcesReturnsResourceError()
    {
        string outputDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        using var output = new StringWriter();
        using var error = new StringWriter();

        int exitCode = await CliApplication.RunAsync(
            [
                "build-db",
                FixturePath("minimal-valid.json"),
                "--resources", Path.Combine(outputDirectory, "missing-resources.zip"),
                "--output", outputDirectory,
                "--private-account-id", "1"
            ],
            output,
            error);

        Assert.Equal(CliApplication.InvalidResources, exitCode);
        Assert.Contains("Target resources not found", error.ToString(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(outputDirectory));
    }

    [Fact]
    public async Task BuildDatabaseRefusesMissingPrivateAccountBeforeCreatingOutput()
    {
        string testDirectory = Path.Combine(
            Path.GetTempPath(),
            "StarlightExporter.Tests",
            Guid.NewGuid().ToString("N"));
        string resourcesDirectory = Path.Combine(testDirectory, "resources");
        string accountDatabasePath = Path.Combine(testDirectory, "accounts.db");
        string outputDirectory = Path.Combine(testDirectory, "output");
        Directory.CreateDirectory(testDirectory);
        TestResourceDirectory.Create(resourcesDirectory);
        await TestAccountDatabase.CreateAsync(accountDatabasePath, 7);
        using var output = new StringWriter();
        using var error = new StringWriter();

        try
        {
            int exitCode = await CliApplication.RunAsync(
                [
                    "build-db",
                    FixturePath("minimal-valid.json"),
                    "--resources", resourcesDirectory,
                    "--output", outputDirectory,
                    "--private-account-id", "8",
                    "--accounts-db", accountDatabasePath
                ],
                output,
                error);

            Assert.Equal(CliApplication.DatabaseError, exitCode);
            Assert.Contains("ERROR ACCOUNT_NOT_FOUND", error.ToString(), StringComparison.Ordinal);
            Assert.False(Directory.Exists(outputDirectory));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task AllocateUidModeUsesDbGateStartingRange()
    {
        string testDirectory = Path.Combine(
            Path.GetTempPath(),
            "StarlightExporter.Tests",
            Guid.NewGuid().ToString("N"));
        string resourcesDirectory = Path.Combine(testDirectory, "resources");
        string outputDirectory = Path.Combine(testDirectory, "output");
        Directory.CreateDirectory(testDirectory);
        TestResourceDirectory.Create(resourcesDirectory);
        using var output = new StringWriter();
        using var error = new StringWriter();

        try
        {
            int exitCode = await CliApplication.RunAsync(
                [
                    "build-db",
                    FixturePath("minimal-valid.json"),
                    "--resources", resourcesDirectory,
                    "--output", outputDirectory,
                    "--private-account-id", "1",
                    "--uid-mode", "allocate"
                ],
                output,
                error);

            Assert.Equal(CliApplication.Success, exitCode);
            string reportJson = await File.ReadAllTextAsync(Path.Combine(outputDirectory, "import-report.json"));
            using JsonDocument report = JsonDocument.Parse(reportJson);
            Assert.Equal(PlayerUidAllocator.FirstAllocatedUid, report.RootElement.GetProperty("privateUid").GetUInt32());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task CaptureReplayPublishesAValidatedSnapshot()
    {
        string testDirectory = CreateTestDirectory();
        string replayPath = Path.Combine(testDirectory, "capture.replay.json");
        string snapshotPath = Path.Combine(testDirectory, "snapshot.json");
        await WriteReplayAsync(replayPath);
        using var output = new StringWriter();
        using var error = new StringWriter();

        try
        {
            int exitCode = await CliApplication.RunAsync(
                ["capture", "--replay", replayPath, "--output", snapshotPath],
                output,
                error);

            Assert.Equal(CliApplication.Success, exitCode);
            Assert.True(File.Exists(snapshotPath));
            Assert.Equal(123456789u, (await Snapshot.OfficialSnapshotSerializer.ReadAsync(snapshotPath)).Manifest.OfficialUid);
            Assert.Equal(string.Empty, error.ToString());
        }
        finally
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task CaptureReplayRefusesOverwriteAndPreservesExistingSnapshot()
    {
        string testDirectory = CreateTestDirectory();
        string replayPath = Path.Combine(testDirectory, "capture.replay.json");
        string snapshotPath = Path.Combine(testDirectory, "snapshot.json");
        await WriteReplayAsync(replayPath);
        await File.WriteAllTextAsync(snapshotPath, "existing");
        using var output = new StringWriter();
        using var error = new StringWriter();

        try
        {
            int exitCode = await CliApplication.RunAsync(
                ["capture", "--replay", replayPath, "--output", snapshotPath],
                output,
                error);

            Assert.Equal(CliApplication.InvalidSnapshot, exitCode);
            Assert.Equal("existing", await File.ReadAllTextAsync(snapshotPath));
            Assert.Contains("SNAPSHOT_WRITE_FAILED", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task CaptureReportsMissingReplayAsReplayFailure()
    {
        string testDirectory = CreateTestDirectory();
        string snapshotPath = Path.Combine(testDirectory, "snapshot.json");
        using var output = new StringWriter();
        using var error = new StringWriter();

        try
        {
            int exitCode = await CliApplication.RunAsync(
                [
                    "capture",
                    "--replay", Path.Combine(testDirectory, "missing.replay.json"),
                    "--output", snapshotPath
                ],
                output,
                error);

            Assert.Equal(CliApplication.InvalidReplay, exitCode);
            Assert.False(File.Exists(snapshotPath));
            Assert.Contains("REPLAY_INVALID", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ExportReplayCreatesPrivateIdentityDatabaseAndPlayerPackage()
    {
        const string privatePassword = "local-private-password";
        string testDirectory = CreateTestDirectory();
        string replayPath = Path.Combine(testDirectory, "capture.replay.json");
        string snapshotPath = Path.Combine(testDirectory, "snapshot.json");
        string resourcesDirectory = Path.Combine(testDirectory, "resources");
        string outputDirectory = Path.Combine(testDirectory, "output");
        await WriteReplayAsync(replayPath);
        TestResourceDirectory.Create(resourcesDirectory);
        using var input = new StringReader(privatePassword + Environment.NewLine);
        using var output = new StringWriter();
        using var error = new StringWriter();

        try
        {
            int exitCode = await CliApplication.RunAsync(
                [
                    "export",
                    "--replay", replayPath,
                    "--snapshot-output", snapshotPath,
                    "--resources", resourcesDirectory,
                    "--output", outputDirectory,
                    "--private-password-stdin",
                    "--uid-mode", "allocate"
                ],
                input,
                output,
                error);

            Assert.Equal(CliApplication.Success, exitCode);
            Assert.True(File.Exists(snapshotPath));
            Assert.Equal(
                ["accounts.db", "import-report.json", "starlight.db"],
                Directory.EnumerateFiles(outputDirectory).Select(path => Path.GetFileName(path)!).Order().ToArray());
            PrivateAccountValidationResult account = await PrivateAccountValidator.ValidateExistsAsync(
                Path.Combine(outputDirectory, "accounts.db"),
                "1");
            Assert.True(account.IsValid);

            string reportJson = await File.ReadAllTextAsync(Path.Combine(outputDirectory, "import-report.json"));
            using JsonDocument report = JsonDocument.Parse(reportJson);
            Assert.Equal(PlayerUidAllocator.FirstAllocatedUid, report.RootElement.GetProperty("privateUid").GetUInt32());
            Assert.Equal("1", report.RootElement.GetProperty("privateAccountId").GetString());
            Assert.DoesNotContain(privatePassword, output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(privatePassword, error.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(privatePassword, reportJson, StringComparison.Ordinal);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ExportRetainsSnapshotWhenDatabaseStageFails()
    {
        string testDirectory = CreateTestDirectory();
        string replayPath = Path.Combine(testDirectory, "capture.replay.json");
        string snapshotPath = Path.Combine(testDirectory, "snapshot.json");
        string outputDirectory = Path.Combine(testDirectory, "output");
        await WriteReplayAsync(replayPath);
        using var input = new StringReader("must-not-leak" + Environment.NewLine);
        using var output = new StringWriter();
        using var error = new StringWriter();

        try
        {
            int exitCode = await CliApplication.RunAsync(
                [
                    "export",
                    "--replay", replayPath,
                    "--snapshot-output", snapshotPath,
                    "--resources", Path.Combine(testDirectory, "missing-resources"),
                    "--output", outputDirectory,
                    "--private-password-stdin"
                ],
                input,
                output,
                error);

            Assert.Equal(CliApplication.InvalidResources, exitCode);
            Assert.True(File.Exists(snapshotPath));
            Assert.False(Directory.Exists(outputDirectory));
            Assert.Contains("snapshot was retained", error.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("must-not-leak", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ExportRejectsPasswordArgumentWithoutEchoingItsValue()
    {
        const string secret = "must-not-echo";
        using var output = new StringWriter();
        using var error = new StringWriter();

        int exitCode = await CliApplication.RunAsync(
            ["export", "--private-password", secret],
            output,
            error);

        Assert.Equal(CliApplication.InvalidUsage, exitCode);
        Assert.DoesNotContain(secret, error.ToString(), StringComparison.Ordinal);
        Assert.Contains("--private-password-stdin", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportRejectsSnapshotInsideAtomicDatabaseDirectory()
    {
        string testDirectory = CreateTestDirectory();
        string replayPath = Path.Combine(testDirectory, "capture.replay.json");
        string outputDirectory = Path.Combine(testDirectory, "output");
        await WriteReplayAsync(replayPath);
        using var output = new StringWriter();
        using var error = new StringWriter();

        try
        {
            int exitCode = await CliApplication.RunAsync(
                [
                    "export",
                    "--replay", replayPath,
                    "--snapshot-output", Path.Combine(outputDirectory, "snapshot.json"),
                    "--resources", Path.Combine(testDirectory, "resources"),
                    "--output", outputDirectory,
                    "--private-password-stdin"
                ],
                output,
                error);

            Assert.Equal(CliApplication.InvalidUsage, exitCode);
            Assert.False(Directory.Exists(outputDirectory));
            Assert.Contains("outside the database output directory", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task CancellationReturnsStableErrorWithoutPublishingOutput()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        int exitCode = await CliApplication.RunAsync(
            ["inspect", FixturePath("minimal-valid.json")],
            output,
            error,
            cancellation.Token);

        Assert.Equal(CliApplication.UnexpectedError, exitCode);
        Assert.Contains("Operation cancelled", error.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, output.ToString());
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

    private static Task WriteReplayAsync(string path)
    {
        var weapon = new Weapon { Level = 20, PromoteLevel = 0 };
        weapon.AffixMap[101] = 2;
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
        OfficialMessageEnvelope[] messages = [
            new(1, new PlayerDataNotify { NickName = "Traveler" }),
            new(2, new PlayerStoreNotify {
                ItemList = {
                    new Item { ItemId = 1001, Guid = 100, Material = new Material { Count = 5 } },
                    new Item {
                        ItemId = 11101,
                        Guid = 200,
                        Equip = new Equip { Weapon = weapon },
                    },
                },
            }),
            new(3, new AvatarDataNotify {
                CurAvatarTeamId = 1,
                ChooseAvatarGuid = 300,
                AvatarList = { avatar },
                AvatarTeamMap = {
                    [1] = new AvatarTeam { TeamName = "Main", AvatarGuidList = { 300 } },
                },
            }),
        ];
        var context = new OfficialCaptureContext(
            123456789,
            "os_euro",
            new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero),
            new OfficialProfileSupplement("Synthetic profile", 10000005, 210001));
        return SanitizedReplaySerializer.WriteNewAsync(path, context, messages);
    }

}
