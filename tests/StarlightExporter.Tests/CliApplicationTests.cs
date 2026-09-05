using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Starlight.Game.Resources;
using StarlightExporter.Cli;
using Xunit;

namespace StarlightExporter.Tests;

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
            Assert.Equal(expected: 765432100u, report.RootElement.GetProperty("privateUid").GetUInt32());
            Assert.Equal("1", report.RootElement.GetProperty("privateAccountId").GetString());
            Assert.Equal(expected: 4, report.RootElement.GetProperty("imported").GetProperty("teams").GetInt32());
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

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> {
                ["Game:ResourcesPath"] = archivePath
            })
            .Build();
        var gameData = new GameData(configuration);

        await gameData.StartAsync(CancellationToken.None);

        Assert.True(gameData.MaterialData.Count > 100);
        Assert.True(gameData.WeaponData.Count > 100);
        Assert.True(gameData.AvatarData.Count > 50);
        Assert.Contains(expected: 11101u, gameData.WeaponData.Keys);
        Assert.Contains(expected: 10000005u, gameData.AvatarData.Keys);
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

    private static string FixturePath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);

}

internal sealed class RealResourcesFactAttribute : FactAttribute
{
    public RealResourcesFactAttribute()
    {
        if (RealResourceArchive.Find() is null)
        {
            Skip = "Set STARLIGHT_EXPORTER_TEST_RESOURCES or run scripts/prepare-resources.ps1 to enable this integration test.";
        }
    }
}

internal static class RealResourceArchive
{
    public static string? Find()
    {
        string? configuredPath = Environment.GetEnvironmentVariable("STARLIGHT_EXPORTER_TEST_RESOURCES");
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            string fullPath = Path.GetFullPath(configuredPath);
            return File.Exists(fullPath) ? fullPath : null;
        }

        string? repositoryRoot = FindRepositoryRoot();
        string archivePath = repositoryRoot is null
            ? string.Empty
            : Path.Combine(repositoryRoot, ".local", "resources", "resources.zip");
        return File.Exists(archivePath) ? archivePath : null;
    }

    private static string? FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "StarlightExporter.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName;
    }
}
