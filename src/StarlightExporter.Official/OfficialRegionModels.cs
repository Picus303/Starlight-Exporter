namespace StarlightExporter.Official;

public sealed class OfficialSecret
{
    private readonly string _value;

    private OfficialSecret(string value)
    {
        _value = value;
    }

    public bool IsEmpty => string.IsNullOrEmpty(_value);

    internal string Reveal() => _value;

    public static OfficialSecret Create(string? value) => new(value ?? string.Empty);

    public override string ToString() => "[REDACTED]";
}

public sealed record OfficialRegion(
    string Name,
    string Title,
    string Type,
    Uri DispatchUri);

public sealed record OfficialRegionList(
    IReadOnlyList<OfficialRegion> Regions,
    byte[] ClientSecretKey,
    byte[] ClientCustomConfigEncrypted,
    bool EnableLoginPc);

public sealed record OfficialCurrentRegion
{
    public required string RegionName { get; init; }
    public required string GateServerIp { get; init; }
    public required uint GateServerPort { get; init; }
    public required bool UseGateServerDomainName { get; init; }
    public required string GateServerDomainName { get; init; }
    public required byte[] ClientSecretKey { get; init; }
    public required byte[] SecretKey { get; init; }
    public required OfficialSecret ConnectGateTicket { get; init; }
    public required uint ClientDataVersion { get; init; }
    public required uint ClientSilenceDataVersion { get; init; }
    public required string ClientDataMd5 { get; init; }
    public required string ClientSilenceDataMd5 { get; init; }
    public required string ClientVersionSuffix { get; init; }
    public required string ClientSilenceVersionSuffix { get; init; }
    public required string GameBiz { get; init; }
    public required string ResourceUrl { get; init; }
    public required string DataUrl { get; init; }

    public string GateHost => UseGateServerDomainName && !string.IsNullOrWhiteSpace(GateServerDomainName)
        ? GateServerDomainName
        : GateServerIp;
}
