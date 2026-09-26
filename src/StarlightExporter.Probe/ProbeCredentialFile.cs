using StarlightExporter.Official;

namespace StarlightExporter.Probe;

internal sealed record ProbeCredentialFile(
    OfficialPasswordCredentials Credentials,
    string? RegionName)
{
    public static ProbeCredentialFile Load(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length is <= 0 or > 65536)
        {
            throw new ProbeConfigurationException("The local credentials file is absent or too large.");
        }
        string? email = null;
        string? password = null;
        string? region = null;
        foreach (string line in File.ReadLines(path))
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }
            int separator = trimmed.IndexOf('=');
            if (separator < 1)
            {
                throw new ProbeConfigurationException("The local credentials file has an invalid entry.");
            }
            string key = trimmed[..separator].Trim();
            string value = trimmed[(separator + 1)..].Trim();
            if (value.Length >= 2 && value[0] == value[^1] && value[0] is '\'' or '"')
            {
                value = value[1..^1];
            }
            switch (key)
            {
                case "STARLIGHT_EXPORTER_OFFICIAL_EMAIL":
                    if (email is not null) throw Duplicate();
                    email = value;
                    break;
                case "STARLIGHT_EXPORTER_OFFICIAL_PASSWORD":
                    if (password is not null) throw Duplicate();
                    password = value;
                    break;
                case "STARLIGHT_EXPORTER_OFFICIAL_REGION":
                    if (region is not null) throw Duplicate();
                    region = value;
                    break;
            }
        }
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            throw new ProbeConfigurationException("The local credentials file lacks account fields.");
        }
        return new ProbeCredentialFile(new OfficialPasswordCredentials(email, password), region);
    }

    public override string ToString() => "ProbeCredentialFile { Secrets = [REDACTED] }";

    private static ProbeConfigurationException Duplicate() =>
        new("The local credentials file has a duplicate known key.");
}
