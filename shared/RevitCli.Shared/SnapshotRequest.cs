using System.Collections.Generic;

namespace RevitCli.Shared;

public class SnapshotRequest
{
    public List<string>? IncludeCategories { get; set; }
    public bool IncludeSheets { get; set; } = true;
    public bool IncludeSchedules { get; set; } = true;
    public bool SummaryOnly { get; set; } = false;
}
