using StarlightExporter.Official;
using Xunit;

namespace StarlightExporter.OfficialTests;

public sealed class OfficialGatePacketSequencerTests
{
    [Fact]
    public void OutgoingHeadersShareASequenceAndUseUnixMilliseconds()
    {
        var clock = new FixedClock();
        var sequencer = new OfficialGatePacketSequencer(clock);

        var first = sequencer.Next();
        clock.Advance(TimeSpan.FromMilliseconds(27));
        var second = sequencer.Next();
        var third = sequencer.Next();

        Assert.Equal(1u, first.ClientSequenceId);
        Assert.Equal(2u, second.ClientSequenceId);
        Assert.Equal(3u, third.ClientSequenceId);
        Assert.Equal(1788566400123ul, first.SentMs);
        Assert.Equal(1788566400150ul, second.SentMs);
        Assert.Equal(0u, first.Flags);
        Assert.Equal(0u, first.ChunkId);
    }

    private sealed class FixedClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 5, 0, 0, 0, 123, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
