using System.Text.Json;
using System.Text.Json.Serialization;
using StarlightExporter.Persistence;
using StarlightExporter.Snapshot;
using StarlightExporter.StarlightTarget;

namespace StarlightExporter.Cli;

public sealed record ImportReport(
    int SchemaVersion,
    string Result,
    int SourceSnapshotSchemaVersion,
    string SourceProtocolVersion,
    string TargetStarlightCommit,
    string TargetProtocolCommit,
    string TargetProtocolVersion,
    string? TargetResourcesRevision,
    DateTimeOffset ImportedAtUtc,
    uint OfficialUid,
    uint PrivateUid,
    string PrivateAccountId,
    ImportCounts Source,
    ImportCounts Imported,
    IReadOnlyDictionary<string, int> Unsupported,
    StarlightModuleValidationResult ModuleValidation,
    IReadOnlyList<MappingIssue> Issues)
{
    public static ImportReport Create(
        OfficialSnapshot snapshot,
        StarlightMappingResult mapping,
        StarlightDatabaseWriteResult persisted,
        StarlightModuleValidationResult moduleValidation,
        string? resourcesRevision)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(persisted);
        ArgumentNullException.ThrowIfNull(moduleValidation);

        StarlightTargetMetadata target = StarlightTargetMetadata.Current;
        return new ImportReport(
            SchemaVersion: 2,
            Result: mapping.Issues.Count == 0 ? "success" : "success-with-warnings",
            snapshot.Manifest.SchemaVersion,
            snapshot.Manifest.SourceProtocolVersion,
            target.StarlightCommit,
            target.ProtocolCommit,
            target.ProtocolVersion,
            resourcesRevision,
            DateTimeOffset.UtcNow,
            snapshot.Manifest.OfficialUid,
            persisted.PlayerUid,
            persisted.PrivateAccountId,
            new ImportCounts(
                snapshot.Materials.Count,
                snapshot.Weapons.Count,
                snapshot.Avatars.Count,
                snapshot.Teams.Count),
            new ImportCounts(
                persisted.MaterialCount,
                persisted.WeaponCount,
                persisted.AvatarCount,
                persisted.TeamCount),
            snapshot.Unsupported
                .GroupBy(item => item.Category, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
            moduleValidation,
            mapping.Issues);
    }
}

public sealed record ImportCounts(int Materials, int Weapons, int Avatars, int Teams);

public static class ImportReportWriter
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    public static async Task WriteAsync(
        string path,
        ImportReport report,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(report);

        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            useAsync: true);
        await JsonSerializer.SerializeAsync(stream, report, Options, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) {
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
