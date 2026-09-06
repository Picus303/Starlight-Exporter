namespace StarlightExporter.Official;

public enum OfficialConnectivityError
{
    GlobalDispatchUnavailable,
    RegionNotFound,
    RegionResponseInvalid,
    RegionCryptoUnsupported,
    RegionCryptoKeyMismatch,
    RegionSignatureMismatch,
    RegionSignatureContractMismatch,
    ClientVersionRejected,
    ReplayInvalid,
    SyncIncomplete,
    CapturedDataInvalid,
    GateHandshakeInvalid,
    GateTransportFailed,
    GatePacketInvalid,
    GateCryptoInvalid,
    PlayerTokenRejected,
    PlayerLoginRejected,
    SessionRekeyFailed,
    ComboConfigurationMissing,
    ComboExchangeRejected,
}

public sealed class OfficialConnectivityException : Exception
{
    public OfficialConnectivityException(
        OfficialConnectivityError error,
        string message,
        Exception? innerException = null)
        : base(message)
    {
        Error = error;
        CauseType = innerException?.GetType().Name;
    }

    public OfficialConnectivityError Error { get; }
    public string? CauseType { get; }
}

public static class OfficialConnectivityDiagnostic
{
    public static string Code(OfficialConnectivityError error)
    {
        string name = error.ToString();
        var result = new System.Text.StringBuilder(name.Length + 8);
        for (int index = 0; index < name.Length; index++)
        {
            char current = name[index];
            if (index > 0 && char.IsUpper(current) && char.IsLower(name[index - 1]))
            {
                result.Append('_');
            }

            result.Append(char.ToUpperInvariant(current));
        }

        return result.ToString();
    }

    public static string SafeMessage(OfficialConnectivityError error) => error switch
    {
        OfficialConnectivityError.GlobalDispatchUnavailable => "The global dispatch request failed.",
        OfficialConnectivityError.RegionNotFound => "The requested official region was not found.",
        OfficialConnectivityError.RegionResponseInvalid => "The regional dispatch response was invalid.",
        OfficialConnectivityError.RegionCryptoUnsupported => "The regional dispatch crypto contract is unsupported.",
        OfficialConnectivityError.RegionCryptoKeyMismatch => "The regional dispatch key did not match.",
        OfficialConnectivityError.RegionSignatureMismatch => "The regional dispatch signature did not match.",
        OfficialConnectivityError.RegionSignatureContractMismatch => "The regional signature contract is unresolved.",
        OfficialConnectivityError.ClientVersionRejected => "The official service rejected the client profile.",
        OfficialConnectivityError.ReplayInvalid => "The sanitized replay was invalid.",
        OfficialConnectivityError.SyncIncomplete => "The player synchronization was incomplete.",
        OfficialConnectivityError.CapturedDataInvalid => "The captured player data was invalid.",
        OfficialConnectivityError.GateHandshakeInvalid => "The Gate handshake failed.",
        OfficialConnectivityError.GateTransportFailed => "The Gate transport failed.",
        OfficialConnectivityError.GatePacketInvalid => "A Gate packet was invalid.",
        OfficialConnectivityError.GateCryptoInvalid => "The Gate crypto state was invalid.",
        OfficialConnectivityError.PlayerTokenRejected => "The Gate rejected the player token exchange.",
        OfficialConnectivityError.PlayerLoginRejected => "The Gate rejected player login.",
        OfficialConnectivityError.SessionRekeyFailed => "The Gate session rekey failed.",
        OfficialConnectivityError.ComboConfigurationMissing => "The Combo configuration is incomplete.",
        OfficialConnectivityError.ComboExchangeRejected => "The Combo exchange failed.",
        _ => "The official connectivity operation failed.",
    };
}
