using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StarlightExporter.Official;
using Xunit;

namespace StarlightExporter.OfficialTests;

public sealed class OfficialComboTests
{
    [Fact]
    public async Task ComboExchangeBuildsCanonicalSignedRequestAndMapsResponse()
    {
        const string hmacKey = "synthetic-hmac-key";
        var handler = new RecordingHandler(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """
                {"retcode":0,"message":"OK","data":{"combo_id":"0","open_id":"account-open-id","combo_token":"combo-secret","data":"{\"guest\":false,\"country_code\":\"FR\",\"is_new_register\":false}","heartbeat":false,"account_type":1,"fatigue_remind":null}}
                """,
                Encoding.UTF8,
                "application/json"),
        });
        using var httpClient = new HttpClient(handler);
        using var subject = new OfficialComboSessionExchange(httpClient, Options(), hmacKey);

        ComboSession result = await subject.ExchangeAsync(
            SdkSession.Create("sdk-uid", "sdk-secret"));

        using JsonDocument request = JsonDocument.Parse(Assert.Single(handler.Bodies));
        JsonElement root = request.RootElement;
        string data = root.GetProperty("data").GetString()!;
        Assert.Equal("{\"uid\":\"sdk-uid\",\"guest\":false,\"token\":\"sdk-secret\"}", data);
        string canonical = $"app_id=4&channel_id=1&data={data}&device=synthetic-device";
        string expectedSignature = Convert.ToHexStringLower(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(hmacKey),
            Encoding.UTF8.GetBytes(canonical)));
        Assert.Equal(expectedSignature, root.GetProperty("sign").GetString());
        Assert.Equal("synthetic-device", Assert.Single(handler.DeviceHeaders));
        Assert.Equal("account-open-id", result.AccountUid);
        Assert.Equal("FR", result.CountryCode);
        Assert.Equal(123456789u, result.ExpectedUid);
        Assert.DoesNotContain("combo-secret", result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("sdk-secret", subject.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ComboExchangeFailsExplicitlyWhenHmacKeyIsMissing()
    {
        using var httpClient = new HttpClient(new RecordingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)));

        OfficialConnectivityException exception = Assert.Throws<OfficialConnectivityException>(() =>
            new OfficialComboSessionExchange(httpClient, Options(), string.Empty));

        Assert.Equal(OfficialConnectivityError.ComboConfigurationMissing, exception.Error);
    }

    [Fact]
    public async Task ComboRejectionPreservesRetcodeWithoutLeakingSession()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"retcode\":-203,\"message\":\"rejected\",\"data\":null}",
                Encoding.UTF8,
                "application/json"),
        });
        using var httpClient = new HttpClient(handler);
        using var subject = new OfficialComboSessionExchange(
            httpClient,
            Options(),
            "synthetic-hmac-key");

        OfficialConnectivityException exception = await Assert.ThrowsAsync<OfficialConnectivityException>(() =>
            subject.ExchangeAsync(SdkSession.Create("sdk-uid", "private-sdk-token")));

        Assert.Equal(OfficialConnectivityError.ComboExchangeRejected, exception.Error);
        Assert.Contains("retcode -203", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-sdk-token", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionProvidersPreserveTheAuthenticationBoundary()
    {
        ComboSession existing = ComboSession.Create("account", "combo-token");
        var direct = new ExistingComboSessionProvider(existing);
        var exchange = new StubExchange(existing);
        var composed = new OfficialSdkComboSessionProvider(
            new StubSdkProvider(SdkSession.Create("sdk-account", "sdk-token")),
            exchange);

        Assert.Same(existing, await direct.GetSessionAsync());
        Assert.Same(existing, await composed.GetSessionAsync());
        Assert.True(exchange.WasCalled);
        Assert.DoesNotContain("combo-token", direct.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("sdk-token", composed.ToString(), StringComparison.Ordinal);
    }

    private static OfficialComboOptions Options() => new()
    {
        Endpoint = new Uri("https://combo.test/hk4e_global/combo/granter/login/v2/login"),
        DeviceId = "synthetic-device",
        ApplicationId = 4,
        ChannelId = 1,
        ExpectedPlayerUid = 123456789,
    };

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        public List<string> DeviceHeaders { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            DeviceHeaders.Add(Assert.Single(request.Headers.GetValues("x-rpc-device_id")));
            return responder(request);
        }
    }

    private sealed class StubSdkProvider(SdkSession session) : ISdkSessionProvider
    {
        public Task<SdkSession> GetSessionAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(session);
    }

    private sealed class StubExchange(ComboSession result) : IComboSessionExchange
    {
        public bool WasCalled { get; private set; }

        public Task<ComboSession> ExchangeAsync(
            SdkSession sdkSession,
            CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            return Task.FromResult(result);
        }
    }
}
