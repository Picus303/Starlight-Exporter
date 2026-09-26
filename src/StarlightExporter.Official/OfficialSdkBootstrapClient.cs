namespace StarlightExporter.Official;

public sealed record OfficialSdkBootstrapOptions
{
    public required Uri ShieldConfigEndpoint { get; init; }
    public required Uri ComboConfigEndpoint { get; init; }
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public static OfficialSdkBootstrapOptions WindowsGlobal700 { get; } = new()
    {
        ShieldConfigEndpoint = new Uri(
            "https://hk4e-sdk-os-static.hoyoverse.com/hk4e_global/mdk/shield/api/loadConfig"),
        ComboConfigEndpoint = new Uri("https://sdk-os-static.hoyoverse.com/combo/box/api/config/sdk/combo"),
    };
}

public sealed record OfficialSdkBootstrapResult(bool ShieldConfigReached, bool ComboConfigReached);

// The official client attempts these GETs in order. Its fallback paths make their
// success distinct from the password route itself (delivery_3/01_auth_route.json).
public sealed class OfficialSdkBootstrapClient
{
    private readonly HttpClient _httpClient;
    private readonly OfficialSdkBootstrapOptions _options;

    public OfficialSdkBootstrapClient(HttpClient httpClient, OfficialSdkBootstrapOptions options)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ValidateEndpoint(options.ShieldConfigEndpoint);
        ValidateEndpoint(options.ComboConfigEndpoint);
        if (options.RequestTimeout <= TimeSpan.Zero || options.RequestTimeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The SDK bootstrap timeout is invalid.");
        }
    }

    public async Task<OfficialSdkBootstrapResult> AttemptAsync(CancellationToken cancellationToken = default)
    {
        bool shield = await TryGetAsync(_options.ShieldConfigEndpoint, cancellationToken);
        bool combo = await TryGetAsync(_options.ComboConfigEndpoint, cancellationToken);
        return new OfficialSdkBootstrapResult(shield, combo);
    }

    private async Task<bool> TryGetAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.RequestTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            OfficialRequestContent.ApplyObservedTransportDefaults(request);
            using HttpResponseMessage response = await _httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    private static void ValidateEndpoint(Uri endpoint)
    {
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme != Uri.UriSchemeHttps
            || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
        {
            throw new ArgumentException("The SDK bootstrap endpoint is invalid.", nameof(endpoint));
        }
    }
}
