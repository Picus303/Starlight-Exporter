using Starlight.Crypto.Client;
using System.Security.Cryptography;

namespace StarlightExporter.Official;

public sealed class StarlightRegionCrypto : IOfficialRegionCrypto, IDisposable
{
    private readonly ClientCrypto _crypto;
    private bool _disposed;

    private StarlightRegionCrypto(ClientCrypto crypto)
    {
        _crypto = crypto;
    }

    public static StarlightRegionCrypto CreatePinned() =>
        new(ClientCrypto.Create(generateRsaKeys: false));

    public byte[] DecryptAndVerify(byte[] ciphertext, string signatureBase64, uint keyId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(ciphertext);
        ArgumentException.ThrowIfNullOrWhiteSpace(signatureBase64);

        if (keyId > int.MaxValue
            || !_crypto.ContentKeys.TryGetValue((int)keyId, out RSA? contentKey))
        {
            throw Failure($"No pinned content key is available for key id {keyId}.");
        }

        int blockSize = contentKey.KeySize / 8;
        if (ciphertext.Length == 0 || ciphertext.Length % blockSize != 0)
        {
            throw Failure("The encrypted regional payload has an invalid RSA block length.");
        }

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(signatureBase64);
        }
        catch (FormatException exception)
        {
            throw Failure("The regional signature is not valid base64.", exception);
        }

        RSA? signingKey = _crypto.SigningKey;
        if (signingKey is null || signature.Length != signingKey.KeySize / 8)
        {
            throw Failure("The regional signature has an invalid length.");
        }

        using var plaintext = new MemoryStream();
        for (int offset = 0; offset < ciphertext.Length; offset += blockSize)
        {
            byte[] block = ciphertext.AsSpan(offset, blockSize).ToArray();
            if (!_crypto.TryDecryptContent(checked((int)keyId), block, out byte[] decrypted))
            {
                throw Failure("The regional payload could not be decrypted with the selected content key.");
            }

            try
            {
                plaintext.Write(decrypted);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(decrypted);
            }
        }

        byte[] result = plaintext.ToArray();
        if (plaintext.TryGetBuffer(out ArraySegment<byte> buffer))
        {
            CryptographicOperations.ZeroMemory(buffer.AsSpan(0, checked((int)plaintext.Length)));
        }

        if (!signingKey.VerifyData(result, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
        {
            CryptographicOperations.ZeroMemory(result);
            throw Failure("The regional payload signature is invalid.");
        }

        return result;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _crypto.Dispose();
        _disposed = true;
    }

    private static OfficialConnectivityException Failure(string message, Exception? innerException = null) =>
        new(OfficialConnectivityError.RegionCryptoUnsupported, message, innerException);
}
