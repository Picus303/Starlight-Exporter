using System.Security.Cryptography;

namespace StarlightExporter.Official;

// Public server key recovered from the Windows Global 7.0.0 client. The key
// with the same numeric ID in Starlight.Crypto.Client is the client private
// decryption key and has a different modulus.
public static class OfficialRsaKeyProfile
{
    public const string OsGlobalV70GatePublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEA15RBm/vARY0axYksImhs
        Ticpv09OYfS4+wCvmE7psOvZhW2URZ2Rlf5DsEtuRG/7v5W/2obqqVkf+1dorPcR
        2iqrYZ4VVPf7KU3Cgqh0kzLGxWOpGxzwJULEyFVaiMDWbk7gr8rik/jYyhLiLc52
        zz3E3whTUPleKhOhXnxx1iOKY+TPVI8jJfDNiQoh0UvgjnkigJ/saPzjogeig/4M
        cBc4l5cDkvttkKQKq7oXe9OCBClgKlYjcc1CNalwMlTz7NvLEko+ZLTgpA+kElZu
        myBXT67mmW7t7IDXorscAI7auwusKWmq797alFkQ/6sUqs8KKGnqQ2fwHfa/RYDh
        EwIDAQAB
        -----END PUBLIC KEY-----
        """;

    public const string OsGlobalV70GatePublicKeySpkiSha256 =
        "1ffa3ca5f7911d17a095d91e08c0094bc4fddac9c572f9ffb99150e34df4da54";

    public static RSA ImportGatePublicKey(string pem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pem);
        RSA key = RSA.Create();
        try
        {
            key.ImportFromPem(pem);
            if (key.KeySize < 2048)
            {
                throw new ArgumentException("The Gate server key must be at least 2048 bits.", nameof(pem));
            }
            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }
}
