using System.Text.Json.Serialization;

namespace RevitCli.Shared;

public class StatusInfo
{
    [JsonPropertyName("revitVersion")]
    public string RevitVersion { get; set; } = "";

    [JsonPropertyName("documentName")]
    public string? DocumentName { get; set; }

    [JsonPropertyName("documentPath")]
    public string? DocumentPath { get; set; }
}
