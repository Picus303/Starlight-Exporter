using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Starlight.Game.Resources;

namespace StarlightExporter.StarlightTarget;

public sealed record LoadedStarlightGameData(GameData Data, string? ResourcesRevision);

public static class StarlightGameDataLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<LoadedStarlightGameData> LoadAsync(
        string resourcesPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourcesPath);

        string fullPath = Path.GetFullPath(resourcesPath);
        if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
        {
            throw new FileNotFoundException("Target resources were not found.", fullPath);
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> {
                ["Game:ResourcesPath"] = fullPath
            })
            .Build();
        var gameData = new GameData(configuration);
        await gameData.StartAsync(cancellationToken);

        return new LoadedStarlightGameData(
            gameData,
            await ReadRevisionAsync(fullPath, cancellationToken));
    }

    private static async Task<string?> ReadRevisionAsync(string resourcesPath, CancellationToken cancellationToken)
    {
        string metadataPath = resourcesPath + ".metadata.json";
        if (!File.Exists(metadataPath))
        {
            return null;
        }

        await using FileStream stream = File.OpenRead(metadataPath);
        ResourceMetadata? metadata = await JsonSerializer.DeserializeAsync<ResourceMetadata>(
            stream,
            JsonOptions,
            cancellationToken);
        return string.IsNullOrWhiteSpace(metadata?.Revision) ? null : metadata.Revision;
    }

    private sealed record ResourceMetadata(string? Revision);
}
