namespace StarlightExporter.Snapshot;

public static class SnapshotContract
{
    public const int CurrentSchemaVersion = 1;
    public const string StarlightCommit = "c1cd286c4909d31d355006899c5905ef6adf9741";
    public const string ProtocolVersion = "V70";
    public const long MaximumDocumentBytes = 16 * 1024 * 1024;
    public const int MaximumMaterials = 10000;
    public const int MaximumWeapons = 10000;
    public const int MaximumAvatars = 1000;
    public const int MaximumUnsupportedRecords = 10000;
}
