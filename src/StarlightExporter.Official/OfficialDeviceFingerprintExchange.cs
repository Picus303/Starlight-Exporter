using System.Buffers;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StarlightExporter.Official;

public sealed record OfficialDeviceFingerprintOptions
{
    public required Uri ExtListEndpoint { get; init; }
    public required Uri FingerprintEndpoint { get; init; }
    public int EnvironmentSelector { get; init; } = 4;
    public string Platform { get; init; } = "3";
    public string AppName { get; init; } = "hk4e_global";
    public string SdkVersion { get; init; } = "2.53.0.196";
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public static OfficialDeviceFingerprintOptions WindowsGlobal700 { get; } = new()
    {
        ExtListEndpoint = new Uri("https://sg-public-data-api.hoyoverse.com/device-fp/api/getExtList"),
        FingerprintEndpoint = new Uri("https://sg-public-data-api.hoyoverse.com/device-fp/api/getFp"),
    };
}

public sealed record OfficialDeviceFingerprintRuntime;

public enum OfficialDeviceFingerprintOrigin
{
    ServerRefreshed,
    Persisted,
    GeneratedFallback,
}

public sealed record OfficialDeviceFingerprintResult(
    SdkInstallationState Installation,
    OfficialDeviceFingerprintOrigin Origin,
    int? RejectionRetcode);

public static class OfficialDeviceFingerprintFields
{
    public static IReadOnlySet<string> KnownNames { get; } = new HashSet<string>(
    [
        "osVersion", "cpuName", "cpuCores", "cpuFrequency", "gpuID", "systemName",
        "deviceUID", "gpuName", "gpuMemory", "gpuVendorID", "memorySize", "screenSize",
        "addressMAC", "deviceName", "deviceModel", "deviceType", "gpuAPI", "gpuVersion",
        "gpuVendor", "isGpuMultiTread", "bootRomVersion", "smcVersion", "board",
        "networkType", "proxyStatus", "batteryStatus", "chargeStatus", "appMemory",
        "hostname", "serialNumber", "IDFV", "screenBrightness", "buildTime",
        "appInstallTimeDiff", "appUpdateTimeDiff", "hasVpn", "packageName", "macosUUID",
        "packageVersion", "cpuType", "ramRemain", "ramCapacity", "romRemain",
        "romCapacity", "engineName",
    ], StringComparer.Ordinal);
}

public interface IOfficialDeviceAttributeCollector
{
    // Null means the requested attribute cannot be measured. An intentional empty
    // value must be returned as the empty string, not synthesized by this exchange.
    ValueTask<string?> CollectAsync(string name, CancellationToken cancellationToken = default);
}

// Implements the observed getExtList -> getFp request order for the 7.0.0 profile.
// Requested attributes and seed values must come from runtime providers. The
// response schema is checked strictly, so a changed 7.1 contract stops here.
public sealed class OfficialDeviceFingerprintExchange
{
    private const int MaximumResponseBytes = 128 * 1024;
    private readonly HttpClient _httpClient;
    private readonly OfficialDeviceFingerprintOptions _options;
    private readonly Func<int, int> _randomDigit;

    public OfficialDeviceFingerprintExchange(
        HttpClient httpClient,
        OfficialDeviceFingerprintOptions options,
        Func<int, int>? randomDigit = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _randomDigit = randomDigit ?? RandomNumberGenerator.GetInt32;
        ValidateEndpoint(options.ExtListEndpoint);
        ValidateEndpoint(options.FingerprintEndpoint);
        if (options.EnvironmentSelector != 4
            || !string.Equals(options.Platform, "3", StringComparison.Ordinal)
            || !string.Equals(options.AppName, "hk4e_global", StringComparison.Ordinal)
            || !string.Equals(options.SdkVersion, "2.53.0.196", StringComparison.Ordinal))
        {
            throw new ArgumentException("The device-fingerprint profile is not Windows Global 7.0.", nameof(options));
        }
        if (options.RequestTimeout <= TimeSpan.Zero || options.RequestTimeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The device-fingerprint timeout is invalid.");
        }
    }

    public async Task<OfficialDeviceFingerprintResult> RefreshAsync(
        SdkInstallationState state,
        OfficialDeviceFingerprintRuntime runtime,
        IOfficialDeviceAttributeCollector collector,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(collector);
        ValidateState(state);

        IReadOnlyList<string> names = await GetRequestedAttributesAsync(cancellationToken);
        var fields = new Dictionary<string, string>(names.Count, StringComparer.Ordinal);
        bool missingAttribute = false;
        foreach (string name in names)
        {
            if (!OfficialDeviceFingerprintFields.KnownNames.Contains(name))
            {
                throw new OfficialConnectivityException(
                    OfficialConnectivityError.UnsupportedDeviceFingerprintField,
                    "The device attribute list contains an unsupported field.");
            }
            string? value = await collector.CollectAsync(name, cancellationToken);
            if (value is null || value.Length > 4096)
            {
                missingAttribute = true;
                continue;
            }
            fields.Add(name, value);
        }
        if (missingAttribute)
        {
            throw Failure("A requested device attribute is unavailable.");
        }

        string body = JsonSerializer.Serialize(new FingerprintRequest(
            state.DeviceId,
            state.SeedId,
            state.SeedTime,
            _options.Platform,
            state.DeviceFingerprint ?? string.Empty,
            _options.AppName,
            fields));
        try
        {
            using var post = new HttpRequestMessage(HttpMethod.Post, _options.FingerprintEndpoint)
            {
                Content = OfficialRequestContent.DeviceFingerprintJson(body),
            };
            OfficialRequestContent.ApplyObservedTransportDefaults(post);
            using JsonDocument fingerprint = await SendAndReadAsync(post, cancellationToken);
            string received = ParseFingerprint(fingerprint.RootElement);
            return new OfficialDeviceFingerprintResult(
                state.WithServerFingerprint(received),
                OfficialDeviceFingerprintOrigin.ServerRefreshed,
                RejectionRetcode: null);
        }
        catch (OfficialConnectivityException exception)
            when (exception.Error == OfficialConnectivityError.SdkFingerprintUnavailable)
        {
            if (SdkInstallationState.IsValidFingerprint(state.DeviceFingerprint))
            {
                return new OfficialDeviceFingerprintResult(
                    state,
                    OfficialDeviceFingerprintOrigin.Persisted,
                    exception.Retcode);
            }

            return new OfficialDeviceFingerprintResult(
                state.WithDeviceFingerprint(CreateFallbackFingerprint()),
                OfficialDeviceFingerprintOrigin.GeneratedFallback,
                exception.Retcode);
        }
    }

    public async Task<IReadOnlyList<string>> GetRequestedAttributesAsync(
        CancellationToken cancellationToken = default)
    {
        var endpoint = new UriBuilder(_options.ExtListEndpoint)
        {
            Query = "platform=" + _options.Platform,
        }.Uri;
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        OfficialRequestContent.ApplyObservedTransportDefaults(request);
        using JsonDocument requested = await SendAndReadAsync(request, cancellationToken);
        return ParseRequestedAttributes(requested.RootElement);
    }

    public async Task<OfficialDeviceFingerprintResult> RefreshAndSaveAsync(
        SdkInstallationState state,
        string statePath,
        OfficialDeviceFingerprintRuntime runtime,
        IOfficialDeviceAttributeCollector collector,
        CancellationToken cancellationToken = default)
    {
        OfficialDeviceFingerprintResult result = await RefreshAsync(
            state, runtime, collector, cancellationToken);
        await result.Installation.SaveAsync(statePath, cancellationToken);
        return result;
    }

    public override string ToString() => "OfficialDeviceFingerprintExchange { Machine = [REDACTED] }";

    private async Task<JsonDocument> SendAndReadAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using (request)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(_options.RequestTimeout);
            try
            {
                using HttpResponseMessage response = await _httpClient.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (response.StatusCode != HttpStatusCode.OK
                    || response.Content.Headers.ContentLength is > MaximumResponseBytes)
                {
                    throw Failure("The device-fingerprint service did not return a usable response.");
                }

                await using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                using var buffer = new MemoryStream();
                byte[] chunk = ArrayPool<byte>.Shared.Rent(8192);
                try
                {
                    int count;
                    while ((count = await stream.ReadAsync(chunk, timeout.Token)) != 0)
                    {
                        if (buffer.Length + count > MaximumResponseBytes)
                        {
                            throw Failure("The device-fingerprint response exceeds its size limit.");
                        }
                        buffer.Write(chunk, 0, count);
                    }
                    return JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(chunk);
                    ArrayPool<byte>.Shared.Return(chunk);
                    if (buffer.TryGetBuffer(out ArraySegment<byte> segment))
                    {
                        CryptographicOperations.ZeroMemory(segment.AsSpan());
                    }
                }
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw Failure("The device-fingerprint request timed out.", exception);
            }
            catch (HttpRequestException exception)
            {
                throw Failure("The device-fingerprint endpoint could not be reached.", exception);
            }
            catch (JsonException exception)
            {
                throw Failure("The device-fingerprint response is invalid JSON.", exception);
            }
        }
    }

    private static List<string> ParseRequestedAttributes(JsonElement root)
    {
        int? retcode = ReadRetcode(root);
        if (retcode is not 0)
        {
            throw Failure(
                "The device attribute list request was rejected.", retcode: retcode);
        }
        if (!HasSuccessfulRetcode(root)
            || !root.TryGetProperty("data", out JsonElement data)
            || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("ext_list", out JsonElement list)
            || list.ValueKind != JsonValueKind.Array
            || list.GetArrayLength() > 64)
        {
            throw Failure("The device attribute list has an unsupported shape or result code.");
        }

        var names = new List<string>(list.GetArrayLength());
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement item in list.EnumerateArray())
        {
            string? name = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
            if (string.IsNullOrWhiteSpace(name) || name.Length > 64
                || !char.IsAsciiLetter(name[0])
                || !name.All(char.IsAsciiLetterOrDigit)
                || !seen.Add(name))
            {
                throw Failure("The device attribute list contains an invalid name.");
            }
            names.Add(name);
        }
        return names;
    }

    private static string ParseFingerprint(JsonElement root)
    {
        int? retcode = ReadRetcode(root);
        if (retcode is not 0)
        {
            throw Failure(
                "The device-fingerprint request was rejected.", retcode: retcode);
        }
        if (!HasSuccessfulRetcode(root)
            || !root.TryGetProperty("data", out JsonElement data)
            || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("device_fp", out JsonElement field)
            || field.ValueKind != JsonValueKind.String
            || !SdkInstallationState.IsValidFingerprint(field.GetString()))
        {
            throw Failure("The device-fingerprint response has an unsupported shape or result code.");
        }
        return field.GetString()!;
    }

    private static bool HasSuccessfulRetcode(JsonElement root) =>
        ReadRetcode(root) == 0;

    private static int? ReadRetcode(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty("retcode", out JsonElement retcode)
        && retcode.ValueKind == JsonValueKind.Number
        && retcode.TryGetInt32(out int code)
            ? code
            : null;

    private static void ValidateState(SdkInstallationState state)
    {
        if (state.SeedId.Length != 16
            || !state.SeedId.All(character => char.IsAsciiHexDigit(character)
                && !char.IsAsciiLetterUpper(character))
            || string.IsNullOrEmpty(state.SeedTime)
            || !state.SeedTime.All(char.IsAsciiDigit))
        {
            throw new ArgumentException("The SDK installation seeds are incomplete.", nameof(state));
        }
    }

    private static void ValidateEndpoint(Uri endpoint)
    {
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme != Uri.UriSchemeHttps
            || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
        {
            throw new ArgumentException("The device-fingerprint endpoint is invalid.", nameof(endpoint));
        }
    }

    private string CreateFallbackFingerprint()
    {
        char[] result = new char[10];
        for (int index = 0; index < result.Length; index++)
        {
            int digit = _randomDigit(10);
            if ((uint)digit >= 10)
            {
                throw new InvalidOperationException(
                    "The device-fingerprint random source returned an invalid digit.");
            }
            result[index] = (char)('0' + digit);
        }
        return new string(result);
    }

    private static OfficialConnectivityException Failure(
        string message,
        Exception? inner = null,
        int? retcode = null) =>
        new(OfficialConnectivityError.SdkFingerprintUnavailable, message, inner, retcode);

    private sealed record FingerprintRequest(
        [property: JsonPropertyName("device_id")] string DeviceId,
        [property: JsonPropertyName("seed_id")] string SeedId,
        [property: JsonPropertyName("seed_time")] string SeedTime,
        [property: JsonPropertyName("platform")] string Platform,
        [property: JsonPropertyName("device_fp")] string DeviceFingerprint,
        [property: JsonPropertyName("app_name")] string AppName,
        [property: JsonPropertyName("ext_fields")] Dictionary<string, string> ExtFields);
}
