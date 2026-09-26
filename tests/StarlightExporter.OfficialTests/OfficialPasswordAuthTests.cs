using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StarlightExporter.Official;
using Xunit;

namespace StarlightExporter.OfficialTests;

public sealed class OfficialPasswordAuthTests
{
    [Fact]
    public void PinnedAuthKeyMatchesTheReportedSelectorZeroFingerprint()
    {
        using RSA key = RSA.Create();
        key.ImportFromPem(OfficialPasswordAuthOptions.WindowsGlobal700PublicKeyPem);

        Assert.Equal(1024, key.KeySize);
        Assert.Equal(
            "23f9c56d7f3a35439866c3ce609dc05be00fa32c441ba5af12eee2bccd38c4e9",
            Convert.ToHexStringLower(SHA256.HashData(key.ExportSubjectPublicKeyInfo())));
        Assert.Equal(
            "/hk4e_global/mdk/shield/api/login",
            OfficialPasswordAuthOptions.WindowsGlobal700Endpoint.AbsolutePath);
    }

    [Fact]
    public async Task ShieldAuthEncryptsTwoIndependentFieldsAndUsesObservedHeaders()
    {
        using RSA key = RSA.Create(1024);
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"retcode\":0,\"data\":{\"account\":{\"uid\":\"sdk-uid\",\"token\":\"sdk-token\",\"is_guest\":false,\"country\":\"FR\"}}}",
                Encoding.UTF8,
                "application/json"),
        });
        using var client = new HttpClient(handler);
        var exchange = new OfficialPasswordAuthExchange(client, Options(key));

        SdkSession session = await exchange.LoginAsync("account@example.test", "synthetic-password");

        Assert.Equal("sdk-uid", session.AccountUid);
        Assert.False(session.IsGuest);
        Assert.Equal("FR", session.Country);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("/hk4e_global/mdk/shield/api/login", handler.Path);
        using JsonDocument body = JsonDocument.Parse(handler.Body!);
        Assert.Equal(2, body.RootElement.EnumerateObject().Count());
        Assert.Equal(
            "account@example.test",
            Encoding.UTF8.GetString(key.Decrypt(
                Convert.FromBase64String(body.RootElement.GetProperty("account").GetString()!),
                RSAEncryptionPadding.Pkcs1)));
        Assert.Equal(
            "synthetic-password",
            Encoding.UTF8.GetString(key.Decrypt(
                Convert.FromBase64String(body.RootElement.GetProperty("password").GetString()!),
                RSAEncryptionPadding.Pkcs1)));
        Assert.DoesNotContain("is_crypto", handler.Body, StringComparison.Ordinal);
        Assert.Equal("synthetic-device-id", handler.Headers["x-rpc-device_id"]);
        Assert.Equal("1234567890", handler.Headers["x-rpc-device_fp"]);
        Assert.Equal("2.53.0.196", handler.Headers["x-rpc-sdk_version"]);
        Assert.Equal("4", handler.Headers["x-rpc-app_id"]);
        Assert.Equal("1", handler.Headers["x-rpc-channel_id"]);
        Assert.Equal("*/*", handler.Headers["Accept"]);
        Assert.Equal("application/json", handler.ContentType);
        Assert.DoesNotContain("1234567890", Options(key).Headers.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShieldAuthStopsOnDeviceGrantWithoutExposingResponseValues()
    {
        using RSA key = RSA.Create(1024);
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"retcode\":0,\"data\":{\"device_grant_required\":true,\"device_grant_ticket\":\"private-ticket\",\"account\":{}}}",
                Encoding.UTF8,
                "application/json"),
        });
        using var client = new HttpClient(handler);
        var exchange = new OfficialPasswordAuthExchange(client, Options(key));

        OfficialConnectivityException error = await Assert.ThrowsAsync<OfficialConnectivityException>(
            () => exchange.LoginAsync("account", "password"));

        Assert.Equal(OfficialConnectivityError.SdkUserActionRequired, error.Error);
        Assert.DoesNotContain("private-ticket", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"retcode\":0,\"content\":{\"mmt_key\":\"private\"},\"data\":{\"account\":{}}}")]
    [InlineData("{\"retcode\":0,\"data\":{\"reactivate_required\":\"true\",\"account\":{\"reactivate_ticket\":\"private\"}}}")]
    [InlineData("{\"retcode\":0,\"data\":{\"safe_moblie_required\":true,\"account\":{}}}")]
    [InlineData("{\"retcode\":0,\"data\":{\"realname_operation\":1,\"account\":{}}}")]
    public async Task ShieldAuthStopsOnEveryDocumentedInteractiveBranch(string response)
    {
        using RSA key = RSA.Create(1024);
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(response, Encoding.UTF8, "application/json"),
        });
        using var client = new HttpClient(handler);
        var exchange = new OfficialPasswordAuthExchange(client, Options(key));

        OfficialConnectivityException error = await Assert.ThrowsAsync<OfficialConnectivityException>(
            () => exchange.LoginAsync("account", "password"));

        Assert.Equal(OfficialConnectivityError.SdkUserActionRequired, error.Error);
        Assert.DoesNotContain("private", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShieldAuthClassifiesLoginNetworkAtRiskAsAnOfficialWorkflowStop()
    {
        using RSA key = RSA.Create(1024);
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"retcode\":-115,\"message\":\"private risk text\"}",
                Encoding.UTF8,
                "application/json"),
        });
        using var client = new HttpClient(handler);
        var exchange = new OfficialPasswordAuthExchange(client, Options(key));

        OfficialConnectivityException error = await Assert.ThrowsAsync<OfficialConnectivityException>(
            () => exchange.LoginAsync("account", "password"));

        Assert.Equal(OfficialConnectivityError.SdkNetworkRisk, error.Error);
        Assert.Equal(-115, error.Retcode);
        Assert.Equal("SDK_NETWORK_RISK", OfficialConnectivityDiagnostic.Code(error.Error));
        Assert.DoesNotContain("private risk text", error.ToString(), StringComparison.Ordinal);
    }

    private static OfficialPasswordAuthOptions Options(RSA key) => new()
    {
        Endpoint = new Uri("https://sdk.test/hk4e_global/mdk/shield/api/login"),
        SdkPublicKeyPem = key.ExportSubjectPublicKeyInfoPem(),
        Headers = new OfficialSdkRequestHeaders
        {
            DeviceId = "synthetic-device-id",
            Language = "en-us",
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
    };

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public string? Path { get; private set; }
        public string? Body { get; private set; }
        public string? ContentType { get; private set; }
        public Dictionary<string, string> Headers { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Method = request.Method;
            Path = request.RequestUri?.AbsolutePath;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            ContentType = request.Content.Headers.ContentType?.ToString();
            foreach ((string name, IEnumerable<string> values) in request.Headers)
            {
                Headers.Add(name, Assert.Single(values));
            }
            return response(request);
        }
    }
}
