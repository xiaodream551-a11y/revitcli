using System.Collections.Generic;

namespace RevitCli.Fix;

internal sealed class FixJournal
{
    public int SchemaVersion { get; set; } = 1;
    public string Action { get; set; } = "fix";
    public string CheckName { get; set; } = "default";
    public string? ProfilePath { get; set; }
    public string BaselinePath { get; set; } = "";
    public string StartedAt { get; set; } = "";
    public string? CompletedAt { get; set; }
    public string User { get; set; } = "";
    public List<FixAction> Actions { get; set; } = new();
}
