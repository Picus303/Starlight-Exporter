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
        if (stream.Length > SnapshotContract.MaximumDocumentBytes)
        {
            throw new InvalidDataException(
                $"The snapshot exceeds the {SnapshotContract.MaximumDocumentBytes}-byte limit.");
        }

        OfficialSnapshot? snapshot = await JsonSerializer.DeserializeAsync<OfficialSnapshot>(
            stream,
            Options,
            cancellationToken);

        return snapshot ?? throw new JsonException("The snapshot document is empty.");
    }

    public static async Task WriteNewAsync(
        string path,
        OfficialSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();

        SnapshotValidationResult validation = SnapshotValidator.Validate(snapshot);
        if (!validation.IsValid)
        {
            string codes = string.Join(", ", validation.Errors.Select(error => error.Code));
            throw new InvalidDataException($"The snapshot is invalid and cannot be written: {codes}.");
        }

        string outputPath = Path.GetFullPath(path);
        if (File.Exists(outputPath) || Directory.Exists(outputPath))
        {
            throw new IOException($"The snapshot output path already exists: '{outputPath}'.");
        }

        OfficialSnapshot canonical = Canonicalize(snapshot);
        byte[] document = JsonSerializer.SerializeToUtf8Bytes(canonical, Options);
        if (document.LongLength > SnapshotContract.MaximumDocumentBytes)
        {
            throw new InvalidDataException(
                $"The snapshot exceeds the {SnapshotContract.MaximumDocumentBytes}-byte limit.");
        }

        SnapshotSecurityGuard.EnsureNoSensitiveProperties(document);
        cancellationToken.ThrowIfCancellationRequested();

        string outputDirectory = Path.GetDirectoryName(outputPath)
            ?? throw new ArgumentException("The snapshot output path must have a parent directory.", nameof(path));
        Directory.CreateDirectory(outputDirectory);
        string temporaryPath = Path.Combine(
            outputDirectory,
            $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(document, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, outputPath);
        }
        catch
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            throw;
        }
    }

    private static OfficialSnapshot Canonicalize(OfficialSnapshot snapshot) => snapshot with {
        Materials = [.. snapshot.Materials.OrderBy(item => item.ItemId).ThenBy(item => item.Guid)],
        Weapons = [.. snapshot.Weapons.OrderBy(item => item.Guid).ThenBy(item => item.ItemId)],
        Avatars = [.. snapshot.Avatars.OrderBy(item => item.Guid).ThenBy(item => item.AvatarId)],
        Teams = [.. snapshot.Teams.OrderBy(team => team.TeamId)],
        Unsupported = [.. snapshot.Unsupported
            .OrderBy(item => item.Category, StringComparer.Ordinal)
            .ThenBy(item => item.Identifier, StringComparer.Ordinal)
            .ThenBy(item => item.Reason, StringComparer.Ordinal)]
    };

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = true,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
