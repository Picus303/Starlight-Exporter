using StarlightExporter.Snapshot;

namespace StarlightExporter.Official;

public interface IOfficialConnectedMessageSource : IOfficialMessageSource, IAsyncDisposable
{
    uint PlayerUid { get; }
    string RegionName { get; }
}

public interface IOfficialGateSessionConnector
{
    Task<IOfficialConnectedMessageSource> ConnectAsync(
        ComboSession session,
        OfficialCurrentRegion region,
        OfficialClientProfile clientProfile,
        OfficialPlayerLoginProfile loginProfile,
        OfficialGateSessionOptions? options = null,
        CancellationToken cancellationToken = default);
}

public sealed class OfficialLiveGateSessionConnector : IOfficialGateSessionConnector
{
    public async Task<IOfficialConnectedMessageSource> ConnectAsync(
        ComboSession session,
        OfficialCurrentRegion region,
        OfficialClientProfile clientProfile,
        OfficialPlayerLoginProfile loginProfile,
        OfficialGateSessionOptions? options = null,
        CancellationToken cancellationToken = default) =>
        await LiveGateMessageSource.ConnectAsync(
            session, region, clientProfile, loginProfile, options, cancellationToken);
}

// Produces the smallest useful Starlight snapshot: player identity from Gate,
// player data, inventory, avatars and teams. Social values remain optional in
// the target schema and their unknown status is retained in Unsupported.
public sealed class OfficialAccountCaptureFlow(
    IOfficialSdkComboFlow sdkCombo,
    IOfficialDispatchClient dispatch,
    IOfficialGateSessionConnector gate,
    OfficialClientProfile clientProfile,
    TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<OfficialSnapshot> CaptureAsync(
        string statePath,
        string deviceUniqueIdentifier,
        OfficialDeviceFingerprintRuntime fingerprintRuntime,
        IOfficialDeviceAttributeCollector attributes,
        Func<CancellationToken, Task<OfficialPasswordCredentials>> readCredentials,
        string regionName,
        Func<SdkInstallationState, OfficialPlayerLoginProfile> buildLoginProfile,
        OfficialGateSessionOptions? gateOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(regionName);
        ArgumentNullException.ThrowIfNull(buildLoginProfile);

        OfficialSdkComboResult sdk = await sdkCombo.RunAsync(
            statePath, deviceUniqueIdentifier, fingerprintRuntime, attributes,
            readCredentials, cancellationToken);
        OfficialCurrentRegion region = await dispatch.ResolveRegionAsync(
            clientProfile, regionName, sdk.Session, cancellationToken);
        OfficialPlayerLoginProfile login = buildLoginProfile(sdk.Installation);
        if (!string.Equals(login.DeviceUuid, sdk.Installation.DeviceId, StringComparison.Ordinal)
            || !string.Equals(login.DeviceFingerprint, sdk.Installation.DeviceFingerprint,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The Gate login profile must reuse the initialized SDK device identity.",
                nameof(buildLoginProfile));
        }

        await using IOfficialConnectedMessageSource source = await gate.ConnectAsync(
            sdk.Session, region, clientProfile, login, gateOptions, cancellationToken);
        if (source.PlayerUid == 0)
        {
            throw new OfficialConnectivityException(
                OfficialConnectivityError.PlayerLoginRejected,
                "Gate did not provide a usable player UID.");
        }
        return await new OfficialSnapshotCollector().CollectAsync(
            new OfficialCaptureContext(source.PlayerUid, source.RegionName, _clock.GetUtcNow()),
            source, cancellationToken);
    }
}
