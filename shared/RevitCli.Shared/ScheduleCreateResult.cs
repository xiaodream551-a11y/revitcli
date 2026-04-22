using System.Text.Json.Serialization;

namespace RevitCli.Shared;

public class ScheduleCreateResult
{
    [JsonPropertyName("viewId")]
    public long ViewId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("fieldCount")]
    public int FieldCount { get; set; }

    [JsonPropertyName("rowCount")]
    public int RowCount { get; set; }

    [JsonPropertyName("placedOnSheet")]
    public string? PlacedOnSheet { get; set; }
}
