using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Starlight.Ec2b;
using Starlight.Protobuf.Core;
using Starlight.Protocol;
using StarlightExporter.Official;
using Xunit;

namespace StarlightExporter.OfficialTests;

public sealed class Delivery4FixtureTests
{
    [Fact]
    public void FreshIdentityFixtureIsProducedByTheApplicationBuilder()
    {
        using JsonDocument fixture = ReadJson("identity", "fresh_identity.synthetic.json");
        JsonElement root = fixture.RootElement;
        string unity = UnityDeviceIdentifier.FromWindowsSerials(
            ["BOARD-001"], ["BIOS-002"], ["OS-003"]);
        var clock = new FixedClock(long.Parse(
            root.GetProperty("MIHOYOSDK_SEED_TIME").GetString()!,
            System.Globalization.CultureInfo.InvariantCulture));

        SdkInstallationState state = SdkInstallationState.Create(
            unity, clock, _ => Convert.FromHexString(
                root.GetProperty("MIHOYOSDK_SEED_ID").GetString()!));

        Assert.Equal(root.GetProperty("unity_device_unique_identifier").GetString(), unity);
        Assert.Equal(root.GetProperty("MIHOYOSDK_DEVICE_ID").GetString(), state.DeviceId);
        Assert.Equal(root.GetProperty("MIHOYOSDK_SEED_ID").GetString(), state.SeedId);
        Assert.Equal(root.GetProperty("MIHOYOSDK_SEED_TIME").GetString(), state.SeedTime);
    }

    [Fact]
    public async Task ComboBuilderMatchesBothDelivery4HmacVectors()
    {
        using JsonDocument fixture = ReadJson("combo", "hmac_vectors.json");
        foreach (JsonElement vector in fixture.RootElement.GetProperty("vectors").EnumerateArray())
        {
            string inner = vector.GetProperty("inner_data").GetString()!;
            using JsonDocument innerJson = JsonDocument.Parse(inner);
            bool guest = innerJson.RootElement.GetProperty("guest").GetBoolean();
            string openId = innerJson.RootElement.GetProperty("open_id").GetString()!;
            string token = guest ? string.Empty
                : innerJson.RootElement.GetProperty("combo_token").GetString()!;
            var handler = new CaptureHandler();
            using var client = new HttpClient(handler);
            using var exchange = new OfficialComboSessionExchange(client, new OfficialComboOptions
            {
                Endpoint = new Uri("https://combo.test/login"),
                DeviceId = "synthetic-device-id",
                ApplicationId = 4,
                ChannelId = 1,
            }, OfficialClientProfile.OsGlobalV70.ComboHmacKey!);

            await exchange.ExchangeAsync(SdkSession.Create(openId, token, guest));

            using JsonDocument body = JsonDocument.Parse(handler.Body!);
            Assert.Equal(JsonValueKind.String, body.RootElement.GetProperty("app_id").ValueKind);
            Assert.Equal(JsonValueKind.String, body.RootElement.GetProperty("channel_id").ValueKind);
            Assert.Equal(inner, body.RootElement.GetProperty("data").GetString());
            Assert.Equal(vector.GetProperty("sign").GetString(),
                body.RootElement.GetProperty("sign").GetString());
        }
    }

    [Fact]
    public async Task ShieldBuilderAndParserConsumeTheDelivery4FixtureContract()
    {
        using JsonDocument clear = ReadJson("q12", "shield.clear.synthetic.json");
        using JsonDocument expectedHeaders = ReadJson(
            "q12", "headers.sdk_explicit.synthetic.json");
        string response = Encoding.UTF8.GetString(ReadBytes(
            "q12", "shield.success.synthetic.json"));
        using RSA key = RSA.Create(1024);
        var handler = new CaptureHandler(response);
        using var client = new HttpClient(handler);
        var exchange = new OfficialPasswordAuthExchange(client, new OfficialPasswordAuthOptions
        {
            Endpoint = new Uri("https://sdk.test/hk4e_global/mdk/shield/api/login"),
            SdkPublicKeyPem = key.ExportSubjectPublicKeyInfoPem(),
            Headers = new OfficialSdkRequestHeaders
            {
                DeviceId = "0123456789abcdef0123456789abcdef012345671700000000000",
                Language = "fr",
                AppId = 4,
                ClientType = 3,
                GameBiz = "hk4e_global",
                ChannelId = 1,
                SdkVersion = "2.53.0.196",
                DeviceFingerprint = "1234567890",
                SystemVersion = "Windows 10 64-bit (synthetic)",
                DeviceModel = "Synthetic BaseBoard",
                DeviceName = "SYNTHETIC-PC",
                MdkVersion = "2.53.0.196",
                ChannelVersion = "2.53.0.196",
            },
        });

        SdkSession session = await exchange.LoginAsync(
            clear.RootElement.GetProperty("account").GetString()!,
            clear.RootElement.GetProperty("password").GetString()!);

        Assert.Equal("100000001", session.AccountUid);
        Assert.False(session.IsGuest);
        Assert.Equal("FR", session.Country);
        using JsonDocument request = JsonDocument.Parse(handler.Body!);
        JsonProperty[] properties = request.RootElement.EnumerateObject().ToArray();
        Assert.Equal(["account", "password"], properties.Select(item => item.Name));
        Assert.Equal(
            clear.RootElement.GetProperty("account").GetString(),
            Encoding.UTF8.GetString(key.Decrypt(
                Convert.FromBase64String(properties[0].Value.GetString()!),
                RSAEncryptionPadding.Pkcs1)));
        Assert.Equal(
            clear.RootElement.GetProperty("password").GetString(),
            Encoding.UTF8.GetString(key.Decrypt(
                Convert.FromBase64String(properties[1].Value.GetString()!),
                RSAEncryptionPadding.Pkcs1)));
        foreach (JsonProperty header in expectedHeaders.RootElement.EnumerateObject())
        {
            if (header.NameEquals("Content-Type"))
            {
                Assert.Equal(header.Value.GetString(), handler.ContentType);
            }
            else
            {
                Assert.Equal(header.Value.GetString(), handler.Headers[header.Name]);
            }
        }
        Assert.DoesNotContain("x-rpc-lrsag", handler.Headers.Keys);
        Assert.DoesNotContain("x-rpc-auto_test", handler.Headers.Keys);
    }

    [Fact]
    public void GateKeyScheduleUsesOnlyDelivery4TopLevelTag11()
    {
        byte[] protobuf = ReadBytes("q16", "query_curr_region_both_distinct.pb.bin");
        var response = new QueryCurrRegionHttpRsp();
        response.MergeFrom(protobuf);
        byte[] topLevel = response.ClientSecretKey.ToByteArray();
        RegionInfo regionInfo = Assert.IsType<RegionInfo>(response.RegionInfo);
        byte[] nested = regionInfo.SecretKey.ToByteArray();
        var region = new OfficialCurrentRegion
        {
            RegionName = "synthetic",
            GateServerIp = "192.0.2.1",
            GateServerPort = 22102,
            UseGateServerDomainName = false,
            GateServerDomainName = string.Empty,
            ClientSecretKey = topLevel,
            SecretKey = nested,
            ConnectGateTicket = OfficialSecret.Create(string.Empty),
            ClientDataVersion = 70,
            ClientSilenceDataVersion = 0,
            ClientDataMd5 = string.Empty,
            ClientSilenceDataMd5 = string.Empty,
            ClientVersionSuffix = string.Empty,
            ClientSilenceVersionSuffix = string.Empty,
            GameBiz = "hk4e_global",
            ResourceUrl = string.Empty,
            DataUrl = string.Empty,
        };

        Assert.Equal("201959acad51675841039edf35692f206e43f73fbd710f43966bdfe1590716f0",
            Convert.ToHexStringLower(SHA256.HashData(topLevel)));
        Assert.Equal("574c8fe9b6aef7ca7655d3ac274ee0c7a5a81201ad022847072e6984f9725a6f",
            Convert.ToHexStringLower(SHA256.HashData(nested)));
        Assert.Equal(Ec2bHelper.Derive(topLevel), OfficialGateKeySchedule.DeriveInitialPad(region));
        Assert.NotEqual(Ec2bHelper.Derive(nested), OfficialGateKeySchedule.DeriveInitialPad(region));
    }

    [Fact]
    public async Task DispatchBuilderMatchesTheDelivery4OrderedQueryFixture()
    {
        string expectedUri = Encoding.UTF8.GetString(ReadBytes(
            "q15", "query_cur_region.synthetic.txt")).Trim();
        byte[] ec2b = ReadBytes("q16", "top_level_tag11.ec2b.bin");
        var list = new QueryRegionListHttpRsp
        {
            ClientSecretKey = ByteString.CopyFrom(ec2b),
        };
        list.RegionList.Add(new RegionSimpleInfo
        {
            Name = "os_euro",
            Title = "Europe",
            Type = "DEV_PUBLIC",
            DispatchUrl = "https://dispatch.example.invalid/query_cur_region/os_euro",
        });
        byte[] regional = new QueryCurrRegionHttpRsp
        {
            ClientSecretKey = ByteString.CopyFrom(ec2b),
            RegionInfo = new RegionInfo
            {
                GateserverIp = "192.0.2.1",
                GateserverPort = 22102,
                GameBiz = "hk4e_global",
            },
        }.ToByteArray();
        var handler = new DispatchFixtureHandler(
            Convert.ToBase64String(list.ToByteArray()),
            "{\"content\":\"Y2lwaGVydGV4dA==\",\"sign\":\"c2lnbmF0dXJl\"}");
        using var client = new HttpClient(handler);
        var dispatch = new OfficialDispatchClient(
            client, new FixtureRegionCrypto(regional), new FixedClock(123));
        OfficialClientProfile profile = OfficialClientProfile.OsGlobalV70 with
        {
            GlobalDispatchUri = new Uri("https://global.example.invalid/query_region_list"),
        };

        OfficialCurrentRegion result = await dispatch.ResolveRegionAsync(
            profile,
            "os_euro",
            ComboSession.Create("100000000", "synthetic-token", accountType: 1));

        Assert.Equal(expectedUri, handler.Requests[1].AbsoluteUri);
        Assert.Equal(ec2b, result.ClientSecretKey);
        Assert.Equal(OfficialRegionalPayloadFormat.EncryptedJsonEnvelope, result.PayloadFormat);
    }

    [Fact]
    public void RegionalEnvelopeFixtureHasTheDocumentedCryptographicShape()
    {
        using JsonDocument expected = ReadJson("q17", "expected.json");
        using JsonDocument envelope = ReadJson(
            "q17", "query_curr_region.response.synthetic.json");
        byte[] plaintext = ReadBytes("q17", "query_curr_region.plaintext.pb.bin");
        byte[] ciphertext = Convert.FromBase64String(
            envelope.RootElement.GetProperty("content").GetString()!);
        byte[] signature = Convert.FromBase64String(
            envelope.RootElement.GetProperty("sign").GetString()!);

        Assert.Equal(expected.RootElement.GetProperty("plaintext_bytes").GetInt32(),
            plaintext.Length);
        Assert.Equal(expected.RootElement.GetProperty("plaintext_sha256").GetString(),
            Convert.ToHexStringLower(SHA256.HashData(plaintext)));
        Assert.Equal(expected.RootElement.GetProperty("ciphertext_bytes").GetInt32(),
            ciphertext.Length);
        Assert.Equal(expected.RootElement.GetProperty("ciphertext_sha256").GetString(),
            Convert.ToHexStringLower(SHA256.HashData(ciphertext)));
        Assert.Equal(expected.RootElement.GetProperty("signature_bytes").GetInt32(),
            signature.Length);
        Assert.Equal(expected.RootElement.GetProperty("signature_sha256").GetString(),
            Convert.ToHexStringLower(SHA256.HashData(signature)));
    }

    [Fact]
    public void GateFixtureManifestMatchesEveryBinary()
    {
        using JsonDocument manifest = ReadJson("gate", "manifest.json");
        foreach (JsonProperty entry in manifest.RootElement.GetProperty("files").EnumerateObject())
        {
            byte[] content = ReadBytes("gate", entry.Name);
            Assert.Equal(entry.Value.GetProperty("length").GetInt32(), content.Length);
            Assert.Equal(entry.Value.GetProperty("sha256").GetString(),
                Convert.ToHexStringLower(SHA256.HashData(content)));
        }
    }

    private static JsonDocument ReadJson(params string[] parts) =>
        JsonDocument.Parse(ReadBytes(parts));

    private static byte[] ReadBytes(params string[] parts) =>
        File.ReadAllBytes(Path.Combine([AppContext.BaseDirectory, "D4Fixtures", .. parts]));

    private sealed class FixedClock(long unixMilliseconds) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds);
    }

    private sealed class FixtureRegionCrypto(byte[] plaintext) : IOfficialRegionCrypto
    {
        public byte[] DecryptAndVerify(byte[] ciphertext, string signatureBase64, uint keyId)
        {
            Assert.Equal("ciphertext"u8.ToArray(), ciphertext);
            Assert.Equal("signature", Encoding.UTF8.GetString(
                Convert.FromBase64String(signatureBase64)));
            Assert.Equal(5u, keyId);
            return plaintext;
        }
    }

    private sealed class DispatchFixtureHandler(
        string listResponse,
        string regionResponse) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request.RequestUri!);
            string response = request.RequestUri!.Host == "global.example.invalid"
                ? listResponse : regionResponse;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class CaptureHandler(string? response = null) : HttpMessageHandler
    {
        public string? Body { get; private set; }
        public string? ContentType { get; private set; }
        public Dictionary<string, string> Headers { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            ContentType = request.Content.Headers.ContentType?.ToString();
            foreach ((string name, IEnumerable<string> values) in request.Headers)
            {
                Headers[name] = Assert.Single(values);
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    response ?? "{\"retcode\":0,\"data\":{\"open_id\":\"synthetic-open-id\",\"combo_token\":\"synthetic-combo-token\",\"account_type\":1,\"guest\":false}}",
                    Encoding.UTF8,
                    "application/json"),
            };
        }
    }
}
