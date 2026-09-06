namespace StarlightExporter.Official;

public interface IOfficialDispatchClient
{
    Task<OfficialRegionList> GetRegionsAsync(
        OfficialClientProfile profile,
        CancellationToken cancellationToken = default);

    Task<OfficialCurrentRegion> ResolveRegionAsync(
        OfficialClientProfile profile,
        string regionName,
        CancellationToken cancellationToken = default);
}

public interface IOfficialRegionCrypto
{
    byte[] DecryptAndVerify(byte[] ciphertext, string signatureBase64, uint keyId);
}
