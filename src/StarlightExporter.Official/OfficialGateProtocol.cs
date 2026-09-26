using System.Security.Cryptography;
using System.Text;
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
    internal OfficialGatePacket(
        ushort commandId,
        PacketHead metadata,
        IMessage message,
        int bodyLength,
        bool playerLoginLimitedSocialCache,
        string fieldPresenceHash)
    {
        CommandId = commandId;
        Metadata = metadata;
        Message = message;
        BodyLength = bodyLength;
        PlayerLoginLimitedSocialCache = playerLoginLimitedSocialCache;
        FieldPresenceHash = fieldPresenceHash;
    }

    public ushort CommandId { get; }
    public PacketHead Metadata { get; }
    public IMessage Message { get; }
    public int BodyLength { get; }
    public bool PlayerLoginLimitedSocialCache { get; }
    public string FieldPresenceHash { get; }

    public override string ToString() =>
        $"OfficialGatePacket {{ Type = {Message.GetType().Name}, CommandId = {CommandId}, BodyLength = {BodyLength} }}";
}

public sealed record OfficialGatePacketMetadata(
    ushort CommandId,
    string MessageType,
    int SerializedBodyBytes,
    string FieldPresenceHash = "0000000000000000");

public sealed class OfficialGatePacketCodec
{
    public const int MaximumPacketBytes = 1024 * 1024;

    private readonly ProtocolRegistry _registry = new V70ProtocolRegistry();

    public string ProtocolVersion => _registry.Version;

    public OfficialGatePacketMetadata Describe(
        IMessage message,
        string? clientVersion = null,
        string? deviceFingerprint = null,
        uint? loginTimestamp = null,
        int? screenWidth = null,
        int? screenHeight = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        int commandId = _registry.GetCmdId(message);
        if (commandId is <= 0 or > ushort.MaxValue)
        {
            throw Failure("The V70 message has an invalid command ID.");
        }

        byte[] body = OfficialV70FieldAliases.Serialize(
            _registry, message, clientVersion, deviceFingerprint, loginTimestamp,
            screenWidth, screenHeight);
        try
        {
            return new OfficialGatePacketMetadata(
                checked((ushort)commandId),
                message.GetType().Name,
                body.Length,
                PresenceHash(body));
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(body);
        }
    }

    public byte[] EncodeEncrypted(
        IMessage message,
        OfficialGateCipherState cipher,
        PacketHead? metadata = null,
        string? clientVersion = null,
        string? deviceFingerprint = null,
        uint? loginTimestamp = null,
        int? screenWidth = null,
        int? screenHeight = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(cipher);

        int commandId = _registry.GetCmdId(message);
        if (commandId is <= 0 or > ushort.MaxValue)
        {
            throw Failure("The V70 message has an invalid command ID.");
        }

        byte[] rawMetadata = (metadata ?? new PacketHead()).ToByteArray();
        byte[] body = OfficialV70FieldAliases.Serialize(
            _registry, message, clientVersion, deviceFingerprint, loginTimestamp,
            screenWidth, screenHeight);
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

            bool limitedSocialCache = message is PlayerLoginRsp
                && OfficialV70FieldAliases.ReadPlayerLoginLimitedSocialCache(packet.Body);
            return new OfficialGatePacket(
                packet.CmdId, packet.Metadata.Value, message, packet.Body.Length, limitedSocialCache,
                PresenceHash(packet.Body));
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

    internal static string PresenceHash(byte[] body)
    {
        var fields = new List<int>();
        using (var input = new CodedInputStream(body))
        {
            uint tag;
            while ((tag = input.ReadTag()) != 0)
            {
                fields.Add(WireFormat.GetTagFieldNumber(tag));
                input.SkipLastField();
            }
        }
        string canonical = string.Join(',', fields.Order());
        byte[] hash = SHA256.HashData(Encoding.ASCII.GetBytes(canonical));
        return Convert.ToHexStringLower(hash.AsSpan(0, 8));
    }
}

internal static class OfficialV70FieldAliases
{
    // These field numbers come from the pinned V70 schema. The versioned names do not
    // correlate with the canonical Starlight names, although the wire types do.
    private const int GetPlayerTokenTicket = 986;
    private const int GetPlayerTokenVersion = 1397;
    private const int GetPlayerTokenAuthAppId = 116;
    private const int GetPlayerTokenSignType = 476;
    private const int GetPlayerTokenAuthkeyVer = 578;
    private const int PlayerLoginClientVersionHash = 1747;
    private const int PlayerLoginUserAgent = 1174;
    private const int PlayerLoginDeviceFingerprint = 787;
    private const int PlayerLoginTimestamp = 383;
    private const int PlayerLoginScreenSize = 137;
    private const int PlayerLoginLimitedSocialCache = 592;

    public static bool ReadPlayerLoginLimitedSocialCache(byte[] body)
    {
        using var input = new CodedInputStream(body);
        bool value = false;
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            if (WireFormat.GetTagFieldNumber(tag) == PlayerLoginLimitedSocialCache)
            {
                if (WireFormat.GetTagWireType(tag) != WireFormat.WireType.Varint)
                {
                    throw new InvalidDataException("PlayerLoginRsp tag 592 has an invalid wire type.");
                }
                value = input.ReadBool();
            }
            else
            {
                input.SkipLastField();
            }
        }
        return value;
    }

    public static byte[] Serialize(
        ProtocolRegistry registry,
        IMessage message,
        string? clientVersion = null,
        string? deviceFingerprint = null,
        uint? loginTimestamp = null,
        int? screenWidth = null,
        int? screenHeight = null)
    {
        byte[] body = registry.Serialize(message);
        using var stream = new MemoryStream(body.Length + 256);
        stream.Write(body);
        using var output = new CodedOutputStream(stream, leaveOpen: true);

        switch (message)
        {
            case GetPlayerTokenReq token:
                if (!string.IsNullOrEmpty(token.Ticket))
                {
                    WriteString(output, GetPlayerTokenTicket, token.Ticket);
                }
                if (!string.IsNullOrWhiteSpace(clientVersion))
                {
                    WriteString(output, GetPlayerTokenVersion, clientVersion);
                }
                WriteString(output, GetPlayerTokenAuthAppId, "csc");
                WriteUInt32(output, GetPlayerTokenSignType, 2);
                WriteUInt32(output, GetPlayerTokenAuthkeyVer, 1);
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
                if (!string.IsNullOrEmpty(deviceFingerprint))
                {
                    WriteString(output, PlayerLoginDeviceFingerprint, deviceFingerprint);
                }
                if (loginTimestamp is { } timestamp)
                {
                    WriteUInt32(output, PlayerLoginTimestamp, timestamp);
                }
                if (screenWidth is > 0 && screenHeight is > 0)
                {
                    WriteVector2Int(output, PlayerLoginScreenSize, screenWidth.Value, screenHeight.Value);
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

    private static void WriteUInt32(CodedOutputStream output, int fieldNumber, uint value)
    {
        output.WriteTag(fieldNumber, WireFormat.WireType.Varint);
        output.WriteUInt32(value);
    }

    private static void WriteVector2Int(
        CodedOutputStream output,
        int fieldNumber,
        int width,
        int height)
    {
        using var stream = new MemoryStream();
        using (var nested = new CodedOutputStream(stream, leaveOpen: true))
        {
            nested.WriteTag(1, WireFormat.WireType.Varint);
            nested.WriteInt32(width);
            nested.WriteTag(2, WireFormat.WireType.Varint);
            nested.WriteInt32(height);
            nested.Flush();
        }
        output.WriteTag(fieldNumber, WireFormat.WireType.LengthDelimited);
        output.WriteBytes(ByteString.CopyFrom(stream.ToArray()));
    }
}
