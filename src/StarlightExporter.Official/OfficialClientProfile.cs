using System.Globalization;

namespace StarlightExporter.Official;

public sealed record OfficialClientProfile
{
    public required Uri GlobalDispatchUri { get; init; }
    public required string Version { get; init; }
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

    public static OfficialClientProfile OsGlobalV70 { get; } = new() {
        GlobalDispatchUri = new Uri("https://dispatchosglobal.yuanshen.com/query_region_list"),
        Version = "OSRELWin7.0.0",
        ProtocolVersion = "V70",
        Language = 2,
        Platform = 3,
        Binary = 1,
        ChannelId = 1,
        SubChannelId = 3,
        KeyId = 5,
        ApplicationId = 4,
    };

    internal IReadOnlyList<KeyValuePair<string, string>> DispatchParameters(
        TimeProvider timeProvider,
        bool regional)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        var parameters = new List<KeyValuePair<string, string>> {
            Pair("version", Version),
            Pair("lang", Language),
            Pair("platform", Platform),
            Pair("binary", Binary),
            Pair("time", timeProvider.GetUtcNow().ToUnixTimeSeconds()),
            Pair("channel_id", ChannelId),
            Pair("sub_channel_id", SubChannelId),
        };

        if (AccountType is { } accountType)
        {
            parameters.Add(Pair("account_type", accountType));
        }

        if (regional)
        {
            if (!string.IsNullOrWhiteSpace(DispatchSeed))
            {
                parameters.Add(Pair("dispatchSeed", DispatchSeed));
            }

            parameters.Add(Pair("key_id", KeyId));
            parameters.Add(Pair("aid", ApplicationId));
        }

        return parameters;
    }

    private static KeyValuePair<string, string> Pair(string key, object value) =>
        new(key, Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
}
