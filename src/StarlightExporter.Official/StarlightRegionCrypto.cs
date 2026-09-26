using System.Security.Cryptography;
using System.Text;
using Starlight.Crypto.Client;

namespace StarlightExporter.Official;

public sealed class StarlightRegionCrypto : IOfficialRegionCrypto, IDisposable
{
    private readonly ClientCrypto _crypto;
    private readonly RSA _gatePublicKey;
    private bool _disposed;

    private StarlightRegionCrypto(ClientCrypto crypto, RSA gatePublicKey)
    {
        _crypto = crypto;
        _gatePublicKey = gatePublicKey;
    }

    public static StarlightRegionCrypto CreatePinned() =>
        CreatePinnedWithVerificationKey(OfficialRsaKeyProfile.OsGlobalV70GatePublicKeyPem);

    public static StarlightRegionCrypto CreatePinnedWithVerificationKey(string publicKeyPem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publicKeyPem);
        RSA verificationKey = OfficialRsaKeyProfile.ImportGatePublicKey(publicKeyPem);
        try
        {
            return new StarlightRegionCrypto(
                ClientCrypto.Create(generateRsaKeys: false),
                verificationKey);
        }
        catch
        {
            verificationKey.Dispose();
            throw;
        }
    }

    public byte[] DecryptAndVerify(byte[] ciphertext, string signatureBase64, uint keyId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(ciphertext);
        ArgumentException.ThrowIfNullOrWhiteSpace(signatureBase64);

        if (keyId > int.MaxValue
            || !_crypto.ContentKeys.TryGetValue((int)keyId, out RSA? contentKey))
        {
            throw Failure(
                OfficialConnectivityError.RegionCryptoKeyMismatch,
                $"No pinned content key is available for key id {keyId}.");
        }

        int blockSize = contentKey.KeySize / 8;
        if (ciphertext.Length == 0 || ciphertext.Length % blockSize != 0)
        {
            throw Failure(
                OfficialConnectivityError.RegionCryptoKeyMismatch,
                "The encrypted regional payload has an invalid RSA block length.");
        }

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(signatureBase64);
        }
        catch (FormatException exception)
        {
            throw Failure(
                OfficialConnectivityError.RegionSignatureMismatch,
                "The regional signature is not valid base64.",
                exception);
        }

        RSA signingKey = _gatePublicKey;
        if (signature.Length != signingKey.KeySize / 8)
        {
            throw Failure(
                OfficialConnectivityError.RegionSignatureMismatch,
                "The regional signature has an invalid length.");
        }

        using var plaintext = new MemoryStream();
        for (int offset = 0; offset < ciphertext.Length; offset += blockSize)
        {
            byte[] block = ciphertext.AsSpan(offset, blockSize).ToArray();
            if (!_crypto.TryDecryptContent(checked((int)keyId), block, out byte[] decrypted))
            {
                throw Failure(
                    OfficialConnectivityError.RegionCryptoKeyMismatch,
                    "The regional payload could not be decrypted with the selected content key.");
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
            if (signingKey.VerifyData(
                ciphertext,
                signature,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1))
            {
                CryptographicOperations.ZeroMemory(result);
                throw Failure(
                    OfficialConnectivityError.RegionSignatureContractMismatch,
                    "The regional signature covers ciphertext instead of the expected plaintext.");
            }

            byte[] encodedCiphertext = Encoding.ASCII.GetBytes(Convert.ToBase64String(ciphertext));
            try
            {
                if (signingKey.VerifyData(
                    encodedCiphertext,
                    signature,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1))
                {
                    CryptographicOperations.ZeroMemory(result);
                    throw Failure(
                        OfficialConnectivityError.RegionSignatureContractMismatch,
                        "The regional signature covers encoded ciphertext instead of the expected plaintext.");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encodedCiphertext);
            }

            CryptographicOperations.ZeroMemory(result);
            throw Failure(
                OfficialConnectivityError.RegionSignatureMismatch,
                "The regional payload signature is invalid.");
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
        _gatePublicKey.Dispose();
        _disposed = true;
    }

    private static OfficialConnectivityException Failure(
        OfficialConnectivityError error,
        string message,
        Exception? innerException = null) =>
        new(error, message, innerException);
}
