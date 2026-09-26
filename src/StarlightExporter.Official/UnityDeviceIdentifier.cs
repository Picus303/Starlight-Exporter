using System.Security.Cryptography;
using System.Text;

namespace StarlightExporter.Official;

public static class UnityDeviceIdentifier
{
    private static readonly char[] TrimCharacters = [' ', '\t'];

    public static string FromWindowsSerials(
        IEnumerable<string?> baseBoardSerials,
        IEnumerable<string?> biosSerials,
        IEnumerable<string?> operatingSystemSerials)
    {
        ArgumentNullException.ThrowIfNull(baseBoardSerials);
        ArgumentNullException.ThrowIfNull(biosSerials);
        ArgumentNullException.ThrowIfNull(operatingSystemSerials);

        var source = new StringBuilder();
        Append(source, baseBoardSerials);
        Append(source, biosSerials);
        if (source.Length != 0)
        {
            Append(source, operatingSystemSerials);
        }
        return source.Length == 0 ? string.Empty : Sha1(source.ToString());
    }

    public static string FromPersistedGuid(Guid value) => Sha1(value.ToString("B"));

    private static void Append(StringBuilder target, IEnumerable<string?> values)
    {
        foreach (string? value in values)
        {
            if (value is not null)
            {
                target.Append(value.Trim(TrimCharacters));
            }
        }
    }

    private static string Sha1(string value)
    {
#pragma warning disable CA5350 // SHA-1 is required by the observed Unity identifier contract.
        return Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(value)));
#pragma warning restore CA5350
    }
}
