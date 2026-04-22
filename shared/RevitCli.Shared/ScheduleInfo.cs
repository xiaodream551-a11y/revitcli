using System.Text.Json.Serialization;

namespace RevitCli.Shared;

public class ScheduleInfo
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("category")]
    public string Category { get; set; } = "";

    [JsonPropertyName("fieldCount")]
    public int FieldCount { get; set; }

    [JsonPropertyName("rowCount")]
    public int RowCount { get; set; }
}
