using System.Globalization;

namespace StarlightExporter.Official;

// Shared AccountPlat header map observed in delivery_3/02_auth_http.tsv and 07_combo_http.tsv.
// Machine values and the fingerprint must come from the current installation.
public sealed record OfficialSdkRequestHeaders
{
    public required string DeviceId { get; init; }
    public required string Language { get; init; }
    public required uint AppId { get; init; }
    public required uint ClientType { get; init; }
    public required string GameBiz { get; init; }
    public required uint ChannelId { get; init; }
    public required string SdkVersion { get; init; }
    public required string MdkVersion { get; init; }
    public required string ChannelVersion { get; init; }
    public required string DeviceFingerprint { get; init; }
    public required string SystemVersion { get; init; }
    public required string DeviceModel { get; init; }
    public required string DeviceName { get; init; }
    public string LifecycleId { get; init; } = string.Empty;
    public string? Lrsag { get; init; }
    public bool AutoTest { get; init; }

    public override string ToString() => "OfficialSdkRequestHeaders { Device = [REDACTED] }";

    public OfficialSdkRequestHeaders WithInstallation(SdkInstallationState installation)
    {
        ArgumentNullException.ThrowIfNull(installation);
        if (!SdkInstallationState.IsValidFingerprint(installation.DeviceFingerprint))
        {
            throw new OfficialConnectivityException(
                OfficialConnectivityError.SdkFingerprintUnavailable,
                "A server device fingerprint is required for SDK requests.");
        }
        if (string.IsNullOrWhiteSpace(installation.DeviceId))
        {
            throw new ArgumentException("The SDK installation device ID is missing.", nameof(installation));
        }
        return this with
        {
            DeviceId = installation.DeviceId,
            DeviceFingerprint = installation.DeviceFingerprint!,
        };
    }

    public void Apply(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        Require(DeviceId, nameof(DeviceId));
        Require(Language, nameof(Language));
        Require(GameBiz, nameof(GameBiz));
        Require(SdkVersion, nameof(SdkVersion));
        Require(MdkVersion, nameof(MdkVersion));
        Require(ChannelVersion, nameof(ChannelVersion));
        if (!SdkInstallationState.IsValidFingerprint(DeviceFingerprint))
        {
            throw new OfficialConnectivityException(
                OfficialConnectivityError.SdkFingerprintUnavailable,
                "A valid server device fingerprint is required for SDK requests.");
        }
        if (AppId == 0 || ChannelId == 0)
        {
            throw new ArgumentException("The SDK application or channel ID is invalid.");
        }

        Add(request, "x-rpc-device_id", DeviceId);
        Add(request, "x-rpc-language", Language);
        Add(request, "x-rpc-app_id", AppId.ToString(CultureInfo.InvariantCulture));
        Add(request, "x-rpc-client_type", ClientType.ToString(CultureInfo.InvariantCulture));
        Add(request, "x-rpc-game_biz", GameBiz);
        Add(request, "x-rpc-channel_id", ChannelId.ToString(CultureInfo.InvariantCulture));
        Add(request, "x-rpc-sdk_version", SdkVersion);
        Add(request, "x-rpc-device_fp", DeviceFingerprint);
        Add(request, "x-rpc-sys_version", SystemVersion);
        Add(request, "x-rpc-device_model", DeviceModel);
        Add(request, "x-rpc-device_name", DeviceName);
        Add(request, "x-rpc-mdk_version", MdkVersion);
        Add(request, "x-rpc-channel_version", ChannelVersion);
        Add(request, "x-rpc-lifecycle_id", LifecycleId);
        if (Lrsag is not null)
        {
            Add(request, "x-rpc-lrsag", Lrsag);
        }
        if (AutoTest)
        {
            Add(request, "x-rpc-auto_test", "true");
        }
    }

    private static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\r') || value.Contains('\n'))
        {
            throw new ArgumentException($"The SDK {name} is missing or invalid.", name);
        }
    }

    private static void Add(HttpRequestMessage request, string name, string value)
    {
        if (value.Contains('\r') || value.Contains('\n')
            || !request.Headers.TryAddWithoutValidation(name, value))
        {
            throw new ArgumentException($"The SDK {name} header is invalid.", name);
        }
    }
}
