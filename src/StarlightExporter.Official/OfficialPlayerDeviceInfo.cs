using System.Globalization;

namespace StarlightExporter.Official;

public sealed record OfficialPlayerDeviceInfo
{
    public required string OperatingSystem { get; init; }
    public required string DeviceModel { get; init; }
    public required string GraphicsDeviceName { get; init; }
    public required string GraphicsDeviceType { get; init; }
    public required string GraphicsDeviceVendor { get; init; }
    public required string GraphicsDeviceVersion { get; init; }
    public required ulong GraphicsMemorySize { get; init; }
    public required uint ProcessorCount { get; init; }
    public required uint ProcessorFrequency { get; init; }
    public required string ProcessorType { get; init; }
    public required ulong SystemMemorySize { get; init; }

    public string Serialize()
    {
        return string.Join('&',
            Field("operatingSystem", OperatingSystem),
            Field("deviceModel", DeviceModel),
            Field("graphicsDeviceName", GraphicsDeviceName),
            Field("graphicsDeviceType", GraphicsDeviceType),
            Field("graphicsDeviceVendor", GraphicsDeviceVendor),
            Field("graphicsDeviceVersion", GraphicsDeviceVersion),
            Field("graphicsMemorySize", GraphicsMemorySize),
            Field("processorCount", ProcessorCount),
            Field("processorFrequency", ProcessorFrequency),
            Field("processorType", ProcessorType),
            Field("systemMemorySize", SystemMemorySize),
            "platformdetail:WinST");
    }

    private static string Field(string name, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return $"{name}:{value}";
    }

    private static string Field<T>(string name, T value) where T : IFormattable =>
        $"{name}:{value.ToString(null, CultureInfo.InvariantCulture)}";
}
