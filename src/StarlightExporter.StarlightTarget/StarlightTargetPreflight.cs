using StarlightExporter.Snapshot;

namespace StarlightExporter.StarlightTarget;

public sealed record StarlightTargetPreflightResult(
    StarlightMappingResult Mapping,
    StarlightModuleValidationResult? ModuleValidation,
    string? ResourcesRevision)
{
    public bool IsCompatible => Mapping.IsSuccess && ModuleValidation?.IsCompatible == true;
}

public static class StarlightTargetPreflight
{
    public static async Task<StarlightTargetPreflightResult> RunAsync(
        OfficialSnapshot snapshot,
        string resourcesPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        LoadedStarlightGameData loaded = await StarlightGameDataLoader.LoadAsync(
            resourcesPath,
            cancellationToken);
        StarlightMappingResult mapping = new StarlightSnapshotMapper(loaded.Data).Map(snapshot);
        if (!mapping.IsSuccess)
        {
            return new StarlightTargetPreflightResult(mapping, null, loaded.ResourcesRevision);
        }

        StarlightModuleValidationResult moduleValidation =
            await StarlightModuleCompatibilityValidator.ValidateAsync(
                snapshot.Manifest.OfficialUid,
                loaded.Data,
                mapping.Profile,
                mapping.State,
                cancellationToken);
        return new StarlightTargetPreflightResult(mapping, moduleValidation, loaded.ResourcesRevision);
    }
}
