using Starlight.Protocol;
using System.Runtime.CompilerServices;

namespace StarlightExporter.Official;

public sealed record OfficialGateSessionOptions
{
    public OfficialKcpTransportOptions Transport { get; init; } = new();
    public TimeSpan PlayerTokenTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan PlayerLoginTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan SynchronizationTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan SynchronizationQuiescence { get; init; } = TimeSpan.FromSeconds(2);
    public int MaximumMessages { get; init; } = 4096;
}

public sealed class LiveGateMessageSource : IOfficialMessageSource, IAsyncDisposable
{
    private readonly OfficialKcpTransport _transport;
    private readonly OfficialGateCipherState _cipher;
    private readonly OfficialGatePacketCodec _codec;
    private readonly OfficialGateSessionOptions _options;
    private readonly Queue<OfficialMessageEnvelope> _pending;
    private long _sequence;
    private int _readStarted;
    private bool _disposed;

    private LiveGateMessageSource(
        OfficialKcpTransport transport,
        OfficialGateCipherState cipher,
        OfficialGatePacketCodec codec,
        OfficialGateSessionOptions options,
        uint playerUid,
        string regionName,
        Queue<OfficialMessageEnvelope> pending,
        long sequence)
    {
        _transport = transport;
        _cipher = cipher;
        _codec = codec;
        _options = options;
        PlayerUid = playerUid;
        RegionName = regionName;
        _pending = pending;
        _sequence = sequence;
    }

    public uint PlayerUid { get; }
    public string RegionName { get; }

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
                await transport.SendAsync(
                    tokenExchange.EncodeRequest(codec, cipher),
                    cancellationToken);
                OfficialGatePacket tokenPacket = await ReadPacketAsync(
                    transport,
                    codec,
                    cipher,
                    options.PlayerTokenTimeout,
                    OfficialConnectivityError.PlayerTokenRejected,
                    "The Gate did not complete GetPlayerToken in time.",
                    cancellationToken);
                token = tokenExchange.CompleteResponse(tokenPacket, cipher);
            }

            var loginExchange = new OfficialPlayerLoginExchange(
                session,
                region,
                clientProfile,
                loginProfile,
                token);
            await transport.SendAsync(loginExchange.EncodeRequest(codec, cipher), cancellationToken);

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

                if (packet.Message is PlayerLoginRsp)
                {
                    loginExchange.CompleteResponse(packet);
                    return new LiveGateMessageSource(
                        transport,
                        cipher,
                        codec,
                        options,
                        token.PlayerUid,
                        region.RegionName,
                        pending,
                        sequence);
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

        bool playerData = false;
        bool playerStore = false;
        bool avatarData = false;
        int count = 0;
        using var synchronizationTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        synchronizationTimeout.CancelAfter(_options.SynchronizationTimeout);

        while (_pending.TryDequeue(out OfficialMessageEnvelope? envelope))
        {
            Observe(envelope.Message, ref playerData, ref playerStore, ref avatarData);
            count++;
            yield return envelope;
        }

        while (count < _options.MaximumMessages)
        {
            bool complete = playerData && playerStore && avatarData;
            using var nextTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                synchronizationTimeout.Token);
            if (complete)
            {
                nextTimeout.CancelAfter(_options.SynchronizationQuiescence);
            }

            byte[] encrypted;
            try
            {
                encrypted = await _transport.ReadAsync(nextTimeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (complete && !synchronizationTimeout.IsCancellationRequested)
                {
                    yield break;
                }

                throw Failure(
                    OfficialConnectivityError.SyncIncomplete,
                    "The Gate synchronization did not provide all required messages in time.");
            }

            OfficialGatePacket packet = _codec.DecodeEncrypted(encrypted, _cipher);
            var next = new OfficialMessageEnvelope(++_sequence, packet.Message);
            Observe(next.Message, ref playerData, ref playerStore, ref avatarData);
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
        $"LiveGateMessageSource {{ PlayerUid = {PlayerUid}, Region = {RegionName}, Secrets = [REDACTED] }}";

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

    private static void Observe(
        Starlight.Protobuf.Core.IMessage message,
        ref bool playerData,
        ref bool playerStore,
        ref bool avatarData)
    {
        playerData |= message is PlayerDataNotify;
        playerStore |= message is PlayerStoreNotify;
        avatarData |= message is AvatarDataNotify;
    }

    private static void ValidateOptions(OfficialGateSessionOptions options)
    {
        if (options.PlayerTokenTimeout <= TimeSpan.Zero
            || options.PlayerTokenTimeout > TimeSpan.FromMinutes(1)
            || options.PlayerLoginTimeout <= TimeSpan.Zero
            || options.PlayerLoginTimeout > TimeSpan.FromMinutes(2)
            || options.SynchronizationTimeout <= TimeSpan.Zero
            || options.SynchronizationTimeout > TimeSpan.FromMinutes(5)
            || options.SynchronizationQuiescence <= TimeSpan.Zero
            || options.SynchronizationQuiescence > options.SynchronizationTimeout
            || options.MaximumMessages is < 3 or > 16384)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The Gate session options are invalid.");
        }
    }

    private static OfficialConnectivityException Failure(
        OfficialConnectivityError error,
        string message) => new(error, message);
}
