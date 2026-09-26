using System.Globalization;

namespace StarlightExporter.Official;

public sealed record OfficialClientProfile
{
    public required Uri GlobalDispatchUri { get; init; }
    public required string Version { get; init; }
    public string GameVersion { get; init; } = string.Empty;
    public required string ProtocolVersion { get; init; }
    public required uint Language { get; init; }
    public required uint Platform { get; init; }
    public required uint Binary { get; init; }
    public required uint ChannelId { get; init; }
    public required uint SubChannelId { get; init; }
    public uint? AccountType { get; init; }
    public required uint KeyId { get; init; }
    public required uint ApplicationId { get; init; }
    public string? DispatchSeed { get; init; }
    public string Cps { get; init; } = string.Empty;
    public string Uapc { get; init; } = string.Empty;
    public Uri? ComboEndpoint { get; init; }
    public string? ComboHmacKey { get; init; }
    public string GateServerPublicKeyPem { get; init; } = OfficialRsaKeyProfile.OsGlobalV70GatePublicKeyPem;

    public static OfficialClientProfile OsGlobalV70 { get; } = new()
    {
        GlobalDispatchUri = new Uri("https://dispatchosglobal.yuanshen.com/query_region_list"),
        Version = "OSRELWin7.0.0",
        GameVersion = "7.0.0",
        ProtocolVersion = "V70",
        Language = 4,
        Platform = 3,
        Binary = 1,
        ChannelId = 1,
        SubChannelId = 0,
        KeyId = 5,
        ApplicationId = 4,
        DispatchSeed = "a581ee28ea5494e6",
        Cps = "hoyoverse",
        Uapc = "1030d792fc42_",
        ComboEndpoint = new Uri("https://hk4e-sdk-os.hoyoverse.com/hk4e_global/combo/granter/login/v2/login"),
        ComboHmacKey = "6a4c78fe0356ba4673b8071127b28123",
    };

    internal IReadOnlyList<KeyValuePair<string, string>> DispatchParameters(
        TimeProvider timeProvider,
        bool regional,
        ComboSession? session = null)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        var parameters = new List<KeyValuePair<string, string>> {
            Pair("version", regional ? GameVersion : Version),
            Pair("lang", Language),
            Pair("platform", Platform),
            Pair("binary", Binary),
            Pair("time", timeProvider.GetLocalNow().Millisecond),
            Pair("channel_id", ChannelId),
            Pair("sub_channel_id", SubChannelId),
        };

        if (regional)
        {
            parameters.Add(Pair("account_type", session?.AccountType ?? AccountType ?? 0));
            if (!string.IsNullOrWhiteSpace(DispatchSeed))
            {
                parameters.Add(Pair("dispatchSeed", DispatchSeed));
            }

            parameters.Add(Pair("key_id", KeyId));
            if (session is not null)
            {
                parameters.Add(Pair("aid", session.AccountUid));
            }
        }

        return parameters;
    }

    public override string ToString() =>
        $"OfficialClientProfile {{ Version = {Version}, Protocol = {ProtocolVersion}, Secrets = [REDACTED] }}";

    private static KeyValuePair<string, string> Pair(string key, object value) =>
        new(key, Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
}
