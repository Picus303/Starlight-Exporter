using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Starlight.Game.Resources;
using StarlightExporter.Mapping;
using StarlightExporter.Persistence;
using StarlightExporter.Snapshot;

namespace StarlightExporter.Cli;

public static class CliApplication
{
    public const int Success = 0;
    public const int UnexpectedError = 1;
    public const int InvalidUsage = 2;
    public const int InvalidSnapshot = 3;
    public const int InvalidResources = 4;
    public const int InvalidMapping = 5;
    public const int DatabaseError = 6;

    public static async Task<int> RunAsync(
        string[] arguments,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        try
        {
            return arguments switch {
                ["inspect", ..] => await InspectAsync(arguments, output, error, cancellationToken),
                ["build-db", ..] => await BuildDatabaseAsync(arguments, output, error, cancellationToken),
                _ => WriteUsage(error)
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await error.WriteLineAsync("Operation cancelled.");
            return UnexpectedError;
        }
        catch (Exception exception)
        {
            await error.WriteLineAsync($"Unexpected error: {exception.Message}");
            return UnexpectedError;
        }
    }

    private static async Task<int> InspectAsync(
        string[] arguments,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        if (!TryParseInspect(arguments, out InspectOptions options, out string? parseError))
        {
            await error.WriteLineAsync(parseError);
            return WriteUsage(error);
        }

        OfficialSnapshot snapshot;

        try
        {
            snapshot = await OfficialSnapshotSerializer.ReadAsync(options.SnapshotPath, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            await error.WriteLineAsync($"Unable to inspect snapshot: {exception.Message}");
            return InvalidSnapshot;
        }

        SnapshotValidationResult validation = SnapshotValidator.Validate(snapshot);
        if (!validation.IsValid)
        {
            await WriteSnapshotErrorsAsync(validation.Errors, error);
            return InvalidSnapshot;
        }

        await output.WriteLineAsync($"Snapshot schema: {snapshot.Manifest.SchemaVersion}");
        await output.WriteLineAsync($"Starlight commit: {snapshot.Manifest.StarlightCommit}");
        await output.WriteLineAsync($"Protocol: {snapshot.Manifest.ProtocolVersion}");
        await output.WriteLineAsync($"Official UID: {snapshot.Manifest.OfficialUid}");
        await output.WriteLineAsync($"Region: {snapshot.Manifest.Region}");
        await output.WriteLineAsync($"Captured at: {snapshot.Manifest.CapturedAtUtc:O}");
        await output.WriteLineAsync($"Materials: {snapshot.Materials.Count}");
        await output.WriteLineAsync($"Weapons: {snapshot.Weapons.Count}");
        await output.WriteLineAsync($"Avatars: {snapshot.Avatars.Count}");
        await output.WriteLineAsync($"Teams: {snapshot.Teams.Count}");
        await output.WriteLineAsync($"Unsupported records: {snapshot.Unsupported.Count}");

        if (options.ResourcesPath is null)
        {
            return Success;
        }

        if (!File.Exists(options.ResourcesPath) && !Directory.Exists(options.ResourcesPath))
        {
            await error.WriteLineAsync($"Target resources not found: '{options.ResourcesPath}'.");
            return InvalidResources;
        }

        GameData gameData;
        try
        {
            gameData = await LoadGameDataAsync(options.ResourcesPath, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await error.WriteLineAsync($"Unable to load target resources: {exception.Message}");
            return InvalidResources;
        }

        StarlightMappingResult mapping = new StarlightSnapshotMapper(gameData).Map(snapshot);
        await WriteMappingIssuesAsync(mapping.Issues, error);
        await output.WriteLineAsync(
            $"Mapped: {mapping.State.Materials.Count} materials, {mapping.State.Weapons.Count} weapons, "
            + $"{mapping.State.Avatars.Count} avatars, {mapping.State.AvatarTeams.Count} teams.");

        if (!SatisfiesMappingPolicy(mapping, options.Strict))
        {
            await error.WriteLineAsync("Mapping did not satisfy the selected policy.");
            return InvalidMapping;
        }

        await output.WriteLineAsync("Target compatibility: accepted.");
        return Success;
    }

    private static async Task<int> BuildDatabaseAsync(
        string[] arguments,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        if (!TryParseBuildDatabase(arguments, out BuildDatabaseOptions options, out string? parseError))
        {
            await error.WriteLineAsync(parseError);
            return WriteUsage(error);
        }

        if (!File.Exists(options.ResourcesPath) && !Directory.Exists(options.ResourcesPath))
        {
            await error.WriteLineAsync($"Target resources not found: '{options.ResourcesPath}'.");
            return InvalidResources;
        }

        if (File.Exists(options.OutputDirectory) || Directory.Exists(options.OutputDirectory))
        {
            await error.WriteLineAsync($"Output path already exists: '{options.OutputDirectory}'.");
            return DatabaseError;
        }

        if (options.AccountDatabasePath is not null)
        {
            PrivateAccountValidationResult accountValidation = await PrivateAccountValidator.ValidateExistsAsync(
                options.AccountDatabasePath,
                options.PrivateAccountId,
                cancellationToken);
            if (!accountValidation.IsValid)
            {
                await error.WriteLineAsync($"ERROR {accountValidation.Code}: {accountValidation.Message}");
                return DatabaseError;
            }

            await output.WriteLineAsync(accountValidation.Message);
        }

        OfficialSnapshot snapshot;
        try
        {
            snapshot = await OfficialSnapshotSerializer.ReadAsync(options.SnapshotPath, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            await error.WriteLineAsync($"Unable to read snapshot: {exception.Message}");
            return InvalidSnapshot;
        }

        GameData gameData;
        try
        {
            gameData = await LoadGameDataAsync(options.ResourcesPath, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await error.WriteLineAsync($"Unable to load target resources: {exception.Message}");
            return InvalidResources;
        }

        StarlightMappingResult mapping = new StarlightSnapshotMapper(gameData).Map(snapshot);
        await WriteMappingIssuesAsync(mapping.Issues, error);

        if (!SatisfiesMappingPolicy(mapping, options.Strict))
        {
            await error.WriteLineAsync("Mapping did not satisfy the selected policy.");
            return InvalidMapping;
        }

        try
        {
            StarlightDatabaseWriteResult result = await BuildOutputDirectoryAsync(
                options,
                snapshot,
                mapping,
                cancellationToken);
            await output.WriteLineAsync($"Database written: {Path.Combine(options.OutputDirectory, "starlight.db")}");
            await output.WriteLineAsync($"Import report written: {Path.Combine(options.OutputDirectory, "import-report.json")}");
            await output.WriteLineAsync(
                $"Imported UID {result.PlayerUid}: {result.MaterialCount} materials, "
                + $"{result.WeaponCount} weapons, {result.AvatarCount} avatars, {result.TeamCount} teams.");
            return Success;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await error.WriteLineAsync($"Unable to create database: {exception.Message}");
            return DatabaseError;
        }
    }

    private static async Task<StarlightDatabaseWriteResult> BuildOutputDirectoryAsync(
        BuildDatabaseOptions options,
        OfficialSnapshot snapshot,
        StarlightMappingResult mapping,
        CancellationToken cancellationToken)
    {
        string outputParent = Path.GetDirectoryName(options.OutputDirectory)
            ?? throw new ArgumentException("Output directory must have a parent directory.");
        Directory.CreateDirectory(outputParent);
        string temporaryDirectory = Path.Combine(
            outputParent,
            $".{Path.GetFileName(options.OutputDirectory)}.{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(temporaryDirectory);

        try
        {
            StarlightDatabaseWriteResult result = await StarlightDatabaseWriter.WriteNewAsync(
                new StarlightDatabaseWriteRequest(
                    Path.Combine(temporaryDirectory, "starlight.db"),
                    snapshot.Manifest.OfficialUid,
                    options.PrivateAccountId,
                    mapping),
                cancellationToken);

            ImportReport report = ImportReport.Create(snapshot, mapping, result);
            await ImportReportWriter.WriteAsync(
                Path.Combine(temporaryDirectory, "import-report.json"),
                report,
                cancellationToken);

            Directory.Move(temporaryDirectory, options.OutputDirectory);
            return result with {
                OutputPath = Path.Combine(options.OutputDirectory, "starlight.db")
            };
        }
        catch
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }

            throw;
        }
    }

    private static bool TryParseBuildDatabase(
        string[] arguments,
        out BuildDatabaseOptions options,
        out string? error)
    {
        options = null!;
        error = null;
        if (arguments.Length < 2 || string.IsNullOrWhiteSpace(arguments[1]))
        {
            error = "A snapshot path is required.";
            return false;
        }

        string? resources = null;
        string? output = null;
        string? accountId = null;
        string? accountDatabase = null;
        string uidMode = "preserve";
        bool strict = false;

        for (int index = 2; index < arguments.Length; index++)
        {
            string option = arguments[index];
            if (option == "--strict")
            {
                strict = true;
                continue;
            }

            if (index + 1 >= arguments.Length)
            {
                error = $"Missing value for option '{option}'.";
                return false;
            }

            string value = arguments[++index];
            switch (option)
            {
                case "--resources":
                    resources = value;
                    break;
                case "--output":
                    output = value;
                    break;
                case "--private-account-id":
                    accountId = value;
                    break;
                case "--accounts-db":
                    accountDatabase = value;
                    break;
                case "--uid-mode":
                    uidMode = value;
                    break;
                default:
                    error = $"Unknown option '{option}'.";
                    return false;
            }
        }

        if (string.IsNullOrWhiteSpace(resources)
            || string.IsNullOrWhiteSpace(output)
            || string.IsNullOrWhiteSpace(accountId))
        {
            error = "--resources, --output and --private-account-id are required.";
            return false;
        }

        if (!string.Equals(uidMode, "preserve", StringComparison.Ordinal))
        {
            error = "Only '--uid-mode preserve' is implemented.";
            return false;
        }

        options = new BuildDatabaseOptions(
            Path.GetFullPath(arguments[1]),
            Path.GetFullPath(resources),
            Path.GetFullPath(output),
            accountId,
            accountDatabase is null ? null : Path.GetFullPath(accountDatabase),
            strict);
        return true;
    }

    private static bool TryParseInspect(
        string[] arguments,
        out InspectOptions options,
        out string? error)
    {
        options = null!;
        error = null;
        if (arguments.Length < 2 || string.IsNullOrWhiteSpace(arguments[1]))
        {
            error = "A snapshot path is required.";
            return false;
        }

        string? resources = null;
        bool strict = false;
        for (int index = 2; index < arguments.Length; index++)
        {
            string option = arguments[index];
            if (option == "--strict")
            {
                strict = true;
                continue;
            }

            if (option != "--resources")
            {
                error = $"Unknown option '{option}'.";
                return false;
            }

            if (++index >= arguments.Length)
            {
                error = "Missing value for option '--resources'.";
                return false;
            }

            resources = arguments[index];
        }

        options = new InspectOptions(
            Path.GetFullPath(arguments[1]),
            resources is null ? null : Path.GetFullPath(resources),
            strict);
        return true;
    }

    private static async Task<GameData> LoadGameDataAsync(
        string resourcesPath,
        CancellationToken cancellationToken)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> {
                ["Game:ResourcesPath"] = resourcesPath
            })
            .Build();
        var gameData = new GameData(configuration);
        await gameData.StartAsync(cancellationToken);
        return gameData;
    }

    private static async Task WriteMappingIssuesAsync(
        IReadOnlyCollection<MappingIssue> issues,
        TextWriter output)
    {
        foreach (MappingIssue issue in issues)
        {
            await output.WriteLineAsync(
                $"{issue.Severity.ToString().ToUpperInvariant()} {issue.Code}: {issue.Message}");
        }
    }

    private static bool SatisfiesMappingPolicy(StarlightMappingResult mapping, bool strict) =>
        mapping.IsSuccess
        && (!strict || mapping.Issues.All(issue => issue.Severity != MappingIssueSeverity.Warning));

    private static async Task WriteSnapshotErrorsAsync(
        IReadOnlyCollection<SnapshotValidationError> errors,
        TextWriter output)
    {
        await output.WriteLineAsync($"Snapshot invalid ({errors.Count} error(s)):");
        foreach (SnapshotValidationError item in errors)
        {
            await output.WriteLineAsync($"  {item.Code}: {item.Message}");
        }
    }

    private static int WriteUsage(TextWriter output)
    {
        output.WriteLine("Usage:");
        output.WriteLine("  starlight-export inspect <snapshot.json> [--resources <resources.zip|directory>] [--strict]");
        output.WriteLine(
            "  starlight-export build-db <snapshot.json> --resources <resources.zip> "
            + "--output <directory> --private-account-id <id> [--accounts-db <accounts.db>] "
            + "[--uid-mode preserve] [--strict]");
        return InvalidUsage;
    }

    private sealed record BuildDatabaseOptions(
        string SnapshotPath,
        string ResourcesPath,
        string OutputDirectory,
        string PrivateAccountId,
        string? AccountDatabasePath,
        bool Strict);

    private sealed record InspectOptions(
        string SnapshotPath,
        string? ResourcesPath,
        bool Strict);
}
