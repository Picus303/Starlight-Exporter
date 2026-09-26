using Starlight.Protocol;

namespace StarlightExporter.Official;

public sealed class OfficialGatePacketSequencer(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private uint _sequence;

    public PacketHead Next()
    {
        if (_sequence == uint.MaxValue)
        {
            throw new InvalidOperationException("The Gate packet sequence is exhausted.");
        }
        return new PacketHead
        {
            ClientSequenceId = ++_sequence,
            SentMs = checked((ulong)_timeProvider.GetUtcNow().ToUnixTimeMilliseconds()),
        };
    }
}
