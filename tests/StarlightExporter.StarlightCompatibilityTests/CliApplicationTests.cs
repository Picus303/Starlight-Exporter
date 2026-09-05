using System.Text.Json;
using Microsoft.Data.Sqlite;
using StarlightExporter.Cli;
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
    public async Task AllocateUidModeIsRejectedUntilImplemented()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        int exitCode = await CliApplication.RunAsync(
            [
                "build-db",
                FixturePath("minimal-valid.json"),
                "--resources", "resources.zip",
                "--output", "output",
                "--private-account-id", "1",
                "--uid-mode", "allocate"
            ],
            output,
            error);

        Assert.Equal(CliApplication.InvalidUsage, exitCode);
        Assert.Contains("Only '--uid-mode preserve' is implemented", error.ToString(), StringComparison.Ordinal);
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

}
