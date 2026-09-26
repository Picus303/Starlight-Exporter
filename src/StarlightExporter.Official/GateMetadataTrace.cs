namespace StarlightExporter.Official;

public enum GateTracePhase
{
    Handshake,
    PlayerToken,
    PlayerLogin,
    InitialSync,
    PostSync,
}

public enum GateTraceDirection
{
    ClientToServer,
    ServerToClient,
}

public sealed record GateMetadataTraceRecord
{
    public required long Sequence { get; init; }
    public required long ElapsedMilliseconds { get; init; }
    public required GateTracePhase Phase { get; init; }
    public required GateTraceDirection Direction { get; init; }
    public required ushort CommandId { get; init; }
    public required string MessageType { get; init; }
    public required int SerializedBodyBytes { get; init; }
    public required bool Chunked { get; init; }
    public required bool Duplicate { get; init; }
    public required string FieldPresenceHash { get; init; }
    public int? Retcode { get; init; }
}

public sealed class GateMetadataTrace
{
    public const int MaximumRecords = 4096;

    private readonly List<GateMetadataTraceRecord> _records = [];
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
    private long _sequence;

    public IReadOnlyList<GateMetadataTraceRecord> Records => _records;

    public void Add(
        long elapsedMilliseconds,
        GateTracePhase phase,
        GateTraceDirection direction,
        OfficialGatePacketMetadata metadata,
        int? retcode = null)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentOutOfRangeException.ThrowIfNegative(elapsedMilliseconds);
        if (_records.Count >= MaximumRecords)
        {
            throw new OfficialConnectivityException(
                OfficialConnectivityError.GatePacketInvalid,
                "The Gate metadata trace reached its record limit.");
        }

        string duplicateKey = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{direction}:{metadata.CommandId}:{metadata.SerializedBodyBytes}:{metadata.FieldPresenceHash}");
        _records.Add(new GateMetadataTraceRecord
        {
            Sequence = ++_sequence,
            ElapsedMilliseconds = elapsedMilliseconds,
            Phase = phase,
            Direction = direction,
            CommandId = metadata.CommandId,
            MessageType = metadata.MessageType,
            SerializedBodyBytes = metadata.SerializedBodyBytes,
            Chunked = false,
            Duplicate = !_seen.Add(duplicateKey),
            FieldPresenceHash = metadata.FieldPresenceHash,
            Retcode = retcode,
        });
    }

    public void Add(
        long elapsedMilliseconds,
        GateTracePhase phase,
        GateTraceDirection direction,
        OfficialGatePacket packet,
        int? retcode = null)
    {
        ArgumentNullException.ThrowIfNull(packet);
        Add(
            elapsedMilliseconds,
            phase,
            direction,
            new OfficialGatePacketMetadata(
                packet.CommandId,
                packet.Message.GetType().Name,
                packet.BodyLength,
                packet.FieldPresenceHash),
            retcode);
    }
}
