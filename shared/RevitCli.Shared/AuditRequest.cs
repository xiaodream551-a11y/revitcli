using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace RevitCli.Shared;

public class AuditRequest
{
    [JsonPropertyName("rules")]
    public List<string> Rules { get; set; } = new();
}
