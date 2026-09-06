using Google.Protobuf;
using Starlight.Kcp;
using Starlight.Protobuf.Core;
using Starlight.Protobuf.Registry;
using Starlight.Protocol;
using Starlight.Protocol.V70;
using IMessage = Starlight.Protobuf.Core.IMessage;

namespace StarlightExporter.Official;

public sealed class OfficialGateConnection(uint conversationId, uint token)
{
    public uint ConversationId { get; } = conversationId;
    public uint Token { get; } = token;

    public override string ToString() =>
        $"OfficialGateConnection {{ ConversationId = {ConversationId}, Token = [REDACTED] }}";
}

public static class OfficialGateHandshake
{
    public static byte[] CreateConnect() => new ConnectHandshake().ToByteArray();

    public static OfficialGateConnection ParseExchange(ReadOnlySpan<byte> payload)
    {
        if (Handshake.Parse(payload) is not ExchangeHandshake exchange
            || exchange.ConvId == 0
            || exchange.Token == 0)
        {
            throw new OfficialConnectivityException(
                OfficialConnectivityError.GateHandshakeInvalid,
                "The Gate exchange handshake is invalid.");
        }

        return new OfficialGateConnection(exchange.ConvId, exchange.Token);
    }

    public static byte[] CreateDisconnect(
        OfficialGateConnection connection,
        DisconnectReason reason = DisconnectReason.ClientClose)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return new DisconnectHandshake(connection.ConversationId, connection.Token, (uint)reason)
            .ToByteArray();
    }
}

public sealed class OfficialGatePacket
{
    internal OfficialGatePacket(ushort commandId, PacketHead metadata, IMessage message, int bodyLength)
    {
        CommandId = commandId;
        Metadata = metadata;
        Message = message;
        BodyLength = bodyLength;
    }

    public ushort CommandId { get; }
    public PacketHead Metadata { get; }
    public IMessage Message { get; }
    public int BodyLength { get; }

    public override string ToString() =>
        $"OfficialGatePacket {{ Type = {Message.GetType().Name}, CommandId = {CommandId}, BodyLength = {BodyLength} }}";
}

public sealed record OfficialGatePacketMetadata(
    ushort CommandId,
    string MessageType,
    int SerializedBodyBytes);

public sealed class OfficialGatePacketCodec
{
    public const int MaximumPacketBytes = 1024 * 1024;

    private readonly ProtocolRegistry _registry = new V70ProtocolRegistry();

    public string ProtocolVersion => _registry.Version;

    public OfficialGatePacketMetadata Describe(IMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        int commandId = _registry.GetCmdId(message);
        if (commandId is <= 0 or > ushort.MaxValue)
        {
            throw Failure("The V70 message has an invalid command ID.");
        }

        byte[] body = OfficialV70FieldAliases.Serialize(_registry, message);
        try
        {
            return new OfficialGatePacketMetadata(
                checked((ushort)commandId),
                message.GetType().Name,
                body.Length);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(body);
        }
    }

    public byte[] EncodeEncrypted(
        IMessage message,
        OfficialGateCipherState cipher,
        PacketHead? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(cipher);

        int commandId = _registry.GetCmdId(message);
        if (commandId is <= 0 or > ushort.MaxValue)
        {
            throw Failure("The V70 message has an invalid command ID.");
        }

        byte[] rawMetadata = (metadata ?? new PacketHead()).ToByteArray();
        byte[] body = OfficialV70FieldAliases.Serialize(_registry, message);
        if (rawMetadata.Length > ushort.MaxValue
            || body.Length > MaximumPacketBytes
            || 12L + rawMetadata.Length + body.Length > MaximumPacketBytes)
        {
            throw Failure("The encoded Gate packet exceeds its size limit.");
        }

        byte[] plaintext = new GamePacket((ushort)commandId, rawMetadata, body).ToBytes();
        try
        {
            return cipher.Transform(plaintext);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public OfficialGatePacket DecodeEncrypted(
        ReadOnlySpan<byte> encrypted,
        OfficialGateCipherState cipher)
    {
        ArgumentNullException.ThrowIfNull(cipher);
        if (encrypted.Length is < 12 or > MaximumPacketBytes)
        {
            throw Failure("The encrypted Gate packet has an invalid size.");
        }

        byte[] plaintext = cipher.Transform(encrypted);
        try
        {
            GamePacket packet = new(plaintext);
            IMessage message;
            try
            {
                using var input = new CodedInputStream(packet.Body);
                message = _registry.Deserialize(packet.CmdId, input);
                OfficialV70FieldAliases.Apply(packet.Body, message);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw Failure("The Gate packet body is not a valid V70 message.", exception);
            }

            return new OfficialGatePacket(packet.CmdId, packet.Metadata.Value, message, packet.Body.Length);
        }
        catch (PacketParseException exception)
        {
            throw Failure("The Gate packet framing is invalid.", exception);
        }
        catch (OfficialConnectivityException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw Failure("The Gate packet metadata is invalid.", exception);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static OfficialConnectivityException Failure(string message, Exception? innerException = null) =>
        new(OfficialConnectivityError.GatePacketInvalid, message, innerException);
}

internal static class OfficialV70FieldAliases
{
    // These field numbers come from the pinned V70 schema. The versioned names do not
    // correlate with the canonical Starlight names, although the wire types do.
    private const int GetPlayerTokenTicket = 986;
    private const int PlayerLoginClientVersionHash = 1747;
    private const int PlayerLoginUserAgent = 1174;

    public static byte[] Serialize(ProtocolRegistry registry, IMessage message)
    {
        byte[] body = registry.Serialize(message);
        using var stream = new MemoryStream(body.Length + 256);
        stream.Write(body);
        using var output = new CodedOutputStream(stream, leaveOpen: true);

        switch (message)
        {
            case GetPlayerTokenReq token when !string.IsNullOrEmpty(token.Ticket):
                WriteString(output, GetPlayerTokenTicket, token.Ticket);
                break;

            case PlayerLoginReq login:
                if (!string.IsNullOrEmpty(login.ClientVersionHash))
                {
                    WriteString(output, PlayerLoginClientVersionHash, login.ClientVersionHash);
                }
                if (!string.IsNullOrEmpty(login.UaPc))
                {
                    WriteString(output, PlayerLoginUserAgent, login.UaPc);
                }
                break;
        }

        output.Flush();
        return stream.ToArray();
    }

    public static void Apply(byte[] body, IMessage message)
    {
        using var input = new CodedInputStream(body);
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            int fieldNumber = WireFormat.GetTagFieldNumber(tag);
            if (message is GetPlayerTokenReq token && fieldNumber == GetPlayerTokenTicket)
            {
                token.Ticket = input.ReadString();
            }
            else if (message is PlayerLoginReq login
                && fieldNumber == PlayerLoginClientVersionHash)
            {
                login.ClientVersionHash = input.ReadString();
            }
            else if (message is PlayerLoginReq loginWithUserAgent
                && fieldNumber == PlayerLoginUserAgent)
            {
                loginWithUserAgent.UaPc = input.ReadString();
            }
            else
            {
                input.SkipLastField();
            }
        }
    }

    private static void WriteString(CodedOutputStream output, int fieldNumber, string value)
    {
        output.WriteTag(fieldNumber, WireFormat.WireType.LengthDelimited);
        output.WriteString(value);
    }
}
