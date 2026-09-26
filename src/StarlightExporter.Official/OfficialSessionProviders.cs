namespace StarlightExporter.Official;

public sealed class SdkSession
{
    private SdkSession(string accountUid, OfficialSecret token, bool isGuest, string country)
    {
        AccountUid = accountUid;
        Token = token;
        IsGuest = isGuest;
        Country = country;
    }

    public string AccountUid { get; }
    public bool IsGuest { get; }
    public string Country { get; }
    internal OfficialSecret Token { get; }

    public static SdkSession Create(
        string accountUid,
        string token,
        bool isGuest = false,
        string country = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountUid);
        if (!isGuest)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(token);
        }
        if (accountUid.Length > 64 || token.Length > 4096 || country.Length > 8)
        {
            throw new ArgumentException("The SDK session contains an oversized field.");
        }

        return new SdkSession(accountUid, OfficialSecret.Create(token), isGuest, country);
    }

    public override string ToString() =>
        $"SdkSession {{ AccountUid = [REDACTED], IsGuest = {IsGuest}, Token = [REDACTED] }}";
}

public interface ISdkSessionProvider
{
    Task<SdkSession> GetSessionAsync(CancellationToken cancellationToken = default);
}

public interface IComboSessionProvider
{
    Task<ComboSession> GetSessionAsync(CancellationToken cancellationToken = default);
}

public interface IComboSessionExchange
{
    Task<ComboSession> ExchangeAsync(
        SdkSession sdkSession,
        CancellationToken cancellationToken = default);
}

public sealed class ExistingComboSessionProvider(ComboSession session) : IComboSessionProvider
{
    private readonly ComboSession _session = session ?? throw new ArgumentNullException(nameof(session));

    public Task<ComboSession> GetSessionAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_session);
    }

    public override string ToString() => "ExistingComboSessionProvider { Session = [REDACTED] }";
}

public sealed class OfficialSdkComboSessionProvider(
    ISdkSessionProvider sdkSessionProvider,
    IComboSessionExchange comboExchange) : IComboSessionProvider
{
    private readonly ISdkSessionProvider _sdkSessionProvider =
        sdkSessionProvider ?? throw new ArgumentNullException(nameof(sdkSessionProvider));
    private readonly IComboSessionExchange _comboExchange =
        comboExchange ?? throw new ArgumentNullException(nameof(comboExchange));

    public async Task<ComboSession> GetSessionAsync(CancellationToken cancellationToken = default)
    {
        SdkSession sdkSession = await _sdkSessionProvider.GetSessionAsync(cancellationToken);
        return await _comboExchange.ExchangeAsync(sdkSession, cancellationToken);
    }

    public override string ToString() => "OfficialSdkComboSessionProvider { Secrets = [REDACTED] }";
}
