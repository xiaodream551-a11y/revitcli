using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace RevitCli.Shared;

public class ScheduleCreateRequest
{
    [JsonPropertyName("category")]
    public string Category { get; set; } = "";

    [JsonPropertyName("fields")]
    public List<string>? Fields { get; set; }

    [JsonPropertyName("filter")]
    public string? Filter { get; set; }

    [JsonPropertyName("sort")]
    public string? Sort { get; set; }

    [JsonPropertyName("sortDescending")]
    public bool SortDescending { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("placeOnSheet")]
    public string? PlaceOnSheet { get; set; }
}
