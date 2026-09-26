namespace StarlightExporter.Official;

public sealed record OfficialSdkInitializationResult(
    SdkInstallationState Installation,
    OfficialSdkBootstrapResult Bootstrap,
    OfficialDeviceFingerprintOrigin FingerprintOrigin,
    int? FingerprintRejectionRetcode);

// Keeps the observed SDK initialization order in one place. A failed config GET
// is reported but does not suppress fingerprint acquisition, as in the client.
public sealed class OfficialSdkInitializer(
    OfficialSdkBootstrapClient bootstrap,
    OfficialDeviceFingerprintExchange fingerprint)
{
    public async Task<OfficialSdkInitializationResult> InitializeAsync(
        string statePath,
        string deviceUniqueIdentifier,
        OfficialDeviceFingerprintRuntime runtime,
        IOfficialDeviceAttributeCollector collector,
        CancellationToken cancellationToken = default)
    {
        SdkInstallationState installation = await SdkInstallationState.LoadOrCreateAsync(
            statePath, deviceUniqueIdentifier, cancellationToken: cancellationToken);
        OfficialSdkBootstrapResult bootstrapResult = await bootstrap.AttemptAsync(cancellationToken);
        OfficialDeviceFingerprintResult refreshed = await fingerprint.RefreshAndSaveAsync(
            installation, statePath, runtime, collector, cancellationToken);
        return new OfficialSdkInitializationResult(
            refreshed.Installation,
            bootstrapResult,
            refreshed.Origin,
            refreshed.RejectionRetcode);
    }
}
