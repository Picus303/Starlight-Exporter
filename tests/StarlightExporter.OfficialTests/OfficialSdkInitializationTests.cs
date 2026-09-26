using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StarlightExporter.Official;
using Xunit;

namespace StarlightExporter.OfficialTests;

public sealed class OfficialSdkInitializationTests
{
    [Fact]
    public async Task BootstrapAndFingerprintFollowTheObservedOrderAndRequestedFields()
    {
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/loadConfig" => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            "/comboConfig" => Response("{}"),
            "/getExtList" => Response(
                "{\"retcode\":0,\"data\":{\"ext_list\":[\"osVersion\",\"cpuName\"]}}"),
            "/getFp" => Response("{\"retcode\":0,\"data\":{\"device_fp\":\"12345678901\"}}"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        using var client = new HttpClient(handler);
        var bootstrap = new OfficialSdkBootstrapClient(client, BootstrapOptions());
        var fingerprint = new OfficialDeviceFingerprintExchange(client, FingerprintOptions());
        SdkInstallationState state = new SdkInstallationState
        {
            DeviceId = "synthetic-device-id",
            SeedId = "0123456789abcdef",
            SeedTime = "123",
            DeviceFingerprint = "1234567890",
        };

        OfficialSdkBootstrapResult bootstrapResult = await bootstrap.AttemptAsync();
        OfficialDeviceFingerprintResult refreshed = await fingerprint.RefreshAsync(
            state,
            Runtime(),
            new AttributeCollector(new Dictionary<string, string>
            {
                ["osVersion"] = "synthetic-os",
                ["cpuName"] = "synthetic-cpu",
            }));

        Assert.False(bootstrapResult.ShieldConfigReached);
        Assert.True(bootstrapResult.ComboConfigReached);
        Assert.Equal("12345678901", refreshed.Installation.DeviceFingerprint);
        Assert.Equal(OfficialDeviceFingerprintOrigin.ServerRefreshed, refreshed.Origin);
        Assert.Null(refreshed.RejectionRetcode);
        Assert.Equal("1234567890", state.DeviceFingerprint);
        Assert.Equal(
            ["/loadConfig", "/comboConfig", "/getExtList", "/getFp"],
            handler.Requests.Select(request => request.Path));
        Assert.Equal(["GET", "GET", "GET", "POST"],
            handler.Requests.Select(request => request.Method));
        Assert.Equal("?platform=3", handler.Requests[2].Query);

        RecordedRequest post = handler.Requests[^1];
        using JsonDocument body = JsonDocument.Parse(post.Body!);
        JsonElement root = body.RootElement;
        Assert.Equal(7, root.EnumerateObject().Count());
        Assert.Equal("synthetic-device-id", root.GetProperty("device_id").GetString());
        Assert.Equal("0123456789abcdef", root.GetProperty("seed_id").GetString());
        Assert.Equal("123", root.GetProperty("seed_time").GetString());
        Assert.Equal("3", root.GetProperty("platform").GetString());
        Assert.Equal("1234567890", root.GetProperty("device_fp").GetString());
        Assert.Equal("hk4e_global", root.GetProperty("app_name").GetString());
        Assert.Equal(2, root.GetProperty("ext_fields").EnumerateObject().Count());
        Assert.Equal("application/x-www-form-urlencoded", post.ContentType);
        Assert.Equal("*/*", post.Headers["Accept"]);
        Assert.Equal("1.1", post.Version);
        Assert.Equal("synthetic-os", root.GetProperty("ext_fields").GetProperty("osVersion").GetString());
        Assert.Equal("synthetic-cpu", root.GetProperty("ext_fields").GetProperty("cpuName").GetString());
        Assert.DoesNotContain("synthetic-cpu", refreshed.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingMachineAttributeStopsBeforeFingerprintPost()
    {
        var handler = new RecordingHandler(_ => Response(
            "{\"retcode\":0,\"data\":{\"ext_list\":[\"serialNumber\"]}}"));
        using var client = new HttpClient(handler);
        var subject = new OfficialDeviceFingerprintExchange(client, FingerprintOptions());
        SdkInstallationState state = new()
        {
            DeviceId = "synthetic-device-id",
            SeedId = "0123456789abcdef",
            SeedTime = "123",
        };

        OfficialConnectivityException error = await Assert.ThrowsAsync<OfficialConnectivityException>(
            () => subject.RefreshAsync(state, Runtime(),
                new AttributeCollector(new Dictionary<string, string>())));

        Assert.Equal(OfficialConnectivityError.SdkFingerprintUnavailable, error.Error);
        Assert.Single(handler.Requests);
        Assert.Null(state.DeviceFingerprint);
        Assert.DoesNotContain("serialNumber", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownRequestedAttributeUsesTheDedicatedStopCategory()
    {
        var handler = new RecordingHandler(_ => Response(
            "{\"retcode\":0,\"data\":{\"ext_list\":[\"futureField\"]}}"));
        using var client = new HttpClient(handler);
        var subject = new OfficialDeviceFingerprintExchange(client, FingerprintOptions());
        SdkInstallationState state = new()
        {
            DeviceId = "synthetic-device-id",
            SeedId = "0123456789abcdef",
            SeedTime = "123",
        };

        OfficialConnectivityException error = await Assert.ThrowsAsync<OfficialConnectivityException>(() =>
            subject.RefreshAsync(state, Runtime(),
                new AttributeCollector(new Dictionary<string, string>())));

        Assert.Equal(OfficialConnectivityError.UnsupportedDeviceFingerprintField, error.Error);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task SdkAuthFlowDoesNotReadCredentialsWhenDeviceAttributesAreMissing()
    {
        string path = Path.Combine(Path.GetTempPath(), $"starlight-sdk-auth-{Guid.NewGuid():N}.json");
        try
        {
            var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
            {
                "/getExtList" => Response(
                    "{\"retcode\":0,\"data\":{\"ext_list\":[\"serialNumber\",\"board\"]}}"),
                _ => Response("{}"),
            });
            using var client = new HttpClient(handler);
            using RSA rsa = RSA.Create(1024);
            var initializer = new OfficialSdkInitializer(
                new OfficialSdkBootstrapClient(client, BootstrapOptions()),
                new OfficialDeviceFingerprintExchange(client, FingerprintOptions()));
            var auth = new OfficialSdkAuthFlow(client, initializer, new OfficialPasswordAuthOptions
            {
                Endpoint = new Uri("https://sdk.test/auth"),
                SdkPublicKeyPem = rsa.ExportSubjectPublicKeyInfoPem(),
                Headers = new OfficialSdkRequestHeaders
                {
                    DeviceId = string.Empty,
                    DeviceFingerprint = string.Empty,
                    Language = "en-us",
                    AppId = 4,
                    ClientType = 3,
                    GameBiz = "hk4e_global",
                    ChannelId = 1,
                    SdkVersion = "synthetic",
                    MdkVersion = "synthetic",
                    ChannelVersion = "synthetic",
                    SystemVersion = "synthetic-os",
                    DeviceModel = "synthetic-model",
                    DeviceName = "synthetic-host",
                },
            });
            int credentialReads = 0;

            await Assert.ThrowsAsync<OfficialConnectivityException>(() => auth.RunAsync(
                path, "synthetic-device", Runtime(),
                new AttributeCollector(new Dictionary<string, string>()),
                _ =>
                {
                    credentialReads++;
                    return Task.FromResult(new OfficialPasswordCredentials("unused", "unused"));
                }));

            Assert.Equal(0, credentialReads);
            Assert.Equal(["/loadConfig", "/comboConfig", "/getExtList"],
                handler.Requests.Select(request => request.Path));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task InvalidOnlineFingerprintDoesNotReplacePersistedValue()
    {
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/getExtList" => Response("{\"retcode\":0,\"data\":{\"ext_list\":[]}}"),
            "/getFp" => Response("{\"retcode\":0,\"data\":{\"device_fp\":\"invalid\"}}"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        using var client = new HttpClient(handler);
        var subject = new OfficialDeviceFingerprintExchange(client, FingerprintOptions());
        SdkInstallationState state = new()
        {
            DeviceId = "synthetic-device-id",
            SeedId = "0123456789abcdef",
            SeedTime = "123",
            DeviceFingerprint = "1234567890",
        };

        OfficialDeviceFingerprintResult result = await subject.RefreshAsync(
            state, Runtime(), new AttributeCollector(new Dictionary<string, string>()));

        Assert.Equal("1234567890", result.Installation.DeviceFingerprint);
        Assert.Equal(OfficialDeviceFingerprintOrigin.Persisted, result.Origin);
        Assert.Null(result.RejectionRetcode);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task FingerprintRejectionGeneratesPersistsFallbackAndPreservesOnlyRetcode()
    {
        var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/getExtList" => Response("{\"retcode\":0,\"data\":{\"ext_list\":[]}}"),
            "/getFp" => Response("{\"retcode\":-102,\"message\":\"private response text\"}"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        using var client = new HttpClient(handler);
        var subject = new OfficialDeviceFingerprintExchange(
            client, FingerprintOptions(), _ => 7);
        SdkInstallationState state = new()
        {
            DeviceId = "synthetic-device-id",
            SeedId = "0123456789abcdef",
            SeedTime = "123",
        };

        string path = Path.Combine(Path.GetTempPath(), $"starlight-sdk-fallback-{Guid.NewGuid():N}.json");
        try
        {
            OfficialDeviceFingerprintResult result = await subject.RefreshAndSaveAsync(
                state, path, Runtime(),
                new AttributeCollector(new Dictionary<string, string>()));
            SdkInstallationState persisted = await SdkInstallationState.LoadOrCreateAsync(
                path, "different-device");

            Assert.Equal(OfficialDeviceFingerprintOrigin.GeneratedFallback, result.Origin);
            Assert.Equal(-102, result.RejectionRetcode);
            Assert.Equal("7777777777", result.Installation.DeviceFingerprint);
            Assert.Equal(result.Installation, persisted);
            Assert.DoesNotContain("private response text", result.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task InitializerPersistsFingerprintAndBindsOneIdentityToSdkHeaders()
    {
        string path = Path.Combine(Path.GetTempPath(), $"starlight-sdk-init-{Guid.NewGuid():N}.json");
        try
        {
            var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
            {
                "/loadConfig" or "/comboConfig" => Response("{}"),
                "/getExtList" => Response("{\"retcode\":0,\"data\":{\"ext_list\":[]}}"),
                "/getFp" => Response("{\"retcode\":0,\"data\":{\"device_fp\":\"12345678901\"}}"),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });
            using var client = new HttpClient(handler);
            var initializer = new OfficialSdkInitializer(
                new OfficialSdkBootstrapClient(client, BootstrapOptions()),
                new OfficialDeviceFingerprintExchange(client, FingerprintOptions()));

            OfficialSdkInitializationResult result = await initializer.InitializeAsync(
                path, "synthetic-device", Runtime(),
                new AttributeCollector(new Dictionary<string, string>()));
            SdkInstallationState persisted = await SdkInstallationState.LoadOrCreateAsync(
                path, "different-device");

            Assert.Equal(result.Installation, persisted);
            Assert.Equal("12345678901", persisted.DeviceFingerprint);
            Assert.Equal(OfficialDeviceFingerprintOrigin.ServerRefreshed, result.FingerprintOrigin);
            Assert.Equal(
                ["/loadConfig", "/comboConfig", "/getExtList", "/getFp"],
                handler.Requests.Select(request => request.Path));

            var template = new OfficialSdkRequestHeaders
            {
                DeviceId = "placeholder",
                DeviceFingerprint = string.Empty,
                Language = "en-us",
                AppId = 4,
                ClientType = 3,
                GameBiz = "hk4e_global",
                ChannelId = 1,
                SdkVersion = "synthetic-version",
                MdkVersion = "synthetic-version",
                ChannelVersion = "synthetic-version",
                SystemVersion = "synthetic-os",
                DeviceModel = "synthetic-model",
                DeviceName = "synthetic-host",
            };
            OfficialSdkRequestHeaders bound = template.WithInstallation(result.Installation);
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://sdk.test/auth");
            bound.Apply(request);

            Assert.Equal(persisted.DeviceId, Assert.Single(request.Headers.GetValues("x-rpc-device_id")));
            Assert.Equal(persisted.DeviceFingerprint,
                Assert.Single(request.Headers.GetValues("x-rpc-device_fp")));
            Assert.Throws<OfficialConnectivityException>(() =>
                template.WithInstallation(new SdkInstallationState { DeviceId = "synthetic-device" }));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task SdkToComboFlowUsesOneIdentityAndReadsCredentialsAfterFingerprint()
    {
        string path = Path.Combine(Path.GetTempPath(), $"starlight-sdk-flow-{Guid.NewGuid():N}.json");
        try
        {
            var handler = new RecordingHandler(request => request.RequestUri!.AbsolutePath switch
            {
                "/loadConfig" or "/comboConfig" => Response("{}"),
                "/getExtList" => Response("{\"retcode\":0,\"data\":{\"ext_list\":[]}}"),
                "/getFp" => Response("{\"retcode\":0,\"data\":{\"device_fp\":\"1234567890\"}}"),
                "/auth" => Response("{\"retcode\":0,\"data\":{\"account\":{\"uid\":\"sdk-account\",\"token\":\"sdk-token\",\"is_guest\":false,\"country\":\"FR\"}}}"),
                "/combo" => Response("{\"retcode\":0,\"data\":{\"open_id\":\"combo-account\",\"combo_token\":\"combo-token\",\"account_type\":1,\"guest\":false}}"),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            });
            using var client = new HttpClient(handler);
            using RSA rsa = RSA.Create(1024);
            var initializer = new OfficialSdkInitializer(
                new OfficialSdkBootstrapClient(client, BootstrapOptions()),
                new OfficialDeviceFingerprintExchange(client, FingerprintOptions()));
            var headers = new OfficialSdkRequestHeaders
            {
                DeviceId = "placeholder",
                DeviceFingerprint = string.Empty,
                Language = "en-us",
                AppId = 4,
                ClientType = 3,
                GameBiz = "hk4e_global",
                ChannelId = 1,
                SdkVersion = "synthetic-sdk-version",
                MdkVersion = "synthetic-sdk-version",
                ChannelVersion = "synthetic-sdk-version",
                SystemVersion = "synthetic-os",
                DeviceModel = "synthetic-model",
                DeviceName = "synthetic-host",
            };
            var flow = new OfficialSdkComboFlow(client, initializer,
                new OfficialPasswordAuthOptions
                {
                    Endpoint = new Uri("https://sdk.test/auth"),
                    SdkPublicKeyPem = rsa.ExportSubjectPublicKeyInfoPem(),
                    Headers = headers,
                },
                new OfficialComboOptions
                {
                    Endpoint = new Uri("https://sdk.test/combo"),
                    DeviceId = "placeholder",
                    ApplicationId = 4,
                    ChannelId = 1,
                },
                "synthetic-combo-key");
            bool credentialsReadAfterFingerprint = false;

            OfficialSdkComboResult result = await flow.RunAsync(
                path, "synthetic-device", Runtime(),
                new AttributeCollector(new Dictionary<string, string>()),
                _ =>
                {
                    credentialsReadAfterFingerprint = handler.Requests.Count == 4;
                    return Task.FromResult(new OfficialPasswordCredentials("synthetic-email", "synthetic-password"));
                });

            Assert.True(credentialsReadAfterFingerprint);
            Assert.Equal("combo-account", result.Session.AccountUid);
            Assert.Null(result.Session.ExpectedUid);
            Assert.Equal("1234567890", result.Installation.DeviceFingerprint);
            Assert.Equal(
                ["/loadConfig", "/comboConfig", "/getExtList", "/getFp", "/auth", "/combo"],
                handler.Requests.Select(request => request.Path));
            RecordedRequest auth = handler.Requests[4];
            RecordedRequest combo = handler.Requests[5];
            Assert.Equal(result.Installation.DeviceId, auth.Headers["x-rpc-device_id"]);
            Assert.Equal(result.Installation.DeviceId, combo.Headers["x-rpc-device_id"]);
            Assert.Equal("1234567890", auth.Headers["x-rpc-device_fp"]);
            Assert.Equal("1234567890", combo.Headers["x-rpc-device_fp"]);
            using JsonDocument comboBody = JsonDocument.Parse(combo.Body!);
            Assert.Equal(result.Installation.DeviceId,
                comboBody.RootElement.GetProperty("device").GetString());
            using JsonDocument inner = JsonDocument.Parse(
                comboBody.RootElement.GetProperty("data").GetString()!);
            Assert.Equal("sdk-account", inner.RootElement.GetProperty("open_id").GetString());
            Assert.Equal("sdk-token", inner.RootElement.GetProperty("combo_token").GetString());
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static OfficialSdkBootstrapOptions BootstrapOptions() => new()
    {
        ShieldConfigEndpoint = new Uri("https://sdk.test/loadConfig"),
        ComboConfigEndpoint = new Uri("https://sdk.test/comboConfig"),
    };

    private static OfficialDeviceFingerprintOptions FingerprintOptions() => new()
    {
        ExtListEndpoint = new Uri("https://fp.test/getExtList"),
        FingerprintEndpoint = new Uri("https://fp.test/getFp"),
    };

    private static OfficialDeviceFingerprintRuntime Runtime() => new();

    private static HttpResponseMessage Response(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json"),
    };

    private sealed class AttributeCollector(Dictionary<string, string> attributes)
        : IOfficialDeviceAttributeCollector
    {
        public ValueTask<string?> CollectAsync(
            string name,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(attributes.GetValueOrDefault(name));
        }
    }

    private sealed record RecordedRequest(
        string Method,
        string Path,
        string Query,
        string? Body,
        string? ContentType,
        string Version,
        Dictionary<string, string> Headers);

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(
                request.Method.Method,
                request.RequestUri!.AbsolutePath,
                request.RequestUri.Query,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken),
                request.Content?.Headers.ContentType?.ToString(),
                request.Version.ToString(),
                request.Headers.ToDictionary(
                    header => header.Key,
                    header => Assert.Single(header.Value),
                    StringComparer.OrdinalIgnoreCase)));
            return responder(request);
        }
    }
}
