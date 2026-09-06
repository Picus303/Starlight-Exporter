using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;

namespace StarlightExporter.Official;

public sealed record OfficialKcpTransportOptions
{
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(3);
    public int HandshakeAttempts { get; init; } = 3;
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public int MaximumPendingSendSegments { get; init; } = 32;
    public int MaximumQueuedMessages { get; init; } = 256;
}

public sealed class OfficialKcpTransport : IAsyncDisposable
{
    private const int MaximumDatagramBytes = 64 * 1024;

    private readonly Socket _socket;
    private readonly OfficialGateConnection _connection;
    private readonly OfficialKcpTransportOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly OfficialKcpPeer _peer;
    private readonly Channel<byte[]> _messages;
    private readonly CancellationTokenSource _closing = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly Lock _failureGate = new();
    private Task? _receiveTask;
    private Task? _updateTask;
    private Exception? _failure;
    private long _lastReceiveTimestamp;
    private bool _disposed;

    private OfficialKcpTransport(
        Socket socket,
        OfficialGateConnection connection,
        OfficialKcpTransportOptions options,
        TimeProvider timeProvider)
    {
        _socket = socket;
        _connection = connection;
        _options = options;
        _timeProvider = timeProvider;
        _lastReceiveTimestamp = timeProvider.GetTimestamp();
        _messages = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(options.MaximumQueuedMessages) {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        _peer = new OfficialKcpPeer(
            connection.ConversationId,
            connection.Token,
            OfficialKcpWireVersion.HoyoV1,
            SendDatagram);
    }

    public OfficialGateConnection Connection => _connection;

    public static async Task<OfficialKcpTransport> ConnectAsync(
        OfficialCurrentRegion region,
        OfficialKcpTransportOptions? options = null,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(region);
        options ??= new OfficialKcpTransportOptions();
        ValidateOptions(options);
        timeProvider ??= TimeProvider.System;

        IPAddress address = await ResolveAddressAsync(region.GateHost, cancellationToken);
        var endpoint = new IPEndPoint(address, checked((int)region.GateServerPort));
        var socket = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);

        try
        {
            await socket.ConnectAsync(endpoint, cancellationToken);
            OfficialGateConnection connection = await ExchangeHandshakeAsync(
                socket,
                options,
                cancellationToken);
            var transport = new OfficialKcpTransport(socket, connection, options, timeProvider);
            transport.Start();
            return transport;
        }
        catch (OperationCanceledException)
        {
            socket.Dispose();
            throw;
        }
        catch (OfficialConnectivityException)
        {
            socket.Dispose();
            throw;
        }
        catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
        {
            socket.Dispose();
            throw TransportFailure("The Gate UDP connection could not be established.", exception);
        }
    }

    public async ValueTask SendAsync(
        ReadOnlyMemory<byte> message,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfFailed();
        if (message.IsEmpty || message.Length > _peer.MaximumMessageBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(message), "The outbound KCP message size is invalid.");
        }

        int requiredSegments = checked(
            (message.Length + _peer.MaximumSegmentPayloadBytes - 1)
            / _peer.MaximumSegmentPayloadBytes);
        if (requiredSegments > _options.MaximumPendingSendSegments)
        {
            throw new ArgumentOutOfRangeException(
                nameof(message),
                "The outbound KCP message exceeds the configured send-segment limit.");
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _closing.Token);
        bool sendGateEntered = false;
        try
        {
            await _sendGate.WaitAsync(linked.Token);
            sendGateEntered = true;
            while (_peer.PendingSendSegments + requiredSegments > _options.MaximumPendingSendSegments)
            {
                ThrowIfFailed();
                await Task.Delay(TimeSpan.FromMilliseconds(10), _timeProvider, linked.Token);
            }

            _peer.Send(message.Span);
            _peer.Update(Environment.TickCount64);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            ThrowIfFailed();
            throw;
        }
        catch (OfficialKcpPeerException exception)
        {
            throw TransportFailure("KCP rejected the outbound message.", exception);
        }
        finally
        {
            if (sendGateEntered)
            {
                _sendGate.Release();
            }
        }
    }

    public async IAsyncEnumerable<byte[]> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (byte[] message in _messages.Reader.ReadAllAsync(cancellationToken))
        {
            yield return message;
        }

        ThrowIfFailed();
    }

    public async ValueTask<byte[]> ReadAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ThrowIfFailed();
        try
        {
            return await _messages.Reader.ReadAsync(cancellationToken);
        }
        catch (ChannelClosedException)
        {
            ThrowIfFailed();
            throw TransportFailure("The Gate transport closed before another message was received.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        try
        {
            byte[] disconnect = OfficialGateHandshake.CreateDisconnect(_connection);
            await _socket.SendAsync(disconnect, SocketFlags.None);
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }

        _closing.Cancel();
        _socket.Dispose();
        _messages.Writer.TryComplete();

        Task[] tasks = [_receiveTask ?? Task.CompletedTask, _updateTask ?? Task.CompletedTask];
        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
        }
        catch (OfficialConnectivityException)
        {
        }

        _closing.Dispose();
    }

    private void Start()
    {
        _receiveTask = Task.Run(ReceiveLoopAsync);
        _updateTask = Task.Run(UpdateLoopAsync);
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[MaximumDatagramBytes];
        try
        {
            while (!_closing.IsCancellationRequested)
            {
                int length = await _socket.ReceiveAsync(buffer, SocketFlags.None, _closing.Token);
                if (length == 0)
                {
                    continue;
                }

                _lastReceiveTimestamp = _timeProvider.GetTimestamp();
                IReadOnlyList<byte[]> messages = _peer.Input(
                    buffer.AsSpan(0, length),
                    Environment.TickCount64);
                foreach (byte[] message in messages)
                {
                    await _messages.Writer.WriteAsync(message, _closing.Token);
                }
            }
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (_closing.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Fail(TransportFailure("The Gate receive loop failed.", exception));
        }
    }

    private async Task UpdateLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10), _timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(_closing.Token))
            {
                _peer.Update(Environment.TickCount64);
                if (_peer.IsDead)
                {
                    throw TransportFailure("The KCP peer reached its retransmission limit.");
                }
                if (_timeProvider.GetElapsedTime(_lastReceiveTimestamp) >= _options.IdleTimeout)
                {
                    throw TransportFailure("The Gate connection reached its idle timeout.");
                }
            }
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Fail(exception is OfficialConnectivityException
                ? exception
                : TransportFailure("The Gate update loop failed.", exception));
        }
    }

    private void SendDatagram(byte[] datagram)
    {
        if (datagram.Length > MaximumDatagramBytes)
        {
            Fail(TransportFailure("KCP emitted an oversized datagram."));
            return;
        }

        try
        {
            _socket.Send(datagram, SocketFlags.None);
        }
        catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
        {
            if (!_closing.IsCancellationRequested)
            {
                Fail(TransportFailure("The Gate datagram could not be sent.", exception));
            }
        }
    }

    private void Fail(Exception exception)
    {
        lock (_failureGate)
        {
            _failure ??= exception;
        }
        _messages.Writer.TryComplete(exception);
        _closing.Cancel();
    }

    private void ThrowIfFailed()
    {
        lock (_failureGate)
        {
            if (_failure is { } failure)
            {
                ExceptionDispatchInfo.Capture(failure).Throw();
            }
        }
    }

    private static async Task<IPAddress> ResolveAddressAsync(
        string host,
        CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out IPAddress? literal))
        {
            return literal;
        }

        try
        {
            IPAddress[] addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
            return addresses.FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork)
                ?? addresses.FirstOrDefault()
                ?? throw new SocketException((int)SocketError.HostNotFound);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw TransportFailure("The Gate host could not be resolved.", exception);
        }
    }

    private static async Task<OfficialGateConnection> ExchangeHandshakeAsync(
        Socket socket,
        OfficialKcpTransportOptions options,
        CancellationToken cancellationToken)
    {
        byte[] connect = OfficialGateHandshake.CreateConnect();
        var response = new byte[MaximumDatagramBytes];

        for (int attempt = 1; attempt <= options.HandshakeAttempts; attempt++)
        {
            await socket.SendAsync(connect, SocketFlags.None, cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.HandshakeTimeout);
            try
            {
                int length = await socket.ReceiveAsync(response, SocketFlags.None, timeout.Token);
                return OfficialGateHandshake.ParseExchange(response.AsSpan(0, length));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (attempt == options.HandshakeAttempts)
                {
                    break;
                }
            }
            catch (OfficialConnectivityException) when (attempt < options.HandshakeAttempts)
            {
            }
        }

        throw new OfficialConnectivityException(
            OfficialConnectivityError.GateHandshakeInvalid,
            "The Gate handshake did not complete within the configured attempts.");
    }

    private static void ValidateOptions(OfficialKcpTransportOptions options)
    {
        if (options.HandshakeTimeout <= TimeSpan.Zero
            || options.HandshakeTimeout > TimeSpan.FromMinutes(1)
            || options.HandshakeAttempts is < 1 or > 10
            || options.IdleTimeout <= TimeSpan.Zero
            || options.IdleTimeout > TimeSpan.FromMinutes(10)
            || options.MaximumPendingSendSegments is < 1 or > 1024
            || options.MaximumQueuedMessages is < 1 or > 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The Gate transport options are invalid.");
        }
    }

    private static OfficialConnectivityException TransportFailure(
        string message,
        Exception? innerException = null) =>
        new(OfficialConnectivityError.GateTransportFailed, message, innerException);
}
