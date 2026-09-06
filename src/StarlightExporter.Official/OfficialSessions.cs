namespace StarlightExporter.Official;

public sealed class ComboSession
{
    private ComboSession(
        string accountUid,
        OfficialSecret accountToken,
        uint accountType,
        bool isGuest,
        string countryCode,
        uint? expectedUid)
    {
        AccountUid = accountUid;
        AccountToken = accountToken;
        AccountType = accountType;
        IsGuest = isGuest;
        CountryCode = countryCode;
        ExpectedUid = expectedUid;
    }

    public string AccountUid { get; }
    public uint AccountType { get; }
    public bool IsGuest { get; }
    public string CountryCode { get; }
    public uint? ExpectedUid { get; }
    internal OfficialSecret AccountToken { get; }

    public static ComboSession Create(
        string accountUid,
        string accountToken,
        uint accountType = 1,
        bool isGuest = false,
        string countryCode = "",
        uint? expectedUid = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountUid);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountToken);
        if (accountUid.Length > 64 || accountToken.Length > 4096 || countryCode.Length > 8)
        {
            throw new ArgumentException("The Combo session contains an oversized field.");
        }
        if (expectedUid == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expectedUid), "An expected UID must be non-zero.");
        }

        return new ComboSession(
            accountUid,
            OfficialSecret.Create(accountToken),
            accountType,
            isGuest,
            countryCode,
            expectedUid);
    }

    public override string ToString() =>
        $"ComboSession {{ AccountUid = [REDACTED], AccountType = {AccountType}, IsGuest = {IsGuest}, ExpectedUid = {ExpectedUid}, AccountToken = [REDACTED] }}";
}
