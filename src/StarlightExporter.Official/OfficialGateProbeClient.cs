using System.Diagnostics;
using Starlight.Protocol;

namespace StarlightExporter.Official;

public sealed record OfficialGateProbeOptions
{
    public OfficialKcpTransportOptions Transport { get; init; } = new();
    public TimeSpan PlayerTokenTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan PlayerLoginTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public int MaximumPreLoginMessages { get; init; } = 1024;
}

public sealed record GateTokenProbeResult
{
    public required bool HandshakeSucceeded { get; init; }
    public required bool ConversationAssigned { get; init; }
    public required bool TransportTokenAssigned { get; init; }
    public required bool InitialPadDerived { get; init; }
    public required bool PlayerTokenResponseReceived { get; init; }
    public required bool PlayerUidMatchesExpected { get; init; }
    public required bool KeyIdMatches { get; init; }
    public required bool ServerRandomKeyDecrypted { get; init; }
    public required bool ServerSignatureValid { get; init; }
    public required bool SessionRekeySucceeded { get; init; }
    public required uint PlayerUid { get; init; }
    public required IReadOnlyList<GateMetadataTraceRecord> Trace { get; init; }
}

public sealed record GateLoginProbeResult
{
    public required GateTokenProbeResult Token { get; init; }
    public required bool PlayerLoginResponseReceived { get; init; }
    public required bool PlayerUidMatches { get; init; }
    public required bool ReloginRequired { get; init; }
    public required IReadOnlyList<GateMetadataTraceRecord> Trace { get; init; }
}

public static class OfficialGateProbeClient
{
    public static async Task<GateTokenProbeResult> ProbeTokenAsync(
        ComboSession session,
        OfficialCurrentRegion region,
        OfficialClientProfile clientProfile,
        OfficialGateProbeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new OfficialGateProbeOptions();
        ValidateOptions(options);
        var trace = new GateMetadataTrace();
        long started = Stopwatch.GetTimestamp();

        await using OfficialKcpTransport transport = await OfficialKcpTransport.ConnectAsync(
            region,
            options.Transport,
            cancellationToken: cancellationToken);
        using OfficialGateCipherState cipher = OfficialGateCipherState.FromRegion(region);
        var codec = new OfficialGatePacketCodec();
        OfficialPlayerTokenResult token = await ExchangeTokenAsync(
            session,
            region,
            clientProfile,
            transport,
            cipher,
            codec,
            trace,
            started,
            options.PlayerTokenTimeout,
            cancellationToken);

        return CreateTokenResult(session, clientProfile, transport, token, trace);
    }

    public static async Task<GateLoginProbeResult> ProbeLoginAsync(
        ComboSession session,
        OfficialCurrentRegion region,
        OfficialClientProfile clientProfile,
        OfficialPlayerLoginProfile loginProfile,
        OfficialGateProbeOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new OfficialGateProbeOptions();
        ValidateOptions(options);
        var trace = new GateMetadataTrace();
        long started = Stopwatch.GetTimestamp();

        await using OfficialKcpTransport transport = await OfficialKcpTransport.ConnectAsync(
            region,
            options.Transport,
            cancellationToken: cancellationToken);
        using OfficialGateCipherState cipher = OfficialGateCipherState.FromRegion(region);
        var codec = new OfficialGatePacketCodec();
        OfficialPlayerTokenResult token = await ExchangeTokenAsync(
            session,
            region,
            clientProfile,
            transport,
            cipher,
            codec,
            trace,
            started,
            options.PlayerTokenTimeout,
            cancellationToken);
        GateTokenProbeResult tokenResult = CreateTokenResult(
            session,
            clientProfile,
            transport,
            token,
            trace);

        var login = new OfficialPlayerLoginExchange(
            session,
            region,
            clientProfile,
            loginProfile,
            token);
        byte[] request = login.EncodeRequest(codec, cipher);
        trace.Add(
            Elapsed(started),
            GateTracePhase.PlayerLogin,
            GateTraceDirection.ClientToServer,
            login.RequestMetadata
                ?? throw new InvalidOperationException("PlayerLogin request metadata is unavailable."));
        await transport.SendAsync(request, cancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.PlayerLoginTimeout);
        for (int count = 0; count < options.MaximumPreLoginMessages; count++)
        {
            OfficialGatePacket packet;
            try
            {
                packet = codec.DecodeEncrypted(await transport.ReadAsync(timeout.Token), cipher);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new OfficialConnectivityException(
                    OfficialConnectivityError.PlayerLoginRejected,
                    "The Gate login probe timed out.");
            }

            int? retcode = packet.Message is PlayerLoginRsp response ? response.Retcode : null;
            trace.Add(
                Elapsed(started),
                GateTracePhase.PlayerLogin,
                GateTraceDirection.ServerToClient,
                packet,
                retcode);
            if (packet.Message is not PlayerLoginRsp loginResponse)
            {
                continue;
            }

            login.CompleteResponse(packet);
            return new GateLoginProbeResult
            {
                Token = tokenResult,
                PlayerLoginResponseReceived = true,
                PlayerUidMatches = loginResponse.TargetUid is 0 || loginResponse.TargetUid == token.PlayerUid,
                ReloginRequired = loginResponse.IsDataNeedRelogin,
                Trace = trace.Records.ToArray(),
            };
        }

        throw new OfficialConnectivityException(
            OfficialConnectivityError.PlayerLoginRejected,
            "The Gate login probe exceeded its message limit.");
    }

    private static async Task<OfficialPlayerTokenResult> ExchangeTokenAsync(
        ComboSession session,
        OfficialCurrentRegion region,
        OfficialClientProfile clientProfile,
        OfficialKcpTransport transport,
        OfficialGateCipherState cipher,
        OfficialGatePacketCodec codec,
        GateMetadataTrace trace,
        long started,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using OfficialPlayerTokenExchange exchange = OfficialPlayerTokenExchange.CreatePinned(
            session,
            region,
            clientProfile);
        byte[] request = exchange.EncodeRequest(codec, cipher);
        trace.Add(
            Elapsed(started),
            GateTracePhase.PlayerToken,
            GateTraceDirection.ClientToServer,
            exchange.RequestMetadata
                ?? throw new InvalidOperationException("GetPlayerToken request metadata is unavailable."));
        await transport.SendAsync(request, cancellationToken);

        using var responseTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        responseTimeout.CancelAfter(timeout);
        OfficialGatePacket packet;
        try
        {
            packet = codec.DecodeEncrypted(await transport.ReadAsync(responseTimeout.Token), cipher);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new OfficialConnectivityException(
                OfficialConnectivityError.PlayerTokenRejected,
                "The Gate token probe timed out.");
        }

        int? retcode = packet.Message is GetPlayerTokenRsp response ? response.Retcode : null;
        trace.Add(
            Elapsed(started),
            GateTracePhase.PlayerToken,
            GateTraceDirection.ServerToClient,
            packet,
            retcode);
        return exchange.CompleteResponse(packet, cipher);
    }

    private static GateTokenProbeResult CreateTokenResult(
        ComboSession session,
        OfficialClientProfile clientProfile,
        OfficialKcpTransport transport,
        OfficialPlayerTokenResult token,
        GateMetadataTrace trace) => new()
        {
            HandshakeSucceeded = true,
            ConversationAssigned = transport.Connection.ConversationId != 0,
            TransportTokenAssigned = transport.Connection.Token != 0,
            InitialPadDerived = true,
            PlayerTokenResponseReceived = true,
            PlayerUidMatchesExpected = session.ExpectedUid is null || session.ExpectedUid == token.PlayerUid,
            KeyIdMatches = clientProfile.KeyId != 0,
            ServerRandomKeyDecrypted = true,
            ServerSignatureValid = true,
            SessionRekeySucceeded = true,
            PlayerUid = token.PlayerUid,
            Trace = trace.Records.ToArray(),
        };

    private static long Elapsed(long started) =>
        (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    private static void ValidateOptions(OfficialGateProbeOptions options)
    {
        if (options.PlayerTokenTimeout <= TimeSpan.Zero
            || options.PlayerTokenTimeout > TimeSpan.FromMinutes(1)
            || options.PlayerLoginTimeout <= TimeSpan.Zero
            || options.PlayerLoginTimeout > TimeSpan.FromMinutes(2)
            || options.MaximumPreLoginMessages is < 1 or > GateMetadataTrace.MaximumRecords)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The Gate probe options are invalid.");
        }
    }
}
