using System.Text.Json;
using System.Text.Json.Serialization;

namespace StarlightExporter.Snapshot;

public static class OfficialSnapshotSerializer
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    public static async Task<OfficialSnapshot> ReadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        await using FileStream stream = File.OpenRead(path);
        OfficialSnapshot? snapshot = await JsonSerializer.DeserializeAsync<OfficialSnapshot>(
            stream,
            Options,
            cancellationToken);

        return snapshot ?? throw new JsonException("The snapshot document is empty.");
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
