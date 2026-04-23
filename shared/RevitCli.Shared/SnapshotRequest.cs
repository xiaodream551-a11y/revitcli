using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace RevitCli.Shared;

public class SnapshotRequest
{
    [JsonPropertyName("includeCategories")]
    public List<string>? IncludeCategories { get; set; }

    [JsonPropertyName("includeSheets")]
    public bool IncludeSheets { get; set; } = true;

    [JsonPropertyName("includeSchedules")]
    public bool IncludeSchedules { get; set; } = true;

    [JsonPropertyName("summaryOnly")]
    public bool SummaryOnly { get; set; } = false;
}
