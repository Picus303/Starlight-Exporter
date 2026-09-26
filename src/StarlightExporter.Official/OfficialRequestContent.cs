using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace StarlightExporter.Official;

internal static class OfficialRequestContent
{
    public static HttpContent Json(string json) => Utf8(json, "application/json");

    // The 7.0 DeviceFp transport supplies a JSON body through libcurl's raw
    // POST field path. With no explicit SDK header map, libcurl emits this
    // default media type on the wire.
    public static HttpContent DeviceFingerprintJson(string json) =>
        Utf8(json, "application/x-www-form-urlencoded");

    public static void ApplyObservedTransportDefaults(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Version = HttpVersion.Version11;
        request.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        request.Headers.Accept.ParseAdd("*/*");
    }

    private static StringContent Utf8(string body, string mediaType)
    {
        ArgumentNullException.ThrowIfNull(body);
        var content = new StringContent(body, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return content;
    }
}
