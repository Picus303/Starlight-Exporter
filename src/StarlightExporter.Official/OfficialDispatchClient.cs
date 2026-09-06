using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Starlight.Ec2b;
using Starlight.Protobuf.Core;
using Starlight.Protocol;

namespace StarlightExporter.Official;

public sealed class OfficialDispatchClient : IOfficialDispatchClient
{
    private const int MaximumResponseBytes = 2 * 1024 * 1024;
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(15);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly HttpClient _httpClient;
    private readonly IOfficialRegionCrypto _crypto;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _requestTimeout;

    public OfficialDispatchClient(
        HttpClient httpClient,
        IOfficialRegionCrypto crypto,
        TimeProvider? timeProvider = null,
        TimeSpan? requestTimeout = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _crypto = crypto ?? throw new ArgumentNullException(nameof(crypto));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _requestTimeout = requestTimeout ?? DefaultRequestTimeout;
        if (_requestTimeout <= TimeSpan.Zero || _requestTimeout > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestTimeout),
                "The dispatch request timeout must be between zero and two minutes.");
        }
    }

    public async Task<OfficialRegionList> GetRegionsAsync(
        OfficialClientProfile profile,
        CancellationToken cancellationToken = default)
    {
        ValidateProfile(profile);
        Uri uri = AppendQuery(
            profile.GlobalDispatchUri,
            profile.DispatchParameters(_timeProvider, regional: false));
        string content = await GetStringAsync(
            uri,
            OfficialConnectivityError.GlobalDispatchUnavailable,
            cancellationToken);

        QueryRegionListHttpRsp response = ParseBase64<QueryRegionListHttpRsp>(
            content,
            static (message, bytes) => message.MergeFrom(bytes),
            OfficialConnectivityError.RegionResponseInvalid,
            "The global dispatch response is invalid.");

        if (response.Retcode != 0)
        {
            throw new OfficialConnectivityException(
                OfficialConnectivityError.ClientVersionRejected,
                $"Global dispatch rejected the client profile with retcode {response.Retcode}.");
        }

        var regions = new List<OfficialRegion>(response.RegionList.Count);
        foreach (RegionSimpleInfo region in response.RegionList)
        {
            if (string.IsNullOrWhiteSpace(region.Name)
                || !Uri.TryCreate(region.DispatchUrl, UriKind.Absolute, out Uri? dispatchUri)
                || dispatchUri.Scheme != Uri.UriSchemeHttps)
            {
                throw new OfficialConnectivityException(
                    OfficialConnectivityError.RegionResponseInvalid,
                    "Global dispatch returned an invalid region entry.");
            }

            regions.Add(new OfficialRegion(region.Name, region.Title, region.Type, dispatchUri));
        }

        if (regions.Count == 0)
        {
            throw new OfficialConnectivityException(
                OfficialConnectivityError.RegionResponseInvalid,
                "Global dispatch returned no regions.");
        }

        byte[] clientSecretKey = response.ClientSecretKey.ToByteArray();
        ValidateEc2b(clientSecretKey, "Global dispatch returned an invalid client secret key.");

        return new OfficialRegionList(
            regions,
            clientSecretKey,
            response.ClientCustomConfigEncrypted.ToByteArray(),
            response.EnableLoginPc);
    }

    public async Task<OfficialCurrentRegion> ResolveRegionAsync(
        OfficialClientProfile profile,
        string regionName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(regionName);

        OfficialRegionList list = await GetRegionsAsync(profile, cancellationToken);
        OfficialRegion? selected = list.Regions.FirstOrDefault(region =>
            string.Equals(region.Name, regionName, StringComparison.Ordinal));
        if (selected is null)
        {
            throw new OfficialConnectivityException(
                OfficialConnectivityError.RegionNotFound,
                $"Region '{regionName}' was not returned by global dispatch.");
        }

        Uri uri = AppendQuery(
            selected.DispatchUri,
            profile.DispatchParameters(_timeProvider, regional: true));
        string content = await GetStringAsync(
            uri,
            OfficialConnectivityError.GlobalDispatchUnavailable,
            cancellationToken);

        (byte[] payload, OfficialRegionalPayloadFormat payloadFormat) =
            DecodeRegionalPayload(content, profile.KeyId);
        QueryCurrRegionHttpRsp response;
        try
        {
            response = new QueryCurrRegionHttpRsp();
            response.MergeFrom(payload);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new OfficialConnectivityException(
                OfficialConnectivityError.RegionResponseInvalid,
                "The regional dispatch protobuf is invalid.",
                exception);
        }

        if (response.Retcode != 0)
        {
            throw new OfficialConnectivityException(
                OfficialConnectivityError.ClientVersionRejected,
                $"Regional dispatch rejected the client profile with retcode {response.Retcode}.");
        }

        RegionInfo? region = response.RegionInfo;
        if (region is null
            || region.GateserverPort is 0 or > ushort.MaxValue
            || (string.IsNullOrWhiteSpace(region.GateserverIp)
                && string.IsNullOrWhiteSpace(region.GateserverDomainName)))
        {
            throw new OfficialConnectivityException(
                OfficialConnectivityError.RegionResponseInvalid,
                "Regional dispatch did not return a usable Gate endpoint.");
        }


        byte[] clientSecretKey = response.ClientSecretKey.ToByteArray();
        byte[] secretKey = region.SecretKey.ToByteArray();
        if (!Ec2bKeyGen.HasValidLayout(clientSecretKey)
            || !Ec2bKeyGen.HasValidLayout(secretKey))
        {
            throw new OfficialConnectivityException(
                OfficialConnectivityError.RegionResponseInvalid,
                "Regional dispatch returned an invalid EC2B secret key.");
        }

        return new OfficialCurrentRegion
        {
            RegionName = selected.Name,
            GateServerIp = region.GateserverIp,
            GateServerPort = region.GateserverPort,
            UseGateServerDomainName = region.UseGateserverDomainName,
            GateServerDomainName = region.GateserverDomainName,
            ClientSecretKey = clientSecretKey,
            SecretKey = secretKey,
            ConnectGateTicket = OfficialSecret.Create(response.ConnectGateTicket),
            ClientDataVersion = region.ClientDataVersion,
            ClientSilenceDataVersion = region.ClientSilenceDataVersion,
            ClientDataMd5 = region.ClientDataMd5,
            ClientSilenceDataMd5 = region.ClientSilenceDataMd5,
            ClientVersionSuffix = region.ClientVersionSuffix,
            ClientSilenceVersionSuffix = region.ClientSilenceVersionSuffix,
            GameBiz = region.GameBiz,
            ResourceUrl = region.ResourceUrl,
            DataUrl = region.DataUrl,
            PayloadFormat = payloadFormat,
        };
    }

    private async Task<string> GetStringAsync(
        Uri uri,
        OfficialConnectivityError error,
        CancellationToken cancellationToken)
    {
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestCancellation.CancelAfter(_requestTimeout);

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.GetAsync(
                uri,
                HttpCompletionOption.ResponseHeadersRead,
                requestCancellation.Token);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new OfficialConnectivityException(error, "The dispatch request timed out.", exception);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new OfficialConnectivityException(error, "The dispatch endpoint could not be reached.", exception);
        }

        using (response)
        {
            if (response.StatusCode != HttpStatusCode.OK)
            {
                throw new OfficialConnectivityException(
                    error,
                    $"The dispatch endpoint returned HTTP {(int)response.StatusCode}.");
            }

            if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
            {
                throw new OfficialConnectivityException(error, "The dispatch response exceeds the size limit.");
            }

            string content;
            try
            {
                content = await response.Content.ReadAsStringAsync(requestCancellation.Token);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new OfficialConnectivityException(error, "The dispatch request timed out.", exception);
            }
            if (Encoding.UTF8.GetByteCount(content) > MaximumResponseBytes)
            {
                throw new OfficialConnectivityException(error, "The dispatch response exceeds the size limit.");
            }

            return content.Trim();
        }
    }

    private (byte[] Payload, OfficialRegionalPayloadFormat Format) DecodeRegionalPayload(
        string content,
        uint keyId)
    {
        if (!content.StartsWith('{'))
        {
            return (
                DecodeBase64(
                    content,
                    OfficialConnectivityError.RegionResponseInvalid,
                    "The regional dispatch response is not valid base64."),
                OfficialRegionalPayloadFormat.DirectProtobuf);
        }

        RegionalEnvelope envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<RegionalEnvelope>(content, JsonOptions)
                ?? throw new JsonException("The response is empty.");
        }
        catch (JsonException exception)
        {
            throw new OfficialConnectivityException(
                OfficialConnectivityError.RegionResponseInvalid,
                "The regional dispatch envelope is invalid.",
                exception);
        }

        byte[] ciphertext = DecodeBase64(
            envelope.Content,
            OfficialConnectivityError.RegionResponseInvalid,
            "The encrypted regional payload is not valid base64.");
        return (
            _crypto.DecryptAndVerify(ciphertext, envelope.Sign, keyId),
            OfficialRegionalPayloadFormat.EncryptedJsonEnvelope);
    }

    private static T ParseBase64<T>(
        string content,
        Action<T, byte[]> parser,
        OfficialConnectivityError error,
        string message)
        where T : new()
    {
        byte[] bytes = DecodeBase64(content, error, message);
        try
        {
            var result = new T();
            parser(result, bytes);
            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new OfficialConnectivityException(error, message, exception);
        }
    }

    private static byte[] DecodeBase64(
        string content,
        OfficialConnectivityError error,
        string message)
    {
        try
        {
            return Convert.FromBase64String(content);
        }
        catch (FormatException exception)
        {
            throw new OfficialConnectivityException(error, message, exception);
        }
    }

    private static Uri AppendQuery(Uri baseUri, IReadOnlyList<KeyValuePair<string, string>> parameters)
    {
        if (!baseUri.IsAbsoluteUri || baseUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("Dispatch URIs must be absolute HTTPS URIs.", nameof(baseUri));
        }

        var builder = new UriBuilder(baseUri);
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(builder.Query))
        {
            parts.Add(builder.Query.TrimStart('?'));
        }

        parts.AddRange(parameters.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
        builder.Query = string.Join('&', parts);
        return builder.Uri;
    }

    private static void ValidateProfile(OfficialClientProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(profile.Version)
            || !string.Equals(profile.ProtocolVersion, "V70", StringComparison.Ordinal)
            || profile.KeyId is 0 or > int.MaxValue
            || profile.ApplicationId == 0)
        {
            throw new ArgumentException("The official V70 client profile is incomplete.", nameof(profile));
        }
    }

    private static void ValidateEc2b(byte[] value, string message)
    {
        if (!Ec2bKeyGen.HasValidLayout(value))
        {
            throw new OfficialConnectivityException(
                OfficialConnectivityError.RegionResponseInvalid,
                message);
        }
    }

    private sealed record RegionalEnvelope
    {
        [JsonPropertyName("content")]
        public required string Content { get; init; }

        [JsonPropertyName("sign")]
        public required string Sign { get; init; }
    }
}
