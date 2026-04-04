using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace RevitCli.Shared;

public class StatusInfo
{
    [JsonPropertyName("revitVersion")]
    public string RevitVersion { get; set; } = "";

    [JsonPropertyName("revitYear")]
    public int RevitYear { get; set; }

    [JsonPropertyName("addinVersion")]
    public string AddinVersion { get; set; } = "";

    [JsonPropertyName("documentName")]
    public string? DocumentName { get; set; }

    [JsonPropertyName("documentPath")]
    public string? DocumentPath { get; set; }

    [JsonPropertyName("capabilities")]
    public List<string> Capabilities { get; set; } = new();
}
