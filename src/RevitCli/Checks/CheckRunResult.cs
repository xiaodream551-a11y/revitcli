using System.Collections.Generic;
using RevitCli.Profile;
using RevitCli.Shared;

namespace RevitCli.Checks;

internal sealed class CheckRunResult
{
    public ProjectProfile Profile { get; init; } = new();
    public CheckDefinition CheckDefinition { get; init; } = new();
    public string CheckName { get; init; } = "default";
    public string? ProfilePath { get; init; }
    public List<AuditIssue> Issues { get; init; } = new();
    public int SuppressedCount { get; init; }
    public int DisplayPassed { get; init; }
    public int DisplayFailed { get; init; }
}
