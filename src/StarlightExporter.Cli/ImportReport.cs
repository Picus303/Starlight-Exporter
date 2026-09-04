using System.Text.Json;
using System.Text.Json.Serialization;
using StarlightExporter.Mapping;
using StarlightExporter.Persistence;
using StarlightExporter.Snapshot;

namespace StarlightExporter.Cli;

public sealed record ImportReport(
    int SchemaVersion,
    string Result,
    string StarlightCommit,
    string ProtocolVersion,
    DateTimeOffset ImportedAtUtc,
    uint OfficialUid,
    uint PrivateUid,
    string PrivateAccountId,
    ImportCounts Source,
    ImportCounts Imported,
    IReadOnlyDictionary<string, int> Unsupported,
    IReadOnlyList<MappingIssue> Issues)
{
    public static ImportReport Create(
        OfficialSnapshot snapshot,
        StarlightMappingResult mapping,
        StarlightDatabaseWriteResult persisted)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(persisted);

        return new ImportReport(
            SchemaVersion: 1,
            Result: mapping.Issues.Count == 0 ? "success" : "success-with-warnings",
            snapshot.Manifest.StarlightCommit,
            snapshot.Manifest.ProtocolVersion,
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
