using System.Globalization;
using System.Text.Json;
using StarlightExporter.Official;
using StarlightExporter.Persistence;
using StarlightExporter.Snapshot;
using StarlightExporter.StarlightTarget;

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
    public const int InvalidReplay = 7;

    public static Task<int> RunAsync(
        string[] arguments,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default) =>
        RunAsync(arguments, TextReader.Null, output, error, cancellationToken);

    public static async Task<int> RunAsync(
        string[] arguments,
        TextReader input,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        try
        {
            return arguments switch {
                ["inspect", ..] => await InspectAsync(arguments, output, error, cancellationToken),
                ["capture", ..] => await CaptureAsync(arguments, output, error, cancellationToken),
                ["build-db", ..] => await BuildDatabaseAsync(arguments, output, error, cancellationToken),
                ["export", ..] => await ExportAsync(arguments, input, output, error, cancellationToken),
                _ => WriteUsage(error),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await WriteErrorAsync(error, "CANCELLED", "Operation cancelled.");
            return UnexpectedError;
        }
        catch (Exception)
        {
            await WriteErrorAsync(error, "UNEXPECTED", "The operation failed unexpectedly.");
            return UnexpectedError;
        }
    }

    private static async Task<int> CaptureAsync(
        string[] arguments,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        if (!TryParseCapture(arguments, out CaptureOptions options, out string? parseError))
        {
            await error.WriteLineAsync(parseError);
            return WriteUsage(error);
        }

        try
        {
            OfficialSnapshot snapshot = await CaptureReplayAsync(
                options.ReplayPath,
                options.SnapshotOutputPath,
                cancellationToken);
            await output.WriteLineAsync($"Snapshot written: {options.SnapshotOutputPath}");
            await output.WriteLineAsync(
                $"Captured UID {snapshot.Manifest.OfficialUid}: {snapshot.Materials.Count} materials, "
                + $"{snapshot.Weapons.Count} weapons, {snapshot.Avatars.Count} avatars, {snapshot.Teams.Count} teams.");
            return Success;
        }
        catch (OfficialConnectivityException exception)
        {
            await WriteErrorAsync(
                error,
                OfficialConnectivityDiagnostic.Code(exception.Error),
                OfficialConnectivityDiagnostic.SafeMessage(exception.Error));
            return InvalidReplay;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or InvalidDataException)
        {
            await WriteErrorAsync(error, "SNAPSHOT_WRITE_FAILED", "The snapshot could not be published.");
            return InvalidSnapshot;
        }
    }

    private static async Task<int> ExportAsync(
        string[] arguments,
        TextReader input,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        if (!TryParseExport(arguments, out ExportOptions options, out string? parseError))
        {
            await error.WriteLineAsync(parseError);
            return WriteUsage(error);
        }

        OfficialSnapshot snapshot;
        try
        {
            snapshot = await CaptureReplayAsync(
                options.ReplayPath,
                options.SnapshotOutputPath,
                cancellationToken);
            await output.WriteLineAsync($"Snapshot written: {options.SnapshotOutputPath}");
        }
        catch (OfficialConnectivityException exception)
        {
            await WriteErrorAsync(
                error,
                OfficialConnectivityDiagnostic.Code(exception.Error),
                OfficialConnectivityDiagnostic.SafeMessage(exception.Error));
            return InvalidReplay;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or InvalidDataException)
        {
            await WriteErrorAsync(error, "SNAPSHOT_WRITE_FAILED", "The snapshot could not be published.");
            return InvalidSnapshot;
        }

        if (!File.Exists(options.ResourcesPath) && !Directory.Exists(options.ResourcesPath))
        {
            await WriteErrorAsync(error, "RESOURCES_NOT_FOUND", "Target resources were not found; the snapshot was retained.");
            return InvalidResources;
        }

        string? password = await input.ReadLineAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(password))
        {
            await WriteErrorAsync(
                error,
                "PRIVATE_PASSWORD_MISSING",
                "A private-server password must be supplied on standard input; the snapshot was retained.");
            return DatabaseError;
        }

        try
        {
            string username = options.PrivateUsername
                ?? $"traveler-{snapshot.Manifest.OfficialUid.ToString(CultureInfo.InvariantCulture)}";
            uint playerUid = PlayerUidAllocator.Resolve(options.UidMode, snapshot.Manifest.OfficialUid);
            DatabasePipelineResult result = await BuildDatabasePipelineAsync(
                new DatabasePipelineOptions(
                    options.ResourcesPath,
                    options.OutputDirectory,
                    playerUid,
                    PrivateAccountId: null,
                    CreatePrivateIdentity: new PrivateIdentityOptions(username, password),
                    options.Strict),
                snapshot,
                error,
                cancellationToken);

            if (result.ExitCode != Success)
            {
                await error.WriteLineAsync("The snapshot was retained.");
                return result.ExitCode;
            }

            PublishedBuildResult published = result.Published!;
            await WritePublishedBuildAsync(options.OutputDirectory, published, output);
            await output.WriteLineAsync(
                $"Private login: {published.PrivateAccount!.Username} (account ID {published.PrivateAccount.AccountId}).");
            return Success;
        }
        finally
        {
            password = string.Empty;
        }
    }

    private static async Task<OfficialSnapshot> CaptureReplayAsync(
        string replayPath,
        string snapshotOutputPath,
        CancellationToken cancellationToken)
    {
        SanitizedReplaySource replay = await SanitizedReplaySerializer.ReadAsync(replayPath, cancellationToken);
        OfficialSnapshot snapshot = await new OfficialSnapshotCollector().CollectAsync(
            replay.Context,
            replay,
            cancellationToken);
        await OfficialSnapshotSerializer.WriteNewAsync(snapshotOutputPath, snapshot, cancellationToken);
        return snapshot;
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
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or InvalidDataException)
        {
            await WriteErrorAsync(error, "SNAPSHOT_READ_FAILED", "The snapshot could not be read.");
            return InvalidSnapshot;
        }

        SnapshotValidationResult validation = SnapshotValidator.Validate(snapshot);
        if (!validation.IsValid)
        {
            await WriteSnapshotErrorsAsync(validation.Errors, error);
            return InvalidSnapshot;
        }

        await output.WriteLineAsync($"Snapshot schema: {snapshot.Manifest.SchemaVersion}");
        await output.WriteLineAsync($"Source protocol: {snapshot.Manifest.SourceProtocolVersion}");
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

        return await RunInspectPreflightAsync(
            snapshot,
            options.ResourcesPath,
            options.Strict,
            output,
            error,
            cancellationToken);
    }

    private static async Task<int> RunInspectPreflightAsync(
        OfficialSnapshot snapshot,
        string resourcesPath,
        bool strict,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(resourcesPath) && !Directory.Exists(resourcesPath))
        {
            await WriteErrorAsync(error, "RESOURCES_NOT_FOUND", "Target resources not found.");
            return InvalidResources;
        }

        StarlightTargetPreflightResult preflight;
        try
        {
            preflight = await StarlightTargetPreflight.RunAsync(snapshot, resourcesPath, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await WriteErrorAsync(error, "RESOURCES_INVALID", "Target resources could not be loaded.");
            return InvalidResources;
        }

        StarlightMappingResult mapping = preflight.Mapping;
        await WriteMappingIssuesAsync(mapping.Issues, error);
        await output.WriteLineAsync(
            $"Mapped: {mapping.State.Materials.Count} materials, {mapping.State.Weapons.Count} weapons, "
            + $"{mapping.State.Avatars.Count} avatars, {mapping.State.AvatarTeams.Count} teams.");

        if (!SatisfiesMappingPolicy(mapping, strict))
        {
            await WriteErrorAsync(error, "MAPPING_POLICY_FAILED", "Mapping did not satisfy the selected policy.");
            return InvalidMapping;
        }

        StarlightModuleValidationResult moduleValidation = preflight.ModuleValidation
            ?? throw new InvalidOperationException("Module validation did not run for a successful mapping.");
        await WriteModuleDiagnosticsAsync(moduleValidation.Diagnostics, error);
        if (!moduleValidation.IsCompatible)
        {
            await WriteErrorAsync(
                error,
                "MODULE_VALIDATION_FAILED",
                "Mapped state was rejected by the pinned Starlight modules.");
            return InvalidMapping;
        }

        await output.WriteLineAsync("Module compatibility: accepted.");
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

        OfficialSnapshot snapshot;
        try
        {
            snapshot = await OfficialSnapshotSerializer.ReadAsync(options.SnapshotPath, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or InvalidDataException)
        {
            await WriteErrorAsync(error, "SNAPSHOT_READ_FAILED", "The snapshot could not be read.");
            return InvalidSnapshot;
        }

        if (options.AccountDatabasePath is not null)
        {
            PrivateAccountValidationResult accountValidation = await PrivateAccountValidator.ValidateExistsAsync(
                options.AccountDatabasePath,
                options.PrivateAccountId,
                cancellationToken);
            if (!accountValidation.IsValid)
            {
                await WriteErrorAsync(error, accountValidation.Code, accountValidation.Message);
                return DatabaseError;
            }

            await output.WriteLineAsync(accountValidation.Message);
        }

        uint playerUid = PlayerUidAllocator.Resolve(options.UidMode, snapshot.Manifest.OfficialUid);
        DatabasePipelineResult pipeline = await BuildDatabasePipelineAsync(
            new DatabasePipelineOptions(
                options.ResourcesPath,
                options.OutputDirectory,
                playerUid,
                options.PrivateAccountId,
                CreatePrivateIdentity: null,
                options.Strict),
            snapshot,
            error,
            cancellationToken);
        if (pipeline.ExitCode != Success)
        {
            return pipeline.ExitCode;
        }

        await WritePublishedBuildAsync(options.OutputDirectory, pipeline.Published!, output);
        return Success;
    }

    private static async Task<DatabasePipelineResult> BuildDatabasePipelineAsync(
        DatabasePipelineOptions options,
        OfficialSnapshot snapshot,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(options.ResourcesPath) && !Directory.Exists(options.ResourcesPath))
        {
            await WriteErrorAsync(error, "RESOURCES_NOT_FOUND", "Target resources not found.");
            return new DatabasePipelineResult(InvalidResources, null);
        }

        if (File.Exists(options.OutputDirectory) || Directory.Exists(options.OutputDirectory))
        {
            await WriteErrorAsync(error, "OUTPUT_EXISTS", "The database output path already exists.");
            return new DatabasePipelineResult(DatabaseError, null);
        }

        StarlightTargetPreflightResult preflight;
        try
        {
            preflight = await StarlightTargetPreflight.RunAsync(snapshot, options.ResourcesPath, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await WriteErrorAsync(error, "RESOURCES_INVALID", "Target resources could not be loaded.");
            return new DatabasePipelineResult(InvalidResources, null);
        }

        StarlightMappingResult mapping = preflight.Mapping;
        await WriteMappingIssuesAsync(mapping.Issues, error);
        if (!SatisfiesMappingPolicy(mapping, options.Strict))
        {
            await WriteErrorAsync(error, "MAPPING_POLICY_FAILED", "Mapping did not satisfy the selected policy.");
            return new DatabasePipelineResult(InvalidMapping, null);
        }

        StarlightModuleValidationResult moduleValidation = preflight.ModuleValidation
            ?? throw new InvalidOperationException("Module validation did not run for a successful mapping.");
        await WriteModuleDiagnosticsAsync(moduleValidation.Diagnostics, error);
        if (!moduleValidation.IsCompatible)
        {
            await WriteErrorAsync(
                error,
                "MODULE_VALIDATION_FAILED",
                "Mapped state was rejected by the pinned Starlight modules.");
            return new DatabasePipelineResult(InvalidMapping, null);
        }

        try
        {
            PublishedBuildResult result = await BuildOutputDirectoryAsync(
                options,
                snapshot,
                mapping,
                moduleValidation,
                preflight.ResourcesRevision,
                cancellationToken);
            return new DatabasePipelineResult(Success, result);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await WriteErrorAsync(error, "DATABASE_CREATE_FAILED", "The database package could not be created.");
            return new DatabasePipelineResult(DatabaseError, null);
        }
    }

    private static async Task<PublishedBuildResult> BuildOutputDirectoryAsync(
        DatabasePipelineOptions options,
        OfficialSnapshot snapshot,
        StarlightMappingResult mapping,
        StarlightModuleValidationResult moduleValidation,
        string? resourcesRevision,
        CancellationToken cancellationToken)
    {
        string outputPath = Path.GetFullPath(options.OutputDirectory);
        string outputParent = Path.GetDirectoryName(outputPath)
            ?? throw new ArgumentException("Output directory must have a parent directory.");
        Directory.CreateDirectory(outputParent);
        string temporaryDirectory = Path.Combine(
            outputParent,
            $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.tmp");
        EnsureDirectChild(outputParent, temporaryDirectory);
        Directory.CreateDirectory(temporaryDirectory);

        try
        {
            PrivateAccountWriteResult? privateAccount = null;
            string privateAccountId;
            if (options.CreatePrivateIdentity is not null)
            {
                privateAccount = await PrivateAccountDatabaseWriter.WriteNewAsync(
                    Path.Combine(temporaryDirectory, "accounts.db"),
                    options.CreatePrivateIdentity.Username,
                    options.CreatePrivateIdentity.Password,
                    cancellationToken);
                privateAccountId = privateAccount.AccountId.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                privateAccountId = options.PrivateAccountId
                    ?? throw new InvalidOperationException("No private account identity was supplied.");
            }

            StarlightDatabaseWriteResult database = await StarlightDatabaseWriter.WriteNewAsync(
                new StarlightDatabaseWriteRequest(
                    Path.Combine(temporaryDirectory, "starlight.db"),
                    options.PlayerUid,
                    privateAccountId,
                    mapping.Profile,
                    mapping.State),
                cancellationToken);

            ImportReport report = ImportReport.Create(
                snapshot,
                mapping,
                database,
                moduleValidation,
                resourcesRevision);
            await ImportReportWriter.WriteAsync(
                Path.Combine(temporaryDirectory, "import-report.json"),
                report,
                cancellationToken);

            Directory.Move(temporaryDirectory, outputPath);
            return new PublishedBuildResult(
                database with { OutputPath = Path.Combine(outputPath, "starlight.db") },
                privateAccount is null
                    ? null
                    : privateAccount with { OutputPath = Path.Combine(outputPath, "accounts.db") });
        }
        catch
        {
            if (Directory.Exists(temporaryDirectory))
            {
                EnsureDirectChild(outputParent, temporaryDirectory);
                Directory.Delete(temporaryDirectory, recursive: true);
            }

            throw;
        }
    }

    private static async Task WritePublishedBuildAsync(
        string outputDirectory,
        PublishedBuildResult published,
        TextWriter output)
    {
        StarlightDatabaseWriteResult result = published.Database;
        await output.WriteLineAsync($"Database written: {Path.Combine(outputDirectory, "starlight.db")}");
        await output.WriteLineAsync($"Import report written: {Path.Combine(outputDirectory, "import-report.json")}");
        if (published.PrivateAccount is not null)
        {
            await output.WriteLineAsync(
                $"Private account database written: {Path.Combine(outputDirectory, "accounts.db")}");
        }

        await output.WriteLineAsync(
            $"Imported UID {result.PlayerUid}: {result.MaterialCount} materials, "
            + $"{result.WeaponCount} weapons, {result.AvatarCount} avatars, {result.TeamCount} teams.");
    }

    private static bool TryParseCapture(
        string[] arguments,
        out CaptureOptions options,
        out string? error)
    {
        options = null!;
        error = null;
        string? replay = null;
        string? output = null;

        if (!TryParseNamedValues(arguments, 1, (name, value) => {
                switch (name)
                {
                    case "--replay": replay = value; return true;
                    case "--output": output = value; return true;
                    default: return false;
                }
            }, out error))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(replay) || string.IsNullOrWhiteSpace(output))
        {
            error = "--replay and --output are required.";
            return false;
        }

        options = new CaptureOptions(Path.GetFullPath(replay), Path.GetFullPath(output));
        if (PathsEqual(options.ReplayPath, options.SnapshotOutputPath))
        {
            error = "Replay input and snapshot output must be different paths.";
            return false;
        }

        return true;
    }

    private static bool TryParseExport(
        string[] arguments,
        out ExportOptions options,
        out string? error)
    {
        options = null!;
        error = null;
        string? replay = null;
        string? snapshotOutput = null;
        string? resources = null;
        string? output = null;
        string? username = null;
        string uidModeText = "preserve";
        bool strict = false;
        bool passwordStdin = false;

        for (int index = 1; index < arguments.Length; index++)
        {
            string name = arguments[index];
            if (name == "--strict")
            {
                strict = true;
                continue;
            }
            if (name == "--private-password-stdin")
            {
                passwordStdin = true;
                continue;
            }
            if (name == "--private-password")
            {
                error = "Passwords are not accepted as command-line values; use --private-password-stdin.";
                return false;
            }
            if (++index >= arguments.Length)
            {
                error = "A command option is missing its value.";
                return false;
            }

            string value = arguments[index];
            switch (name)
            {
                case "--replay": replay = value; break;
                case "--snapshot-output": snapshotOutput = value; break;
                case "--resources": resources = value; break;
                case "--output": output = value; break;
                case "--private-username": username = value; break;
                case "--uid-mode": uidModeText = value; break;
                default:
                    error = "An unknown command option was supplied.";
                    return false;
            }
        }

        if (string.IsNullOrWhiteSpace(replay)
            || string.IsNullOrWhiteSpace(snapshotOutput)
            || string.IsNullOrWhiteSpace(resources)
            || string.IsNullOrWhiteSpace(output)
            || !passwordStdin)
        {
            error = "--replay, --snapshot-output, --resources, --output and --private-password-stdin are required.";
            return false;
        }
        if (!TryParseUidMode(uidModeText, out PlayerUidMode uidMode))
        {
            error = "--uid-mode must be 'preserve' or 'allocate'.";
            return false;
        }

        options = new ExportOptions(
            Path.GetFullPath(replay),
            Path.GetFullPath(snapshotOutput),
            Path.GetFullPath(resources),
            Path.GetFullPath(output),
            string.IsNullOrWhiteSpace(username) ? null : username,
            uidMode,
            strict);
        if (PathsEqual(options.ReplayPath, options.SnapshotOutputPath)
            || IsSameOrDescendant(options.SnapshotOutputPath, options.OutputDirectory))
        {
            error = "Snapshot output must be distinct from the replay and outside the database output directory.";
            return false;
        }

        return true;
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
        string uidModeText = "preserve";
        bool strict = false;

        for (int index = 2; index < arguments.Length; index++)
        {
            string name = arguments[index];
            if (name == "--strict")
            {
                strict = true;
                continue;
            }
            if (++index >= arguments.Length)
            {
                error = "A command option is missing its value.";
                return false;
            }

            string value = arguments[index];
            switch (name)
            {
                case "--resources": resources = value; break;
                case "--output": output = value; break;
                case "--private-account-id": accountId = value; break;
                case "--accounts-db": accountDatabase = value; break;
                case "--uid-mode": uidModeText = value; break;
                default:
                    error = "An unknown command option was supplied.";
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
        if (!TryParseUidMode(uidModeText, out PlayerUidMode uidMode))
        {
            error = "--uid-mode must be 'preserve' or 'allocate'.";
            return false;
        }

        options = new BuildDatabaseOptions(
            Path.GetFullPath(arguments[1]),
            Path.GetFullPath(resources),
            Path.GetFullPath(output),
            accountId,
            accountDatabase is null ? null : Path.GetFullPath(accountDatabase),
            uidMode,
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
            string name = arguments[index];
            if (name == "--strict")
            {
                strict = true;
                continue;
            }
            if (name != "--resources" || ++index >= arguments.Length)
            {
                error = "Inspect accepts only --resources <path> and --strict.";
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

    private static bool TryParseNamedValues(
        string[] arguments,
        int start,
        Func<string, string, bool> accept,
        out string? error)
    {
        error = null;
        for (int index = start; index < arguments.Length; index++)
        {
            string name = arguments[index];
            if (++index >= arguments.Length)
            {
                error = "A command option is missing its value.";
                return false;
            }
            if (!accept(name, arguments[index]))
            {
                error = "An unknown command option was supplied.";
                return false;
            }
        }

        return true;
    }

    private static bool TryParseUidMode(string value, out PlayerUidMode mode)
    {
        if (string.Equals(value, "preserve", StringComparison.Ordinal))
        {
            mode = PlayerUidMode.Preserve;
            return true;
        }
        if (string.Equals(value, "allocate", StringComparison.Ordinal))
        {
            mode = PlayerUidMode.Allocate;
            return true;
        }

        mode = default;
        return false;
    }

    private static void EnsureDirectChild(string parent, string child)
    {
        string normalizedParent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string normalizedChild = Path.GetFullPath(child);
        if (!normalizedChild.StartsWith(normalizedParent, StringComparison.OrdinalIgnoreCase)
            || !PathsEqual(Path.GetDirectoryName(normalizedChild)!, parent))
        {
            throw new InvalidOperationException("Temporary output escaped its expected parent directory.");
        }
    }

    private static bool IsSameOrDescendant(string path, string directory)
    {
        string normalizedPath = Path.GetFullPath(path);
        string normalizedDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return PathsEqual(normalizedPath, directory)
            || normalizedPath.StartsWith(normalizedDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(string first, string second) =>
        string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);

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

    private static async Task WriteModuleDiagnosticsAsync(
        IReadOnlyCollection<ModuleValidationDiagnostic> diagnostics,
        TextWriter output)
    {
        foreach (ModuleValidationDiagnostic diagnostic in diagnostics)
        {
            await output.WriteLineAsync($"ERROR {diagnostic.Code}: {diagnostic.Message}");
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

    private static Task WriteErrorAsync(TextWriter output, string code, string message) =>
        output.WriteLineAsync($"ERROR {code}: {message}");

    private static int WriteUsage(TextWriter output)
    {
        output.WriteLine("Usage:");
        output.WriteLine("  starlight-export inspect <snapshot.json> [--resources <resources.zip|directory>] [--strict]");
        output.WriteLine("  starlight-export capture --replay <replay.json> --output <snapshot.json>");
        output.WriteLine(
            "  starlight-export build-db <snapshot.json> --resources <resources.zip|directory> "
            + "--output <directory> --private-account-id <id> [--accounts-db <accounts.db>] "
            + "[--uid-mode preserve|allocate] [--strict]");
        output.WriteLine(
            "  starlight-export export --replay <replay.json> --snapshot-output <snapshot.json> "
            + "--resources <resources.zip|directory> --output <directory> --private-password-stdin "
            + "[--private-username <name>] [--uid-mode preserve|allocate] [--strict]");
        return InvalidUsage;
    }

    private sealed record CaptureOptions(string ReplayPath, string SnapshotOutputPath);

    private sealed record ExportOptions(
        string ReplayPath,
        string SnapshotOutputPath,
        string ResourcesPath,
        string OutputDirectory,
        string? PrivateUsername,
        PlayerUidMode UidMode,
        bool Strict);

    private sealed record BuildDatabaseOptions(
        string SnapshotPath,
        string ResourcesPath,
        string OutputDirectory,
        string PrivateAccountId,
        string? AccountDatabasePath,
        PlayerUidMode UidMode,
        bool Strict);

    private sealed record InspectOptions(string SnapshotPath, string? ResourcesPath, bool Strict);

    private sealed record PrivateIdentityOptions(string Username, string Password);

    private sealed record DatabasePipelineOptions(
        string ResourcesPath,
        string OutputDirectory,
        uint PlayerUid,
        string? PrivateAccountId,
        PrivateIdentityOptions? CreatePrivateIdentity,
        bool Strict);

    private sealed record PublishedBuildResult(
        StarlightDatabaseWriteResult Database,
        PrivateAccountWriteResult? PrivateAccount);

    private sealed record DatabasePipelineResult(int ExitCode, PublishedBuildResult? Published);
}
