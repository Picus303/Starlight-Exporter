using System.Text.Json;
using StarlightExporter.Snapshot;

return await RunAsync(args);

static async Task<int> RunAsync(string[] arguments)
{
    if (arguments is not ["inspect", var snapshotPath])
    {
        Console.Error.WriteLine("Usage: starlight-export inspect <snapshot.json>");
        return 2;
    }

    try
    {
        OfficialSnapshot snapshot = await OfficialSnapshotSerializer.ReadAsync(snapshotPath);
        SnapshotValidationResult validation = SnapshotValidator.Validate(snapshot);

        if (!validation.IsValid)
        {
            Console.Error.WriteLine($"Snapshot invalid ({validation.Errors.Count} error(s)): ");
            foreach (SnapshotValidationError error in validation.Errors)
            {
                Console.Error.WriteLine($"  {error.Code}: {error.Message}");
            }

            return 3;
        }

        Console.WriteLine($"Snapshot schema: {snapshot.Manifest.SchemaVersion}");
        Console.WriteLine($"Starlight commit: {snapshot.Manifest.StarlightCommit}");
        Console.WriteLine($"Protocol: {snapshot.Manifest.ProtocolVersion}");
        Console.WriteLine($"Official UID: {snapshot.Manifest.OfficialUid}");
        Console.WriteLine($"Region: {snapshot.Manifest.Region}");
        Console.WriteLine($"Captured at: {snapshot.Manifest.CapturedAtUtc:O}");
        Console.WriteLine($"Materials: {snapshot.Materials.Count}");
        Console.WriteLine($"Weapons: {snapshot.Weapons.Count}");
        Console.WriteLine($"Avatars: {snapshot.Avatars.Count}");
        Console.WriteLine($"Teams: {snapshot.Teams.Count}");
        Console.WriteLine($"Unsupported records: {snapshot.Unsupported.Count}");
        return 0;
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
    {
        Console.Error.WriteLine($"Unable to inspect snapshot: {exception.Message}");
        return 1;
    }
}

