using Starlight.Ec2b;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace StarlightExporter.Official;

public static class OfficialGateKeySchedule
{
    public const int PadLength = 4096;

    public static byte[] DeriveInitialPad(OfficialCurrentRegion region)
    {
        ArgumentNullException.ThrowIfNull(region);
        if (!Ec2bKeyGen.HasValidLayout(region.SecretKey))
        {
            throw Failure("The regional Gate secret is not a valid EC2B buffer.");
        }

        byte[] pad = Ec2bHelper.Derive(region.SecretKey);
        if (pad.Length != PadLength)
        {
            CryptographicOperations.ZeroMemory(pad);
            throw Failure("The initial Gate pad has an invalid length.");
        }

        return pad;
    }

    public static byte[] GenerateSessionPad(ulong serverSeed)
    {
        Span<byte> pad = stackalloc byte[PadLength];
        var random = new Mt19937_64(serverSeed);
        random.Init(random.NextULong());
        random.NextULong();

        for (int index = 0; index < pad.Length; index += sizeof(ulong))
        {
            ulong value = BinaryPrimitives.ReverseEndianness(random.NextULong());
            MemoryMarshal.Write(pad[index..], in value);
        }

        return pad.ToArray();
    }

    internal static void XorInPlace(Span<byte> data, ReadOnlySpan<byte> pad)
    {
        if (pad.Length != PadLength)
        {
            throw Failure("The Gate XOR pad has an invalid length.");
        }

        for (int index = 0; index < data.Length; index++)
        {
            data[index] ^= pad[index % pad.Length];
        }
    }

    private static OfficialConnectivityException Failure(string message) =>
        new(OfficialConnectivityError.GateCryptoInvalid, message);
}

public sealed class OfficialGateCipherState : IDisposable
{
    private readonly Lock _gate = new();
    private byte[] _activePad;
    private bool _disposed;

    private OfficialGateCipherState(byte[] initialPad)
    {
        _activePad = initialPad;
    }

    public static OfficialGateCipherState FromRegion(OfficialCurrentRegion region) =>
        new(OfficialGateKeySchedule.DeriveInitialPad(region));

    public byte[] Transform(ReadOnlySpan<byte> data)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            byte[] output = data.ToArray();
            OfficialGateKeySchedule.XorInPlace(output, _activePad);
            return output;
        }
    }

    public void ActivateSessionPadAfterTokenResponse(byte[] sessionPad)
    {
        ArgumentNullException.ThrowIfNull(sessionPad);
        if (sessionPad.Length != OfficialGateKeySchedule.PadLength)
        {
            throw new OfficialConnectivityException(
                OfficialConnectivityError.SessionRekeyFailed,
                "The negotiated Gate session pad has an invalid length.");
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            byte[] previous = _activePad;
            _activePad = sessionPad.ToArray();
            CryptographicOperations.ZeroMemory(previous);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            CryptographicOperations.ZeroMemory(_activePad);
            _activePad = [];
            _disposed = true;
        }
    }
}
