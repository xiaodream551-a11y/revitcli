using System.Text.Json.Serialization;

namespace RevitCli.Shared;

public class SetRequest
{
    [JsonPropertyName("category")]
    public string? Category { get; set; }

    [JsonPropertyName("elementId")]
    public long? ElementId { get; set; }

    [JsonPropertyName("filter")]
    public string? Filter { get; set; }

    [JsonPropertyName("param")]
    public string Param { get; set; } = "";

    [JsonPropertyName("value")]
    public string Value { get; set; } = "";

    [JsonPropertyName("dryRun")]
    public bool DryRun { get; set; }
}
