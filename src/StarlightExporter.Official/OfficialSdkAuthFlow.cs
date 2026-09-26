namespace StarlightExporter.Official;

public sealed record OfficialSdkAuthResult(
    SdkSession Session,
    SdkInstallationState Installation,
    OfficialSdkBootstrapResult Bootstrap,
    OfficialDeviceFingerprintOrigin FingerprintOrigin,
    int? FingerprintRejectionRetcode);

// Stops at the first account-bearing response. Credentials are requested only
// after the SDK installation and its server fingerprint are initialized.
public sealed class OfficialSdkAuthFlow(
    HttpClient httpClient,
    OfficialSdkInitializer initializer,
    OfficialPasswordAuthOptions authOptions,
    Func<OfficialSdkInitializationResult, CancellationToken, Task>? initializationObserver = null)
{
    public async Task<OfficialSdkAuthResult> RunAsync(
        string statePath,
        string deviceUniqueIdentifier,
        OfficialDeviceFingerprintRuntime runtime,
        IOfficialDeviceAttributeCollector attributes,
        Func<CancellationToken, Task<OfficialPasswordCredentials>> readCredentials,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(readCredentials);
        OfficialSdkInitializationResult sdk = await initializer.InitializeAsync(
            statePath, deviceUniqueIdentifier, runtime, attributes, cancellationToken);
        if (initializationObserver is not null)
        {
            await initializationObserver(sdk, cancellationToken);
        }
        var authentication = new OfficialPasswordAuthExchange(httpClient, authOptions with
        {
            Headers = authOptions.Headers.WithInstallation(sdk.Installation),
        });
        OfficialPasswordCredentials credentials = await readCredentials(cancellationToken);
        SdkSession session = await authentication.LoginAsync(
            credentials.Account, credentials.Password, cancellationToken);
        return new OfficialSdkAuthResult(
            session,
            sdk.Installation,
            sdk.Bootstrap,
            sdk.FingerprintOrigin,
            sdk.FingerprintRejectionRetcode);
    }
}
