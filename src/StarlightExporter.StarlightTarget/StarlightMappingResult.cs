using Starlight.Rpc.Proto;

namespace StarlightExporter.StarlightTarget;

public enum MappingIssueSeverity
{
    Warning,
    Error,
}

public sealed record MappingIssue(
    MappingIssueSeverity Severity,
    string Code,
    string Message);

public sealed record StarlightMappingResult(
    NetPlayerProfile Profile,
    NetPlayerState State,
    IReadOnlyList<MappingIssue> Issues)
{
    public bool IsSuccess => Issues.All(issue => issue.Severity != MappingIssueSeverity.Error);
}
