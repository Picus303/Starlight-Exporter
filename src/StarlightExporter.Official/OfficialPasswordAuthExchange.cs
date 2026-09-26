using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StarlightExporter.Official;

public sealed record OfficialPasswordAuthOptions
{
    public required Uri Endpoint { get; init; }
    public required string SdkPublicKeyPem { get; init; }
    public required OfficialSdkRequestHeaders Headers { get; init; }
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(15);

    // Public selector-0 key from delivery_3/03_auth_crypto.json; scoped to Windows Global 7.0.0.
    public static string WindowsGlobal700PublicKeyPem { get; } =
        "-----BEGIN PUBLIC KEY-----\n"
        + "MIGfMA0GCSqGSIb3DQEBAQUAA4GNADCBiQKBgQDDvekdPMHN3AYhm/vktJT+YJr7cI5DcsNKqdsx5DZX0gDuWFuIjzdwButrIYPNmRJ1G8ybDIF7oDW2eEpm5sMbL9zs9ExXCdvqrn51qELbqj0XxtMTIpaCHFSI50PfPpTFV9Xt/hmyVwokoOXFlAEgCn+QCgGs52bFoYMtyi+xEQIDAQAB\n"
        + "-----END PUBLIC KEY-----";

    public static Uri WindowsGlobal700Endpoint { get; } =
        new("https://hk4e-sdk-os.hoyoverse.com/hk4e_global/mdk/shield/api/login");

    public override string ToString() => "OfficialPasswordAuthOptions { Device = [REDACTED] }";
}

public sealed record OfficialPasswordCredentials(string Account, string Password)
{
    public override string ToString() => "OfficialPasswordCredentials { Secrets = [REDACTED] }";
}

public sealed class OfficialPasswordAuthProvider(
    OfficialPasswordAuthExchange exchange,
    Func<CancellationToken, Task<OfficialPasswordCredentials>> readCredentials) : ISdkSessionProvider
{
    public async Task<SdkSession> GetSessionAsync(CancellationToken cancellationToken = default)
    {
        OfficialPasswordCredentials credentials = await readCredentials(cancellationToken);
        return await exchange.LoginAsync(credentials.Account, credentials.Password, cancellationToken);
    }

    public override string ToString() => "OfficialPasswordAuthProvider { Secrets = [REDACTED] }";
}

public sealed class OfficialPasswordAuthExchange
{
    private const int MaximumResponseBytes = 2 * 1024 * 1024;
    private readonly HttpClient _httpClient;
    private readonly OfficialPasswordAuthOptions _options;

    public OfficialPasswordAuthExchange(HttpClient httpClient, OfficialPasswordAuthOptions options)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (!options.Endpoint.IsAbsoluteUri || options.Endpoint.Scheme != Uri.UriSchemeHttps
            || options.RequestTimeout <= TimeSpan.Zero || options.RequestTimeout > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentException("The SDK auth endpoint or timeout is invalid.", nameof(options));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(options.SdkPublicKeyPem);
        ArgumentNullException.ThrowIfNull(options.Headers);
        if (!SdkInstallationState.IsValidFingerprint(options.Headers.DeviceFingerprint))
        {
            throw new OfficialConnectivityException(
                OfficialConnectivityError.SdkAuthenticationRejected,
                "A current SDK device fingerprint is required before password auth.");
        }
        using RSA rsa = RSA.Create();
        rsa.ImportFromPem(options.SdkPublicKeyPem);
        if (rsa.KeySize != 1024)
        {
            throw new ArgumentException("The selected SDK auth key must be RSA-1024.", nameof(options));
        }
    }

    public async Task<SdkSession> LoginAsync(
        string account,
        string password,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(account);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        using RSA rsa = RSA.Create();
        rsa.ImportFromPem(_options.SdkPublicKeyPem);
        string encryptedAccount;
        string encryptedPassword;
        try
        {
            encryptedAccount = Encrypt(rsa, account);
            encryptedPassword = Encrypt(rsa, password);
        }
        catch (CryptographicException exception)
        {
            throw Failure("The SDK auth credentials could not be encrypted.", exception);
        }
        string body = JsonSerializer.Serialize(new AuthRequest(encryptedAccount, encryptedPassword));

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
        {
            Content = OfficialRequestContent.Json(body),
        };
        OfficialRequestContent.ApplyObservedTransportDefaults(request);
        _options.Headers.Apply(request);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.RequestTimeout);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw Failure("The SDK auth request timed out.", exception);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw Failure("The SDK auth endpoint could not be reached.", exception);
        }

        using (response)
        {
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw Failure($"The SDK auth endpoint returned HTTP {(int)response.StatusCode}.");
            }
            if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
            {
                throw Failure("The SDK auth response exceeds the size limit.");
            }
            string content;
            try
            {
                content = await response.Content.ReadAsStringAsync(timeout.Token);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw Failure("The SDK auth response timed out.", exception);
            }
            if (Encoding.UTF8.GetByteCount(content) > MaximumResponseBytes)
            {
                throw Failure("The SDK auth response exceeds the size limit.");
            }

            AuthResponse? result;
            try
            {
                result = JsonSerializer.Deserialize<AuthResponse>(content);
            }
            catch (JsonException exception)
            {
                throw Failure("The SDK auth response is invalid.", exception);
            }
            if (result is null || (result.Ret is null && result.Retcode is null)
                || (result.Ret is not null && result.Retcode is not null && result.Ret != result.Retcode))
            {
                throw Failure("The SDK auth response has no unambiguous result code.");
            }
            if (ChallengePresent(result.Content)
                || ChallengePresent(result.Data?.DeviceGrantRequired ?? default)
                || ChallengePresent(result.Data?.ReactivateRequired ?? default)
                || ChallengePresent(result.Data?.SafeMobileRequired ?? default)
                || ChallengePresent(result.Data?.RealnameOperation ?? default)
                || ChallengePresent(result.Data?.Account?.ReactivateTicket ?? default)
                || ChallengePresent(result.Data?.Account?.RealnameOperation ?? default))
            {
                throw new OfficialConnectivityException(
                    OfficialConnectivityError.SdkUserActionRequired,
                    "The SDK auth response requires an official user action.");
            }
            int retcode = result.Ret ?? result.Retcode ?? -1;
            if (retcode == -115)
            {
                throw new OfficialConnectivityException(
                    OfficialConnectivityError.SdkNetworkRisk,
                    "The SDK auth endpoint requested its network-risk workflow.",
                    retcode: retcode);
            }
            if (retcode != 0)
            {
                throw new OfficialConnectivityException(
                    OfficialConnectivityError.SdkAuthenticationRejected,
                    $"The SDK auth endpoint rejected the request with retcode {retcode}.",
                    retcode: retcode);
            }
            AuthAccount? accountData = result.Data?.Account;
            if (accountData is null || string.IsNullOrWhiteSpace(accountData.Uid)
                || (!accountData.IsGuest && string.IsNullOrWhiteSpace(accountData.Token)))
            {
                throw Failure("The SDK auth response does not contain a usable session.");
            }
            return SdkSession.Create(
                accountData.Uid,
                accountData.Token,
                accountData.IsGuest,
                accountData.Country);
        }
    }

    public override string ToString() => "OfficialPasswordAuthExchange { Secrets = [REDACTED] }";

    private static string Encrypt(RSA rsa, string value)
    {
        byte[] plaintext = Encoding.UTF8.GetBytes(value);
        try
        {
            return Convert.ToBase64String(rsa.Encrypt(plaintext, RSAEncryptionPadding.Pkcs1));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static bool ChallengePresent(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False or JsonValueKind.Null or JsonValueKind.Undefined => false,
        JsonValueKind.Number => !value.TryGetInt64(out long number) || number != 0,
        JsonValueKind.String => !string.IsNullOrWhiteSpace(value.GetString())
            && !string.Equals(value.GetString(), "false", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(value.GetString(), "0", StringComparison.Ordinal),
        JsonValueKind.Object or JsonValueKind.Array => true,
        _ => false,
    };

    private static OfficialConnectivityException Failure(string message, Exception? inner = null) =>
        new(OfficialConnectivityError.SdkAuthenticationRejected, message, inner);

    private sealed record AuthRequest(
        [property: JsonPropertyName("account")] string Account,
        [property: JsonPropertyName("password")] string Password);

    private sealed record AuthResponse
    {
        [JsonPropertyName("ret")]
        public int? Ret { get; init; }

        [JsonPropertyName("retcode")]
        public int? Retcode { get; init; }

        [JsonPropertyName("content")]
        public JsonElement Content { get; init; }

        [JsonPropertyName("data")]
        public AuthResponseData? Data { get; init; }
    }

    private sealed record AuthResponseData
    {
        [JsonPropertyName("account")]
        public AuthAccount? Account { get; init; }

        [JsonPropertyName("device_grant_required")]
        public JsonElement DeviceGrantRequired { get; init; }

        [JsonPropertyName("reactivate_required")]
        public JsonElement ReactivateRequired { get; init; }

        [JsonPropertyName("safe_moblie_required")]
        public JsonElement SafeMobileRequired { get; init; }

        [JsonPropertyName("realname_operation")]
        public JsonElement RealnameOperation { get; init; }
    }

    private sealed record AuthAccount
    {
        [JsonPropertyName("uid")]
        public string Uid { get; init; } = string.Empty;

        [JsonPropertyName("token")]
        public string Token { get; init; } = string.Empty;

        [JsonPropertyName("is_guest")]
        public bool IsGuest { get; init; }

        [JsonPropertyName("country")]
        public string Country { get; init; } = string.Empty;

        [JsonPropertyName("reactivate_ticket")]
        public JsonElement ReactivateTicket { get; init; }

        [JsonPropertyName("realname_operation")]
        public JsonElement RealnameOperation { get; init; }
    }
}
