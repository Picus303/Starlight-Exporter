using System.Diagnostics;
using System.Runtime.CompilerServices;
using Starlight.Protobuf.Core;
using Starlight.Protocol;

namespace StarlightExporter.Official;

public sealed record OfficialGateSessionOptions
{
    public OfficialKcpTransportOptions Transport { get; init; } = new();
    public TimeSpan PlayerTokenTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan PlayerLoginTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan SynchronizationTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public int MaximumMessages { get; init; } = 4096;
    public GateMetadataTrace? MetadataTrace { get; init; }
}

public sealed class LiveGateMessageSource : IOfficialConnectedMessageSource
{
    private readonly OfficialKcpTransport _transport;
    private readonly OfficialGateCipherState _cipher;
    private readonly OfficialGatePacketCodec _codec;
    private readonly OfficialGateSessionOptions _options;
    private readonly OfficialGatePacketSequencer _outgoingPackets;
    private readonly OfficialPostLoginRequestPlanner _postLoginRequests;
    private readonly Queue<OfficialMessageEnvelope> _pending;
    private long _sequence;
    private readonly long _traceStarted;
    private int _readStarted;
    private bool _disposed;

    private LiveGateMessageSource(
        OfficialKcpTransport transport,
        OfficialGateCipherState cipher,
        OfficialGatePacketCodec codec,
        OfficialGateSessionOptions options,
        OfficialGatePacketSequencer outgoingPackets,
        OfficialPostLoginRequestPlanner postLoginRequests,
        uint playerUid,
        string regionName,
        Queue<OfficialMessageEnvelope> pending,
        long sequence,
        long traceStarted)
    {
        _transport = transport;
        _cipher = cipher;
        _codec = codec;
        _options = options;
        _outgoingPackets = outgoingPackets;
        _postLoginRequests = postLoginRequests;
        PlayerUid = playerUid;
        RegionName = regionName;
        _pending = pending;
        _sequence = sequence;
        _traceStarted = traceStarted;
    }

    public uint PlayerUid { get; }
    public string RegionName { get; }

    internal PacketHead NextOutgoingMetadata() => _outgoingPackets.Next();

    public static async Task<LiveGateMessageSource> ConnectAsync(
        ComboSession session,
        OfficialCurrentRegion region,
        OfficialClientProfile clientProfile,
        OfficialPlayerLoginProfile loginProfile,
        OfficialGateSessionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(region);
        ArgumentNullException.ThrowIfNull(clientProfile);
        ArgumentNullException.ThrowIfNull(loginProfile);
        options ??= new OfficialGateSessionOptions();
        ValidateOptions(options);
        long traceStarted = Stopwatch.GetTimestamp();
        var outgoingPackets = new OfficialGatePacketSequencer();

        OfficialKcpTransport? transport = null;
        OfficialGateCipherState? cipher = null;
        try
        {
            transport = await OfficialKcpTransport.ConnectAsync(
                region,
                options.Transport,
                cancellationToken: cancellationToken);
            cipher = OfficialGateCipherState.FromRegion(region);
            var codec = new OfficialGatePacketCodec();

            OfficialPlayerTokenResult token;
            using (OfficialPlayerTokenExchange tokenExchange =
                OfficialPlayerTokenExchange.CreatePinned(session, region, clientProfile))
            {
                byte[] tokenRequest = tokenExchange.EncodeRequest(codec, cipher, outgoingPackets.Next());
                AddTrace(
                    options.MetadataTrace,
                    traceStarted,
                    GateTracePhase.PlayerToken,
                    GateTraceDirection.ClientToServer,
                    tokenExchange.RequestMetadata);
                await transport.SendAsync(tokenRequest, cancellationToken);
                OfficialGatePacket tokenPacket = await ReadPacketAsync(
                    transport,
                    codec,
                    cipher,
                    options.PlayerTokenTimeout,
                    OfficialConnectivityError.PlayerTokenRejected,
                    "The Gate did not complete GetPlayerToken in time.",
                    cancellationToken);
                AddTrace(
                    options.MetadataTrace,
                    traceStarted,
                    GateTracePhase.PlayerToken,
                    GateTraceDirection.ServerToClient,
                    tokenPacket,
                    tokenPacket.Message is GetPlayerTokenRsp tokenResponse
                        ? tokenResponse.Retcode
                        : null);
                token = tokenExchange.CompleteResponse(tokenPacket, cipher);
            }

            var loginExchange = new OfficialPlayerLoginExchange(
                session,
                region,
                clientProfile,
                loginProfile,
                token);
            var postLoginRequests = new OfficialPostLoginRequestPlanner(token.PlayerUid);
            byte[] loginRequest = loginExchange.EncodeRequest(codec, cipher, outgoingPackets.Next());
            AddTrace(
                options.MetadataTrace,
                traceStarted,
                GateTracePhase.PlayerLogin,
                GateTraceDirection.ClientToServer,
                loginExchange.RequestMetadata);
            await transport.SendAsync(loginRequest, cancellationToken);

            var pending = new Queue<OfficialMessageEnvelope>();
            long sequence = 0;
            using var loginTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            loginTimeout.CancelAfter(options.PlayerLoginTimeout);
            while (pending.Count < options.MaximumMessages)
            {
                OfficialGatePacket packet;
                try
                {
                    byte[] encrypted = await transport.ReadAsync(loginTimeout.Token);
                    packet = codec.DecodeEncrypted(encrypted, cipher);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw Failure(
                        OfficialConnectivityError.PlayerLoginRejected,
                        "The Gate did not complete PlayerLogin in time.");
                }

                AddTrace(
                    options.MetadataTrace,
                    traceStarted,
                    packet.Message is PlayerLoginRsp
                        ? GateTracePhase.PlayerLogin
                        : GateTracePhase.InitialSync,
                    GateTraceDirection.ServerToClient,
                    packet,
                    packet.Message is PlayerLoginRsp loginResponse
                        ? loginResponse.Retcode
                        : null);

                if (packet.Message is PlayerLoginRsp)
                {
                    loginExchange.CompleteResponse(packet);
                    await SendObservedRequestsAsync(
                        postLoginRequests.OnLoginAccepted(packet.PlayerLoginLimitedSocialCache),
                        transport, codec, cipher, outgoingPackets,
                        options.MetadataTrace, traceStarted, cancellationToken);
                    return new LiveGateMessageSource(
                        transport,
                        cipher,
                        codec,
                        options,
                        outgoingPackets,
                        postLoginRequests,
                        token.PlayerUid,
                        region.RegionName,
                        pending,
                        sequence,
                        traceStarted);
                }

                if (packet.Message is PlayerDataNotify
                    && postLoginRequests.OnPlayerData() is { } socialRequest)
                {
                    await SendObservedRequestsAsync(
                        [socialRequest], transport, codec, cipher, outgoingPackets,
                        options.MetadataTrace, traceStarted, cancellationToken);
                }

                pending.Enqueue(new OfficialMessageEnvelope(++sequence, packet.Message));
            }

            throw Failure(
                OfficialConnectivityError.PlayerLoginRejected,
                "The Gate sent too many messages before PlayerLoginRsp.");
        }
        catch
        {
            cipher?.Dispose();
            if (transport is not null)
            {
                await transport.DisposeAsync();
            }
            throw;
        }
    }

    public async IAsyncEnumerable<OfficialMessageEnvelope> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Interlocked.Exchange(ref _readStarted, 1) != 0)
        {
            throw new InvalidOperationException("A live Gate message source can only be consumed once.");
        }

        var readiness = new OfficialSnapshotReadiness(loginAccepted: true);
        int count = 0;
        using var synchronizationTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        synchronizationTimeout.CancelAfter(_options.SynchronizationTimeout);

        while (_pending.TryDequeue(out OfficialMessageEnvelope? envelope))
        {
            readiness.Observe(envelope.Message);
            count++;
            yield return envelope;
        }

        while (count < _options.MaximumMessages)
        {
            byte[] encrypted;
            try
            {
                encrypted = await _transport.ReadAsync(synchronizationTimeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                readiness.MarkCaptureBoundaryValidated();
                if (readiness.IsReady)
                {
                    yield break;
                }

                throw Failure(
                    OfficialConnectivityError.SyncIncomplete,
                    "The Gate synchronization did not provide all required messages in time.");
            }

            OfficialGatePacket packet = _codec.DecodeEncrypted(encrypted, _cipher);
            AddTrace(
                _options.MetadataTrace,
                _traceStarted,
                GateTracePhase.InitialSync,
                GateTraceDirection.ServerToClient,
                packet);
            var next = new OfficialMessageEnvelope(++_sequence, packet.Message);
            if (packet.Message is PlayerDataNotify
                && _postLoginRequests.OnPlayerData() is { } socialRequest)
            {
                await SendObservedRequestsAsync(
                    [socialRequest], _transport, _codec, _cipher, _outgoingPackets,
                    _options.MetadataTrace, _traceStarted, cancellationToken);
            }
            readiness.Observe(next.Message);
            count++;
            yield return next;
        }

        throw Failure(
            OfficialConnectivityError.SyncIncomplete,
            "The Gate synchronization exceeded its message limit.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cipher.Dispose();
        await _transport.DisposeAsync();
    }

    public override string ToString() =>
        "LiveGateMessageSource { PlayerUid = [REDACTED], Region = [REDACTED], Secrets = [REDACTED] }";

    private static async Task<OfficialGatePacket> ReadPacketAsync(
        OfficialKcpTransport transport,
        OfficialGatePacketCodec codec,
        OfficialGateCipherState cipher,
        TimeSpan timeout,
        OfficialConnectivityError timeoutError,
        string timeoutMessage,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            byte[] encrypted = await transport.ReadAsync(timeoutSource.Token);
            return codec.DecodeEncrypted(encrypted, cipher);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw Failure(timeoutError, timeoutMessage);
        }
    }

    private static async Task SendObservedRequestsAsync(
        IReadOnlyList<IMessage> requests,
        OfficialKcpTransport transport,
        OfficialGatePacketCodec codec,
        OfficialGateCipherState cipher,
        OfficialGatePacketSequencer sequencer,
        GateMetadataTrace? trace,
        long started,
        CancellationToken cancellationToken)
    {
        foreach (IMessage request in requests)
        {
            byte[] encrypted = codec.EncodeEncrypted(request, cipher, sequencer.Next());
            AddTrace(trace, started, GateTracePhase.InitialSync,
                GateTraceDirection.ClientToServer, codec.Describe(request));
            await transport.SendAsync(encrypted, cancellationToken);
        }
    }

    private static void ValidateOptions(OfficialGateSessionOptions options)
    {
        if (options.PlayerTokenTimeout <= TimeSpan.Zero
            || options.PlayerTokenTimeout > TimeSpan.FromMinutes(1)
            || options.PlayerLoginTimeout <= TimeSpan.Zero
            || options.PlayerLoginTimeout > TimeSpan.FromMinutes(2)
            || options.SynchronizationTimeout <= TimeSpan.Zero
            || options.SynchronizationTimeout > TimeSpan.FromMinutes(5)
            || options.MaximumMessages is < 3 or > 16384)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The Gate session options are invalid.");
        }
    }

    private static OfficialConnectivityException Failure(
        OfficialConnectivityError error,
        string message) => new(error, message);

    private static void AddTrace(
        GateMetadataTrace? trace,
        long started,
        GateTracePhase phase,
        GateTraceDirection direction,
        OfficialGatePacketMetadata? metadata)
    {
        if (trace is not null && metadata is not null)
        {
            trace.Add(
                (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                phase,
                direction,
                metadata);
        }
    }

    private static void AddTrace(
        GateMetadataTrace? trace,
        long started,
        GateTracePhase phase,
        GateTraceDirection direction,
        OfficialGatePacket packet,
        int? retcode = null)
    {
        trace?.Add(
            (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            phase,
            direction,
            packet,
            retcode);
    }
}
