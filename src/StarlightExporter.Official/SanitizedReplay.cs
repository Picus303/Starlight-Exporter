using Google.Protobuf;
using Starlight.Protobuf.Core;
using Starlight.Protobuf.Registry;
using Starlight.Protocol;
using Starlight.Protocol.V70;
using StarlightExporter.Snapshot;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StarlightExporter.Official;

public sealed class SanitizedReplaySource : IOfficialMessageSource
{
    internal SanitizedReplaySource(
        OfficialCaptureContext context,
        IReadOnlyList<OfficialMessageEnvelope> messages)
    {
        Context = context;
        Messages = messages;
    }

    public OfficialCaptureContext Context { get; }
    public IReadOnlyList<OfficialMessageEnvelope> Messages { get; }

    public async IAsyncEnumerable<OfficialMessageEnvelope> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (OfficialMessageEnvelope message in Messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return message;
        }

        await Task.CompletedTask;
    }
}

public static class SanitizedReplaySerializer
{
    public const int CurrentSchemaVersion = 1;

    private const long MaximumDocumentBytes = 16 * 1024 * 1024;
    private const int MaximumMessages = 4096;
    private const int MaximumMessageBytes = 1024 * 1024;

    private static readonly ProtocolRegistry Registry = new V70ProtocolRegistry();

    private static readonly Dictionary<Type, string> AllowedTypes =
        new Dictionary<Type, string> {
            [typeof(PlayerDataNotify)] = nameof(PlayerDataNotify),
            [typeof(PlayerStoreNotify)] = nameof(PlayerStoreNotify),
            [typeof(AvatarDataNotify)] = nameof(AvatarDataNotify),
        };

    private static readonly Dictionary<string, Type> AllowedNames = AllowedTypes
        .ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) {
        PropertyNameCaseInsensitive = false,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };

    public static async Task WriteNewAsync(
        string path,
        OfficialCaptureContext context,
        IEnumerable<OfficialMessageEnvelope> messages,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(messages);
        ValidateContext(context);

        OfficialMessageEnvelope[] ordered = [.. messages.Take(MaximumMessages + 1)];
        if (ordered.Length is 0 or > MaximumMessages)
        {
            throw ReplayFailure($"A replay must contain between 1 and {MaximumMessages} messages.");
        }

        EnsureStrictSequence(ordered.Select(message => message.Sequence));

        var records = new List<ReplayMessageRecord>(ordered.Length);
        foreach (OfficialMessageEnvelope envelope in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Type messageType = envelope.Message.GetType();
            if (!AllowedTypes.TryGetValue(messageType, out string? typeName))
            {
                throw ReplayFailure($"Message type '{messageType.Name}' is not allowed in a sanitized replay.");
            }

            byte[] body = Registry.Serialize(envelope.Message);
            if (body.Length > MaximumMessageBytes)
            {
                throw ReplayFailure($"Message {envelope.Sequence} exceeds the replay message size limit.");
            }

            records.Add(new ReplayMessageRecord {
                Sequence = envelope.Sequence,
                CmdId = Registry.GetCmdId(envelope.Message),
                MessageType = typeName,
                BodyBase64 = Convert.ToBase64String(body),
            });
        }

        var document = new ReplayDocument {
            SchemaVersion = CurrentSchemaVersion,
            ProtocolVersion = Registry.Version,
            CapturedAtUtc = context.CapturedAtUtc,
            Region = context.Region,
            OfficialUid = context.OfficialUid,
            Profile = context.Profile is null
                ? null
                : new ReplayProfileRecord {
                    Signature = context.Profile.Signature,
                    PictureId = context.Profile.PictureId,
                    NameCardId = context.Profile.NameCardId,
                },
            Messages = records,
        };

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        if (json.LongLength > MaximumDocumentBytes)
        {
            throw ReplayFailure("The replay exceeds the document size limit.");
        }

        SnapshotSecurityGuard.EnsureNoSensitiveProperties(json);
        await WriteAtomicallyAsync(path, json, cancellationToken);
    }

    public static async Task<SanitizedReplaySource> ReadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var file = new FileInfo(path);
        if (file.Length > MaximumDocumentBytes)
        {
            throw ReplayFailure("The replay exceeds the document size limit.");
        }

        byte[] json = await File.ReadAllBytesAsync(path, cancellationToken);
        try
        {
            SnapshotSecurityGuard.EnsureNoSensitiveProperties(json);
        }
        catch (InvalidDataException exception)
        {
            throw ReplayFailure("The replay contains a forbidden sensitive property.", exception);
        }

        ReplayDocument document;
        try
        {
            document = JsonSerializer.Deserialize<ReplayDocument>(json, JsonOptions)
                ?? throw new JsonException("The replay document is empty.");
        }
        catch (JsonException exception)
        {
            throw ReplayFailure("The replay JSON is invalid.", exception);
        }

        if (document.SchemaVersion != CurrentSchemaVersion
            || !string.Equals(document.ProtocolVersion, Registry.Version, StringComparison.Ordinal))
        {
            throw ReplayFailure("The replay schema or protocol version is unsupported.");
        }

        var context = new OfficialCaptureContext(
            document.OfficialUid,
            document.Region,
            document.CapturedAtUtc,
            document.Profile is null
                ? null
                : new OfficialProfileSupplement(
                    document.Profile.Signature,
                    document.Profile.PictureId,
                    document.Profile.NameCardId));
        ValidateContext(context);

        if (document.Messages.Count is 0 or > MaximumMessages)
        {
            throw ReplayFailure($"A replay must contain between 1 and {MaximumMessages} messages.");
        }

        EnsureStrictSequence(document.Messages.Select(message => message.Sequence));

        var messages = new List<OfficialMessageEnvelope>(document.Messages.Count);
        foreach (ReplayMessageRecord record in document.Messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!AllowedNames.TryGetValue(record.MessageType, out Type? expectedType))
            {
                throw ReplayFailure($"Message type '{record.MessageType}' is not allowed in a sanitized replay.");
            }

            byte[] body;
            try
            {
                body = Convert.FromBase64String(record.BodyBase64);
            }
            catch (FormatException exception)
            {
                throw ReplayFailure($"Message {record.Sequence} is not valid base64.", exception);
            }

            if (body.Length > MaximumMessageBytes)
            {
                throw ReplayFailure($"Message {record.Sequence} exceeds the replay message size limit.");
            }

            Starlight.Protobuf.Core.IMessage message;
            try
            {
                using var input = new CodedInputStream(body);
                message = Registry.Deserialize(record.CmdId, input);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw ReplayFailure($"Message {record.Sequence} cannot be decoded as V70.", exception);
            }

            if (message.GetType() != expectedType || Registry.GetCmdId(message) != record.CmdId)
            {
                throw ReplayFailure($"Message {record.Sequence} type and CmdId do not agree.");
            }

            messages.Add(new OfficialMessageEnvelope(record.Sequence, message));
        }

        return new SanitizedReplaySource(context, messages);
    }

    private static void ValidateContext(OfficialCaptureContext context)
    {
        if (context.OfficialUid == 0
            || string.IsNullOrWhiteSpace(context.Region)
            || context.CapturedAtUtc == default
            || context.CapturedAtUtc.Offset != TimeSpan.Zero)
        {
            throw ReplayFailure("The replay capture context is incomplete.");
        }
    }

    private static void EnsureStrictSequence(IEnumerable<long> sequences)
    {
        long previous = 0;
        foreach (long sequence in sequences)
        {
            if (sequence <= previous)
            {
                throw ReplayFailure("Replay message sequences must be positive and strictly increasing.");
            }

            previous = sequence;
        }
    }

    private static async Task WriteAtomicallyAsync(
        string path,
        byte[] content,
        CancellationToken cancellationToken)
    {
        string outputPath = Path.GetFullPath(path);
        if (File.Exists(outputPath) || Directory.Exists(outputPath))
        {
            throw new IOException($"The replay output path already exists: '{outputPath}'.");
        }

        string directory = Path.GetDirectoryName(outputPath)
            ?? throw new ArgumentException("The replay output path must have a parent directory.", nameof(path));
        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(
            directory,
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
                await stream.WriteAsync(content, cancellationToken);
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

    private static OfficialConnectivityException ReplayFailure(
        string message,
        Exception? innerException = null) =>
        new(OfficialConnectivityError.ReplayInvalid, message, innerException);

    private sealed record ReplayDocument
    {
        public required int SchemaVersion { get; init; }
        public required string ProtocolVersion { get; init; }
        public required DateTimeOffset CapturedAtUtc { get; init; }
        public required string Region { get; init; }
        public required uint OfficialUid { get; init; }
        public ReplayProfileRecord? Profile { get; init; }
        public required List<ReplayMessageRecord> Messages { get; init; }
    }

    private sealed record ReplayProfileRecord
    {
        public required string Signature { get; init; }
        public required uint PictureId { get; init; }
        public required uint NameCardId { get; init; }
    }

    private sealed record ReplayMessageRecord
    {
        public required long Sequence { get; init; }
        public required int CmdId { get; init; }
        public required string MessageType { get; init; }
        public required string BodyBase64 { get; init; }
    }
}
