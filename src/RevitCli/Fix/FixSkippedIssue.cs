namespace RevitCli.Fix;

internal sealed class FixSkippedIssue
{
    public string Rule { get; init; } = "";
    public string Severity { get; init; } = "";
    public long? ElementId { get; init; }
    public string Reason { get; init; } = "";
}
