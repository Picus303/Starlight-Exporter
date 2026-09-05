namespace StarlightExporter.Snapshot;

public static class SnapshotContract
{
    public const int CurrentSchemaVersion = 2;
    public const string SupportedSourceProtocolVersion = "V70";
    public const long MaximumDocumentBytes = 16 * 1024 * 1024;
    public const int MaximumMaterials = 10000;
    public const int MaximumWeapons = 10000;
    public const int MaximumAvatars = 1000;
    public const int MaximumUnsupportedRecords = 10000;
}
