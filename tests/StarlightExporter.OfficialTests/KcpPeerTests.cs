using StarlightExporter.Official;
using Xunit;

namespace StarlightExporter.OfficialTests;

public sealed class KcpPeerTests
{
    [Fact]
    public void PeersReassembleFragmentedMessagesAndExchangeAcknowledgements()
    {
        var clientOutput = new List<byte[]>();
        var serverOutput = new List<byte[]>();
        var client = new OfficialKcpPeer(123, 456, OfficialKcpWireVersion.HoyoV1, clientOutput.Add);
        var server = new OfficialKcpPeer(123, 456, OfficialKcpWireVersion.HoyoV1, serverOutput.Add);
        byte[] payload = Enumerable.Range(0, 10_000).Select(index => (byte)index).ToArray();

        client.Send(payload);
        client.Update(timestamp: 1_000);

        Assert.True(clientOutput.Count > 1);
        var received = new List<byte[]>();
        foreach (byte[] datagram in clientOutput)
        {
            received.AddRange(server.Input(datagram, timestamp: 1_010));
        }
        foreach (byte[] acknowledgement in serverOutput)
        {
            client.Input(acknowledgement, timestamp: 1_020);
        }

        Assert.Equal(payload, Assert.Single(received));
        Assert.Equal(0, client.PendingSendSegments);
    }

    [Fact]
    public void PeerRestoresApplicationMessageOrderFromReorderedDatagrams()
    {
        var output = new List<byte[]>();
        var sender = new OfficialKcpPeer(123, 456, OfficialKcpWireVersion.HoyoV1, output.Add);
        var receiver = new OfficialKcpPeer(123, 456, OfficialKcpWireVersion.HoyoV1, _ => { });
        byte[] first = Enumerable.Repeat((byte)1, 2_000).ToArray();
        byte[] second = Enumerable.Repeat((byte)2, 2_000).ToArray();
        sender.Send(first);
        sender.Send(second);
        sender.Update(timestamp: 1_000);

        var received = new List<byte[]>();
        foreach (byte[] datagram in output.AsEnumerable().Reverse())
        {
            received.AddRange(receiver.Input(datagram, timestamp: 1_010));
        }

        Assert.Equal(2, received.Count);
        Assert.Equal(first, received[0]);
        Assert.Equal(second, received[1]);
    }

    [Fact]
    public void PeerRetransmitsAfterLoss()
    {
        var output = new List<byte[]>();
        var sender = new OfficialKcpPeer(123, 456, OfficialKcpWireVersion.HoyoV1, output.Add);
        var receiver = new OfficialKcpPeer(123, 456, OfficialKcpWireVersion.HoyoV1, _ => { });
        byte[] payload = "retry-me"u8.ToArray();
        sender.Send(payload);
        sender.Update(timestamp: 1_000);
        int initialDatagrams = output.Count;

        sender.Update(timestamp: 1_500);

        Assert.True(output.Count > initialDatagrams);
        IReadOnlyList<byte[]> received = receiver.Input(output[^1], timestamp: 1_510);
        Assert.Equal(payload, Assert.Single(received));
    }

    [Fact]
    public void PeerReadsMultipleSegmentsFromOneDatagram()
    {
        var output = new List<byte[]>();
        var sender = new OfficialKcpPeer(123, 456, OfficialKcpWireVersion.HoyoV1, output.Add);
        var receiver = new OfficialKcpPeer(123, 456, OfficialKcpWireVersion.HoyoV1, _ => { });
        sender.Send("first"u8);
        sender.Send("second"u8);
        sender.Update(timestamp: 1_000);

        byte[] datagram = Assert.Single(output);
        IReadOnlyList<byte[]> received = receiver.Input(datagram, timestamp: 1_010);

        Assert.Equal(2, received.Count);
        Assert.Equal("first"u8.ToArray(), received[0]);
        Assert.Equal("second"u8.ToArray(), received[1]);
    }
}
