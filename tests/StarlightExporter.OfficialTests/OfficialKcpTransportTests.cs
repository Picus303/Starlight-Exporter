using Starlight.Kcp;
using StarlightExporter.Official;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace StarlightExporter.OfficialTests;

public sealed class OfficialKcpTransportTests
{
    [Fact]
    public async Task LoopbackTransportCompletesHandshakeAndExchangesApplicationMessages()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        Task serverTask = RunEchoServerAsync(server, timeout.Token);

        await using OfficialKcpTransport transport = await OfficialKcpTransport.ConnectAsync(
            Region(port),
            new OfficialKcpTransportOptions {
                HandshakeTimeout = TimeSpan.FromMilliseconds(250),
                IdleTimeout = TimeSpan.FromSeconds(5),
            },
            cancellationToken: timeout.Token);

        Assert.Equal(123u, transport.Connection.ConversationId);
        Assert.Equal(456u, transport.Connection.Token);
        await transport.SendAsync("client-message"u8.ToArray(), timeout.Token);
        byte[] response = await transport.ReadAsync(timeout.Token);

        Assert.Equal("server-response"u8.ToArray(), response);
        await serverTask;
    }

    [Fact]
    public async Task HandshakeTimeoutHasAStableConnectivityError()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;

        OfficialConnectivityException exception = await Assert.ThrowsAsync<OfficialConnectivityException>(
            async () => await OfficialKcpTransport.ConnectAsync(
                Region(port),
                new OfficialKcpTransportOptions {
                    HandshakeTimeout = TimeSpan.FromMilliseconds(20),
                    HandshakeAttempts = 2,
                }));

        Assert.Equal(OfficialConnectivityError.GateHandshakeInvalid, exception.Error);
    }

    private static async Task RunEchoServerAsync(UdpClient server, CancellationToken cancellationToken)
    {
        UdpReceiveResult connect = await server.ReceiveAsync(cancellationToken);
        Assert.IsType<ConnectHandshake>(Handshake.Parse(connect.Buffer));
        await server.SendAsync(
            new ExchangeHandshake(conv: 123, token: 456).ToByteArray(),
            connect.RemoteEndPoint,
            cancellationToken);

        var outbound = new List<byte[]>();
        var peer = new OfficialKcpPeer(123, 456, OfficialKcpWireVersion.HoyoV1, outbound.Add);
        while (true)
        {
            UdpReceiveResult datagram = await server.ReceiveAsync(cancellationToken);
            IReadOnlyList<byte[]> messages = peer.Input(datagram.Buffer, Environment.TickCount64);
            Flush(server, connect.RemoteEndPoint, outbound);
            if (messages.Count == 0)
            {
                continue;
            }

            Assert.Equal("client-message"u8.ToArray(), Assert.Single(messages));
            peer.Send("server-response"u8);
            peer.Update(Environment.TickCount64 + 10);
            Flush(server, connect.RemoteEndPoint, outbound);
            return;
        }
    }

    private static void Flush(UdpClient server, IPEndPoint endpoint, List<byte[]> outbound)
    {
        foreach (byte[] datagram in outbound)
        {
            server.Client.SendTo(datagram, endpoint);
        }
        outbound.Clear();
    }

    private static OfficialCurrentRegion Region(int port) => new() {
        RegionName = "loopback",
        GateServerIp = IPAddress.Loopback.ToString(),
        GateServerPort = checked((uint)port),
        UseGateServerDomainName = false,
        GateServerDomainName = string.Empty,
        ClientSecretKey = [1, 2, 3],
        SecretKey = [1, 2, 3],
        ConnectGateTicket = OfficialSecret.Create(string.Empty),
        ClientDataVersion = 70,
        ClientSilenceDataVersion = 71,
        ClientDataMd5 = string.Empty,
        ClientSilenceDataMd5 = string.Empty,
        ClientVersionSuffix = string.Empty,
        ClientSilenceVersionSuffix = string.Empty,
        GameBiz = "hk4e_global",
        ResourceUrl = string.Empty,
        DataUrl = string.Empty,
    };
}
