using Google.Protobuf;
using Starlight.Ec2b;
using Starlight.Protobuf.Core;
using Starlight.Protocol;
using StarlightExporter.Official;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace StarlightExporter.OfficialTests;

public sealed class OfficialDispatchTests
{
    [Fact]
    public async Task ResolveRegionFollowsGlobalDispatchAndDecodesEncryptedEnvelope()
    {
        byte[] regionalPayload = CreateRegionalResponse().ToByteArray();
        var crypto = new PassThroughCrypto(regionalPayload);
        var handler = new StubHttpHandler(request => request.RequestUri!.Host switch {
            "global.test" => TextResponse(CreateGlobalResponse("https://euro.test/query_cur_region")),
            "euro.test" => TextResponse(JsonSerializer.Serialize(new {
                content = Convert.ToBase64String("ciphertext"u8),
                sign = Convert.ToBase64String("signature"u8),
            })),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        using var httpClient = new HttpClient(handler);
        var subject = new OfficialDispatchClient(httpClient, crypto, new FixedTimeProvider());

        OfficialCurrentRegion region = await subject.ResolveRegionAsync(Profile(), "os_euro");

        Assert.Equal("gate.example.test", region.GateHost);
        Assert.Equal(22102u, region.GateServerPort);
        Assert.Equal("hk4e_global", region.GameBiz);
        Assert.True(Ec2bKeyGen.HasValidLayout(region.ClientSecretKey));
        Assert.DoesNotContain("test-ticket", region.ToString(), StringComparison.Ordinal);
        Assert.True(crypto.WasCalled);
        Uri regionalUri = Assert.Single(handler.Requests, uri => uri.Host == "euro.test");
        Assert.Contains("key_id=5", regionalUri.Query, StringComparison.Ordinal);
        Assert.Contains("aid=4", regionalUri.Query, StringComparison.Ordinal);
        Assert.Contains("version=OSRELWin7.0.0", regionalUri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetRegionsDecodesPublicMetadata()
    {
        var handler = new StubHttpHandler(_ => TextResponse(CreateGlobalResponse(
            "https://euro.test/query_cur_region")));
        using var httpClient = new HttpClient(handler);
        var subject = new OfficialDispatchClient(httpClient, new PassThroughCrypto([]), new FixedTimeProvider());

        OfficialRegionList result = await subject.GetRegionsAsync(Profile());

        OfficialRegion region = Assert.Single(result.Regions);
        Assert.Equal("os_euro", region.Name);
        Assert.Equal("Europe", region.Title);
        Assert.Equal(2076, result.ClientSecretKey.Length);
        Assert.Equal(new byte[] { 4, 5 }, result.ClientCustomConfigEncrypted);
        Assert.True(result.EnableLoginPc);
        Assert.Contains("time=1788566400", Assert.Single(handler.Requests).Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveRegionRejectsDirectErrorProtobuf()
    {
        var handler = new StubHttpHandler(request => request.RequestUri!.Host switch {
            "global.test" => TextResponse(CreateGlobalResponse("https://euro.test/query_cur_region")),
            "euro.test" => TextResponse("CAE="),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        using var httpClient = new HttpClient(handler);
        var subject = new OfficialDispatchClient(httpClient, new PassThroughCrypto([]), new FixedTimeProvider());

        OfficialConnectivityException exception = await Assert.ThrowsAsync<OfficialConnectivityException>(
            () => subject.ResolveRegionAsync(Profile(), "os_euro"));

        Assert.Equal(OfficialConnectivityError.ClientVersionRejected, exception.Error);
        Assert.Contains("retcode 1", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveRegionRejectsUnknownRegionBeforeRegionalRequest()
    {
        var handler = new StubHttpHandler(_ => TextResponse(CreateGlobalResponse(
            "https://euro.test/query_cur_region")));
        using var httpClient = new HttpClient(handler);
        var subject = new OfficialDispatchClient(httpClient, new PassThroughCrypto([]), new FixedTimeProvider());

        OfficialConnectivityException exception = await Assert.ThrowsAsync<OfficialConnectivityException>(
            () => subject.ResolveRegionAsync(Profile(), "os_missing"));

        Assert.Equal(OfficialConnectivityError.RegionNotFound, exception.Error);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ResolveRegionAcceptsDirectSuccessfulProtobuf()
    {
        var crypto = new PassThroughCrypto([]);
        var handler = new StubHttpHandler(request => request.RequestUri!.Host switch {
            "global.test" => TextResponse(CreateGlobalResponse("https://euro.test/query_cur_region")),
            "euro.test" => TextResponse(Convert.ToBase64String(CreateRegionalResponse().ToByteArray())),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        using var httpClient = new HttpClient(handler);
        var subject = new OfficialDispatchClient(httpClient, crypto, new FixedTimeProvider());

        OfficialCurrentRegion result = await subject.ResolveRegionAsync(Profile(), "os_euro");

        Assert.Equal("gate.example.test", result.GateHost);
        Assert.False(crypto.WasCalled);
    }

    [Fact]
    public async Task GetRegionsRejectsInvalidEc2bMaterial()
    {
        var response = new QueryRegionListHttpRsp {
            ClientSecretKey = ByteString.CopyFrom(new byte[2076]),
        };
        response.RegionList.Add(new RegionSimpleInfo {
            Name = "os_euro",
            Title = "Europe",
            Type = "DEV_PUBLIC",
            DispatchUrl = "https://euro.test/query_cur_region",
        });
        var handler = new StubHttpHandler(_ => TextResponse(Convert.ToBase64String(response.ToByteArray())));
        using var httpClient = new HttpClient(handler);
        var subject = new OfficialDispatchClient(httpClient, new PassThroughCrypto([]));

        OfficialConnectivityException exception = await Assert.ThrowsAsync<OfficialConnectivityException>(
            () => subject.GetRegionsAsync(Profile()));

        Assert.Equal(OfficialConnectivityError.RegionResponseInvalid, exception.Error);
    }

    [Fact]
    public async Task DispatchTimeoutHasAStableDomainError()
    {
        var handler = new StubHttpHandler(async (_, cancellationToken) => {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var httpClient = new HttpClient(handler);
        var subject = new OfficialDispatchClient(
            httpClient,
            new PassThroughCrypto([]),
            requestTimeout: TimeSpan.FromMilliseconds(20));

        OfficialConnectivityException exception = await Assert.ThrowsAsync<OfficialConnectivityException>(
            () => subject.GetRegionsAsync(Profile()));

        Assert.Equal(OfficialConnectivityError.GlobalDispatchUnavailable, exception.Error);
        Assert.Contains("timed out", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PinnedCryptoDecryptsAndVerifiesChunkedPayload()
    {
        byte[] plaintext = Encoding.UTF8.GetBytes(new string('x', 400));
        using var producer = Starlight.Crypto.Client.ClientCrypto.Create(generateRsaKeys: false);
        RSA contentKey = producer.ContentKeys[5];
        int chunkSize = contentKey.KeySize / 8 - 11;
        using var encrypted = new MemoryStream();
        for (int offset = 0; offset < plaintext.Length; offset += chunkSize)
        {
            byte[] block = plaintext.AsSpan(offset, Math.Min(chunkSize, plaintext.Length - offset)).ToArray();
            encrypted.Write(contentKey.Encrypt(block, RSAEncryptionPadding.Pkcs1));
        }

        string signature = Convert.ToBase64String(producer.SigningKey!.SignData(
            plaintext,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1));
        using StarlightRegionCrypto subject = StarlightRegionCrypto.CreatePinned();

        byte[] result = subject.DecryptAndVerify(encrypted.ToArray(), signature, keyId: 5);

        Assert.Equal(plaintext, result);
    }

    [Fact]
    public void PinnedCryptoRejectsInvalidSignatureWithoutLeakingPayload()
    {
        byte[] plaintext = "regional-data"u8.ToArray();
        using var producer = Starlight.Crypto.Client.ClientCrypto.Create(generateRsaKeys: false);
        byte[] encrypted = producer.ContentKeys[5].Encrypt(plaintext, RSAEncryptionPadding.Pkcs1);
        using StarlightRegionCrypto subject = StarlightRegionCrypto.CreatePinned();

        OfficialConnectivityException exception = Assert.Throws<OfficialConnectivityException>(() =>
            subject.DecryptAndVerify(encrypted, Convert.ToBase64String(new byte[256]), keyId: 5));

        Assert.Equal(OfficialConnectivityError.RegionCryptoUnsupported, exception.Error);
        Assert.DoesNotContain("regional-data", exception.ToString(), StringComparison.Ordinal);
    }

    private static OfficialClientProfile Profile() => OfficialClientProfile.OsGlobalV70 with {
        GlobalDispatchUri = new Uri("https://global.test/query_region_list"),
    };

    private static string CreateGlobalResponse(string dispatchUrl)
    {
        var response = new QueryRegionListHttpRsp {
            ClientSecretKey = ByteString.CopyFrom(Ec2bKeyGen.Create("global-test")),
            ClientCustomConfigEncrypted = ByteString.CopyFrom(new byte[] { 4, 5 }),
            EnableLoginPc = true,
        };
        response.RegionList.Add(new RegionSimpleInfo {
            Name = "os_euro",
            Title = "Europe",
            Type = "DEV_PUBLIC",
            DispatchUrl = dispatchUrl,
        });
        return Convert.ToBase64String(response.ToByteArray());
    }

    private static QueryCurrRegionHttpRsp CreateRegionalResponse() => new() {
        ClientSecretKey = ByteString.CopyFrom(Ec2bKeyGen.Create("region-test")),
        ConnectGateTicket = "test-ticket",
        RegionInfo = new RegionInfo {
            GateserverIp = "192.0.2.1",
            GateserverPort = 22102,
            UseGateserverDomainName = true,
            GateserverDomainName = "gate.example.test",
            SecretKey = ByteString.CopyFrom(Ec2bKeyGen.Create("region-test")),
            ClientDataVersion = 70,
            ClientSilenceDataVersion = 71,
            ClientDataMd5 = "data-md5",
            ClientSilenceDataMd5 = "silence-md5",
            ClientVersionSuffix = "suffix",
            ClientSilenceVersionSuffix = "silence-suffix",
            GameBiz = "hk4e_global",
            ResourceUrl = "https://resources.example.test/",
            DataUrl = "https://data.example.test/",
        },
    };

    private static HttpResponseMessage TextResponse(string content) => new(HttpStatusCode.OK) {
        Content = new StringContent(content, Encoding.UTF8, "text/plain"),
    };

    private sealed class PassThroughCrypto(byte[] plaintext) : IOfficialRegionCrypto
    {
        public bool WasCalled { get; private set; }

        public byte[] DecryptAndVerify(byte[] ciphertext, string signatureBase64, uint keyId)
        {
            WasCalled = true;
            Assert.NotEmpty(ciphertext);
            Assert.NotEmpty(signatureBase64);
            Assert.Equal(5u, keyId);
            return plaintext;
        }
    }

    private sealed class StubHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;

        public StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
            : this((request, _) => Task.FromResult(responder(request)))
        {
        }

        public StubHttpHandler(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        {
            _responder = responder;
        }

        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return _responder(request, cancellationToken);
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            new(2026, 9, 5, 0, 0, 0, TimeSpan.Zero);
    }
}
