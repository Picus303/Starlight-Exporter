namespace StarlightExporter.Official;

public enum OfficialConnectivityError
{
    GlobalDispatchUnavailable,
    RegionNotFound,
    RegionResponseInvalid,
    RegionCryptoUnsupported,
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
}

public sealed class OfficialConnectivityException : Exception
{
    public OfficialConnectivityException(
        OfficialConnectivityError error,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Error = error;
    }

    public OfficialConnectivityError Error { get; }
}
