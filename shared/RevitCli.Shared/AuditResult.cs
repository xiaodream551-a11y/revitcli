using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace RevitCli.Shared;

public class AuditResult
{
    [JsonPropertyName("passed")]
    public int Passed { get; set; }

    [JsonPropertyName("failed")]
    public int Failed { get; set; }

    [JsonPropertyName("issues")]
    public List<AuditIssue> Issues { get; set; } = new();
}

public class AuditIssue
{
    [JsonPropertyName("rule")]
    public string Rule { get; set; } = "";

    [JsonPropertyName("severity")]
    public string Severity { get; set; } = "";  // "error", "warning", "info"

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("elementId")]
    public long? ElementId { get; set; }
}
