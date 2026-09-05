using System.Reflection;
using System.Text.Json;

namespace StarlightExporter.StarlightTarget;

public sealed record StarlightTargetMetadata(
    int SchemaVersion,
    string StarlightCommit,
    string ProtocolCommit,
    string ProtocolVersion)
{
    private const string ResourceName =
        "StarlightExporter.StarlightTarget.starlight-target.lock.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Lazy<StarlightTargetMetadata> CurrentValue = new(Load);

    public static StarlightTargetMetadata Current => CurrentValue.Value;

    private static StarlightTargetMetadata Load()
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("The embedded Starlight target lock is missing.");
        StarlightTargetMetadata? metadata = JsonSerializer.Deserialize<StarlightTargetMetadata>(
            stream,
            JsonOptions);

        if (metadata is null
            || metadata.SchemaVersion != 1
            || string.IsNullOrWhiteSpace(metadata.StarlightCommit)
            || string.IsNullOrWhiteSpace(metadata.ProtocolCommit)
            || string.IsNullOrWhiteSpace(metadata.ProtocolVersion))
        {
            throw new InvalidDataException("The embedded Starlight target lock is invalid.");
        }

        return metadata;
    }
}
