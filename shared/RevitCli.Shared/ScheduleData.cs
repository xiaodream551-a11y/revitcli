using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace RevitCli.Shared;

public class ScheduleData
{
    [JsonPropertyName("columns")]
    public List<string> Columns { get; set; } = new();

    [JsonPropertyName("rows")]
    public List<Dictionary<string, string>> Rows { get; set; } = new();

    [JsonPropertyName("totalRows")]
    public int TotalRows { get; set; }
}
