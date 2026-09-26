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
                {"retcode":0,"message":"OK","data":{"combo_id":"0","open_id":"account-open-id","combo_token":"combo-secret","guest":false,"country":"FR","heartbeat":false,"account_type":1,"fatigue_remind":null}}
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
        Assert.Equal(JsonValueKind.String, root.GetProperty("app_id").ValueKind);
        Assert.Equal("4", root.GetProperty("app_id").GetString());
        Assert.Equal(JsonValueKind.String, root.GetProperty("channel_id").ValueKind);
        Assert.Equal("1", root.GetProperty("channel_id").GetString());
        string data = root.GetProperty("data").GetString()!;
        Assert.Equal("{\"open_id\":\"sdk-uid\",\"guest\":false,\"combo_token\":\"sdk-secret\"}", data);
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
    public async Task GuestComboRequestOmitsTheSdkToken()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"retcode\":0,\"data\":{\"open_id\":\"guest-id\",\"combo_token\":\"new-combo-token\",\"guest\":true,\"account_type\":0}}",
                Encoding.UTF8,
                "application/json"),
        });
        using var httpClient = new HttpClient(handler);
        using var subject = new OfficialComboSessionExchange(httpClient, Options(), "synthetic-hmac-key");

        await subject.ExchangeAsync(SdkSession.Create("guest-id", string.Empty, isGuest: true));

        using JsonDocument request = JsonDocument.Parse(Assert.Single(handler.Bodies));
        Assert.Equal(
            "{\"open_id\":\"guest-id\",\"guest\":true}",
            request.RootElement.GetProperty("data").GetString());
    }

    [Fact]
    public async Task OfficialSyntheticVectorUsesTheExactSignedInnerData()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"retcode\":0,\"data\":{\"open_id\":\"test\",\"combo_token\":\"new-token\",\"guest\":false,\"account_type\":1}}",
                Encoding.UTF8,
                "application/json"),
        });
        using var client = new HttpClient(handler);
        OfficialClientProfile profile = OfficialClientProfile.OsGlobalV70;
        using var exchange = new OfficialComboSessionExchange(client, new OfficialComboOptions
        {
            Endpoint = profile.ComboEndpoint!,
            DeviceId = "synthetic-device-id",
            ApplicationId = profile.ApplicationId,
            ChannelId = profile.ChannelId,
            Headers = new OfficialSdkRequestHeaders
            {
                DeviceId = "synthetic-device-id",
                Language = "fr",
                AppId = 4,
                ClientType = 3,
                GameBiz = "hk4e_global",
                ChannelId = 1,
                SdkVersion = "2.53.0.196",
                MdkVersion = "2.53.0.196",
                ChannelVersion = "2.53.0.196",
                DeviceFingerprint = "1234567890",
                SystemVersion = "Windows 11",
                DeviceModel = "synthetic-model",
                DeviceName = "synthetic-host",
            },
        }, profile.ComboHmacKey!);

        await exchange.ExchangeAsync(SdkSession.Create("test", "token"));

        using JsonDocument request = JsonDocument.Parse(Assert.Single(handler.Bodies));
        Assert.Equal(
            "{\"open_id\":\"test\",\"guest\":false,\"combo_token\":\"token\"}",
            request.RootElement.GetProperty("data").GetString());
        Assert.Equal(
            "b4daf92182ee2a94a6d888a62cbf52f9b4bbd8e6c1601f8d8983373efb4faba8",
            request.RootElement.GetProperty("sign").GetString());
        Assert.Equal("1234567890", Assert.Single(handler.FingerprintHeaders));
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
        Assert.Equal(-203, exception.Retcode);
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
        public List<string> FingerprintHeaders { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            DeviceHeaders.Add(Assert.Single(request.Headers.GetValues("x-rpc-device_id")));
            if (request.Headers.TryGetValues("x-rpc-device_fp", out IEnumerable<string>? fingerprints))
            {
                FingerprintHeaders.Add(Assert.Single(fingerprints));
            }
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
