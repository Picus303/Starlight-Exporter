namespace StarlightExporter.Official;

public sealed record OfficialSdkComboResult(
    ComboSession Session,
    SdkInstallationState Installation,
    OfficialSdkBootstrapResult Bootstrap,
    OfficialDeviceFingerprintOrigin FingerprintOrigin,
    int? FingerprintRejectionRetcode);

public interface IOfficialSdkComboFlow
{
    Task<OfficialSdkComboResult> RunAsync(
        string statePath,
        string deviceUniqueIdentifier,
        OfficialDeviceFingerprintRuntime runtime,
        IOfficialDeviceAttributeCollector attributes,
        Func<CancellationToken, Task<OfficialPasswordCredentials>> readCredentials,
        CancellationToken cancellationToken = default);
}

// One session owns one installation identity from SDK bootstrap through Combo.
// The caller supplies build-scoped parameters and reads credentials only after
// the fingerprint step succeeds. The SDK token remains inside the exchange;
// the resulting Combo session is retained in memory for Dispatch and Gate.
public sealed class OfficialSdkComboFlow(
    HttpClient httpClient,
    OfficialSdkInitializer initializer,
    OfficialPasswordAuthOptions authOptions,
    OfficialComboOptions comboOptions,
    string comboHmacKey) : IOfficialSdkComboFlow
{
    public async Task<OfficialSdkComboResult> RunAsync(
        string statePath,
        string deviceUniqueIdentifier,
        OfficialDeviceFingerprintRuntime runtime,
        IOfficialDeviceAttributeCollector attributes,
        Func<CancellationToken, Task<OfficialPasswordCredentials>> readCredentials,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(readCredentials);
        OfficialSdkAuthResult auth = await new OfficialSdkAuthFlow(
            httpClient, initializer, authOptions).RunAsync(
                statePath, deviceUniqueIdentifier, runtime, attributes,
                readCredentials, cancellationToken);
        OfficialSdkRequestHeaders headers = authOptions.Headers.WithInstallation(auth.Installation);
        using var combo = new OfficialComboSessionExchange(httpClient, comboOptions with
        {
            DeviceId = auth.Installation.DeviceId,
            Headers = headers,
        }, comboHmacKey);
        ComboSession session = await combo.ExchangeAsync(auth.Session, cancellationToken);
        return new OfficialSdkComboResult(
            session,
            auth.Installation,
            auth.Bootstrap,
            auth.FingerprintOrigin,
            auth.FingerprintRejectionRetcode);
    }
}
