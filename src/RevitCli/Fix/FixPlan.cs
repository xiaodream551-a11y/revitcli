namespace RevitCli.Fix;

internal sealed class FixPlan
{
    public string CheckName { get; init; } = "default";
    public List<FixAction> Actions { get; init; } = new();
    public List<FixSkippedIssue> Skipped { get; init; } = new();
    public List<string> Warnings { get; init; } = new();
}
