namespace StarlightExporter.Official;

public sealed record DispatchRegionSummary(string Name, string Title, string Type);

public sealed record DispatchListProbeResult
{
    public required string Version { get; init; }
    public required string ProtocolVersion { get; init; }
    public required uint Language { get; init; }
    public required uint Platform { get; init; }
    public required uint Binary { get; init; }
    public required uint ChannelId { get; init; }
    public required uint SubChannelId { get; init; }
    public required bool EnableLoginPc { get; init; }
    public required int ClientSecretKeyBytes { get; init; }
    public required IReadOnlyList<DispatchRegionSummary> Regions { get; init; }
}

public sealed record RegionalDispatchProbeResult
{
    public required string RegionName { get; init; }
    public required OfficialRegionalPayloadFormat PayloadFormat { get; init; }
    public required uint KeyId { get; init; }
    public required bool GateHostPresent { get; init; }
    public required uint GatePort { get; init; }
    public required bool UsesDomainName { get; init; }
    public required bool ConnectGateTicketPresent { get; init; }
    public required uint ClientDataVersion { get; init; }
    public required uint ClientSilenceDataVersion { get; init; }
    public required string GameBiz { get; init; }
    public required bool ResourceUrlPresent { get; init; }
    public required bool DataUrlPresent { get; init; }
}

public sealed class OfficialDispatchProbe(IOfficialDispatchClient dispatchClient)
{
    private readonly IOfficialDispatchClient _dispatchClient =
        dispatchClient ?? throw new ArgumentNullException(nameof(dispatchClient));

    public async Task<DispatchListProbeResult> ProbeListAsync(
        OfficialClientProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        OfficialRegionList list = await _dispatchClient.GetRegionsAsync(profile, cancellationToken);

        return new DispatchListProbeResult
        {
            Version = profile.Version,
            ProtocolVersion = profile.ProtocolVersion,
            Language = profile.Language,
            Platform = profile.Platform,
            Binary = profile.Binary,
            ChannelId = profile.ChannelId,
            SubChannelId = profile.SubChannelId,
            EnableLoginPc = list.EnableLoginPc,
            ClientSecretKeyBytes = list.ClientSecretKey.Length,
            Regions = list.Regions
                .Select(region => new DispatchRegionSummary(region.Name, region.Title, region.Type))
                .ToArray(),
        };
    }

    public async Task<RegionalDispatchProbeResult> ProbeRegionAsync(
        OfficialClientProfile profile,
        string regionName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(regionName);
        OfficialCurrentRegion region = await _dispatchClient.ResolveRegionAsync(
            profile,
            regionName,
            cancellationToken);

        return new RegionalDispatchProbeResult
        {
            RegionName = region.RegionName,
            PayloadFormat = region.PayloadFormat,
            KeyId = profile.KeyId,
            GateHostPresent = !string.IsNullOrWhiteSpace(region.GateHost),
            GatePort = region.GateServerPort,
            UsesDomainName = region.UseGateServerDomainName,
            ConnectGateTicketPresent = !region.ConnectGateTicket.IsEmpty,
            ClientDataVersion = region.ClientDataVersion,
            ClientSilenceDataVersion = region.ClientSilenceDataVersion,
            GameBiz = region.GameBiz,
            ResourceUrlPresent = !string.IsNullOrWhiteSpace(region.ResourceUrl),
            DataUrlPresent = !string.IsNullOrWhiteSpace(region.DataUrl),
        };
    }
}
