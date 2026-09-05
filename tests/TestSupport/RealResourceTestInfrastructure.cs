using Xunit;

namespace StarlightExporter.Tests;

internal sealed class RealResourcesFactAttribute : FactAttribute
{
    public RealResourcesFactAttribute()
    {
        if (RealResourceArchive.Find() is null)
        {
            Skip = "Set STARLIGHT_EXPORTER_TEST_RESOURCES or run scripts/prepare-resources.ps1 to enable this integration test.";
        }
    }
}

internal static class RealResourceArchive
{
    public static string? Find()
    {
        string? configuredPath = Environment.GetEnvironmentVariable("STARLIGHT_EXPORTER_TEST_RESOURCES");
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            string fullPath = Path.GetFullPath(configuredPath);
            return File.Exists(fullPath) ? fullPath : null;
        }

        string? repositoryRoot = FindRepositoryRoot();
        string archivePath = repositoryRoot is null
            ? string.Empty
            : Path.Combine(repositoryRoot, ".local", "resources", "resources.zip");
        return File.Exists(archivePath) ? archivePath : null;
    }

    internal static string? FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "StarlightExporter.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName;
    }
}

[CollectionDefinition("Real resources", DisableParallelization = true)]
public sealed class RealResourceSerialGroup;
