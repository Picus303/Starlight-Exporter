using Starlight.Kcp.Internals;

namespace StarlightExporter.Official;

public enum OfficialKcpWireVersion
{
    Base,
    HoyoV1
}

public sealed class OfficialKcpPeerException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>
/// Direction-neutral adapter over the public KCP engine in the pinned Starlight assembly.
/// This file is the only exporter source that references its Internals namespace; socket
/// ownership, handshake and the update clock remain with the caller.
/// </summary>
public sealed class OfficialKcpPeer
{
    private const int MaximumDatagramBytes = 64 * 1024;

    private readonly Lock _gate = new();
    private readonly Kcp _kcp;

    public OfficialKcpPeer(
        uint conversationId,
        uint token,
        OfficialKcpWireVersion wireVersion,
        Action<byte[]> output)
    {
        ArgumentOutOfRangeException.ThrowIfZero(conversationId);
        ArgumentOutOfRangeException.ThrowIfZero(token);
        ArgumentNullException.ThrowIfNull(output);

        _kcp = new Kcp(
            conversationId,
            token,
            stream: false,
            new DelegateWriter(output)) {
            KcpVersion = wireVersion switch {
                OfficialKcpWireVersion.Base => KcpVersion.KCP_BASE,
                OfficialKcpWireVersion.HoyoV1 => KcpVersion.KCP_HYV_V1,
                _ => throw new ArgumentOutOfRangeException(nameof(wireVersion))
            }
        };
        _kcp.Mss = _kcp.Mtu - _kcp.KcpVersion.Overhead();
        _kcp.SetNodelay(nodelay: true, interval: 10, resend: 2, nc: true);
    }

    public uint ConversationId => _kcp.Conv;
    public uint Token => _kcp.Token;
    public int MaximumSegmentPayloadBytes => _kcp.Mss;
    public int MaximumMessageBytes => _kcp.Mss * (KcpConstants.KCP_WND_RCV - 1);

    public int PendingSendSegments
    {
        get
        {
            lock (_gate)
            {
                return _kcp.SndQueue.Count + _kcp.SndBuf.Count;
            }
        }
    }

    public bool IsDead
    {
        get
        {
            lock (_gate)
            {
                return _kcp.State == -1;
            }
        }
    }

    public void Send(ReadOnlySpan<byte> message)
    {
        if (message.IsEmpty || message.Length > MaximumMessageBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(message),
                $"A KCP message must contain between 1 and {MaximumMessageBytes} bytes.");
        }

        lock (_gate)
        {
            ThrowIfFailure(_kcp.Send(message.ToArray()), "KCP rejected the outbound message.");
        }
    }

    public IReadOnlyList<byte[]> Input(ReadOnlySpan<byte> datagram, long timestamp)
    {
        if (datagram.IsEmpty || datagram.Length > MaximumDatagramBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(datagram), "The KCP datagram size is invalid.");
        }

        lock (_gate)
        {
            ThrowIfFailure(_kcp.Update(timestamp), "KCP update failed before input.");
            ThrowIfFailure(
                _kcp.Input(new ByteCursor(datagram.ToArray())),
                "KCP rejected the inbound datagram.");
            ThrowIfFailure(_kcp.Flush(), "KCP could not flush acknowledgements.");

            var messages = new List<byte[]>();
            while (true)
            {
                KcpResult<int> size = _kcp.PeekSize();
                if (size.IsFailure)
                {
                    break;
                }
                if (size.Value is <= 0 || size.Value > MaximumMessageBytes)
                {
                    throw new OfficialKcpPeerException(
                        "KCP produced an invalid reassembled message size.");
                }

                var message = new byte[size.Value];
                KcpResult<int> received = _kcp.Recv(message);
                ThrowIfFailure(received, "KCP could not read a reassembled message.");
                messages.Add(message);
            }

            return messages;
        }
    }

    public void Update(long timestamp)
    {
        lock (_gate)
        {
            ThrowIfFailure(_kcp.Update(timestamp), "KCP update failed.");
        }
    }

    private static void ThrowIfFailure<T>(KcpResult<T> result, string message)
    {
        if (result.IsFailure)
        {
            throw new OfficialKcpPeerException(message, result.Exception);
        }
    }

    private sealed class DelegateWriter(Action<byte[]> output) : IWriter
    {
        public void Write(byte[] data) => output(data);
    }
}
