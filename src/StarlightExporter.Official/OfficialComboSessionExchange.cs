using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StarlightExporter.Official;

public sealed record OfficialComboOptions
{
    public required Uri Endpoint { get; init; }
    public required string DeviceId { get; init; }
    public required uint ApplicationId { get; init; }
    public required uint ChannelId { get; init; }
    public uint? ExpectedPlayerUid { get; init; }
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(15);
}

public sealed class OfficialComboSessionExchange : IComboSessionExchange, IDisposable
{
    private const int MaximumResponseBytes = 2 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    private readonly HttpClient _httpClient;
    private readonly OfficialComboOptions _options;
    private readonly byte[] _hmacKey;
    private bool _disposed;

    public OfficialComboSessionExchange(
        HttpClient httpClient,
        OfficialComboOptions options,
        string hmacKey)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ValidateOptions(options);
        if (string.IsNullOrEmpty(hmacKey))
        {
            throw new OfficialConnectivityException(
                OfficialConnectivityError.ComboConfigurationMissing,
                "The Combo HMAC key is not configured.");
        }
        if (hmacKey.Length > 4096)
        {
            throw new ArgumentException("The Combo HMAC key is oversized.", nameof(hmacKey));
        }

        _hmacKey = Encoding.UTF8.GetBytes(hmacKey);
    }

    public async Task<ComboSession> ExchangeAsync(
        SdkSession sdkSession,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(sdkSession);

        string data = JsonSerializer.Serialize(new ComboRequestData(
            sdkSession.AccountUid,
            sdkSession.IsGuest,
            sdkSession.Token.Reveal()), JsonOptions);
        string canonical = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"app_id={_options.ApplicationId}&channel_id={_options.ChannelId}&data={data}&device={_options.DeviceId}");
        string signature = CreateSignature(canonical);
        string body = JsonSerializer.Serialize(new ComboRequest(
            _options.ApplicationId,
            _options.ChannelId,
            data,
            _options.DeviceId,
            signature), JsonOptions);

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("x-rpc-device_id", _options.DeviceId);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.RequestTimeout);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw Failure("The Combo request timed out.", exception);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw Failure("The Combo endpoint could not be reached.", exception);
        }

        using (response)
        {
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw Failure($"The Combo endpoint returned HTTP {(int)response.StatusCode}.");
            }
            if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
            {
                throw Failure("The Combo response exceeds the size limit.");
            }

            string content;
            try
            {
                content = await response.Content.ReadAsStringAsync(timeout.Token);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw Failure("The Combo request timed out.", exception);
            }
            if (Encoding.UTF8.GetByteCount(content) > MaximumResponseBytes)
            {
                throw Failure("The Combo response exceeds the size limit.");
            }

            ComboResponseEnvelope envelope;
            try
            {
                envelope = JsonSerializer.Deserialize<ComboResponseEnvelope>(content, JsonOptions)
                    ?? throw new JsonException();
            }
            catch (JsonException exception)
            {
                throw Failure("The Combo response is invalid.", exception);
            }
            if (envelope.Retcode != 0)
            {
                throw Failure($"The Combo endpoint rejected the session with retcode {envelope.Retcode}.");
            }
            if (envelope.Data is null
                || string.IsNullOrWhiteSpace(envelope.Data.OpenId)
                || string.IsNullOrWhiteSpace(envelope.Data.ComboToken))
            {
                throw Failure("The Combo response does not contain a usable session.");
            }

            ComboResponseData extra;
            try
            {
                extra = JsonSerializer.Deserialize<ComboResponseData>(envelope.Data.Data, JsonOptions)
                    ?? new ComboResponseData();
            }
            catch (JsonException exception)
            {
                throw Failure("The Combo response metadata is invalid.", exception);
            }

            return ComboSession.Create(
                envelope.Data.OpenId,
                envelope.Data.ComboToken,
                envelope.Data.AccountType,
                extra.Guest,
                extra.CountryCode,
                _options.ExpectedPlayerUid);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(_hmacKey);
        _disposed = true;
    }

    public override string ToString() =>
        $"OfficialComboSessionExchange {{ Endpoint = {_options.Endpoint.GetLeftPart(UriPartial.Authority)}, Secrets = [REDACTED] }}";

    private string CreateSignature(string canonical)
    {
        byte[] content = Encoding.UTF8.GetBytes(canonical);
        try
        {
            return Convert.ToHexStringLower(HMACSHA256.HashData(_hmacKey, content));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(content);
        }
    }

    private static void ValidateOptions(OfficialComboOptions options)
    {
        if (!options.Endpoint.IsAbsoluteUri
            || options.Endpoint.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrWhiteSpace(options.DeviceId)
            || options.DeviceId.Length > 256
            || options.ApplicationId == 0
            || options.ChannelId == 0
            || options.ExpectedPlayerUid == 0
            || options.RequestTimeout <= TimeSpan.Zero
            || options.RequestTimeout > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentException("The Combo options are invalid.", nameof(options));
        }
    }

    private static OfficialConnectivityException Failure(
        string message,
        Exception? innerException = null) =>
        new(OfficialConnectivityError.ComboExchangeRejected, message, innerException);

    private sealed record ComboRequest(
        [property: JsonPropertyName("app_id")] uint AppId,
        [property: JsonPropertyName("channel_id")] uint ChannelId,
        [property: JsonPropertyName("data")] string Data,
        [property: JsonPropertyName("device")] string Device,
        [property: JsonPropertyName("sign")] string Sign);

    private sealed record ComboRequestData(
        [property: JsonPropertyName("uid")] string Uid,
        [property: JsonPropertyName("guest")] bool Guest,
        [property: JsonPropertyName("token")] string Token);

    private sealed record ComboResponseEnvelope
    {
        [JsonPropertyName("retcode")]
        public int Retcode { get; init; }

        [JsonPropertyName("data")]
        public ComboResponse? Data { get; init; }
    }

    private sealed record ComboResponse
    {
        [JsonPropertyName("open_id")]
        public string OpenId { get; init; } = string.Empty;

        [JsonPropertyName("combo_token")]
        public string ComboToken { get; init; } = string.Empty;

        [JsonPropertyName("data")]
        public string Data { get; init; } = "{}";

        [JsonPropertyName("account_type")]
        public uint AccountType { get; init; } = 1;
    }

    private sealed record ComboResponseData
    {
        [JsonPropertyName("guest")]
        public bool Guest { get; init; }

        [JsonPropertyName("country_code")]
        public string CountryCode { get; init; } = string.Empty;
    }
}
