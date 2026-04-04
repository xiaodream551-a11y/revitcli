using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace RevitCli.Shared;

public class AuditRequest
{
    [JsonPropertyName("rules")]
    public List<string> Rules { get; set; } = new();

    [JsonPropertyName("requiredParameters")]
    public List<RequiredParameterSpec> RequiredParameters { get; set; } = new();

    [JsonPropertyName("namingPatterns")]
    public List<NamingPatternSpec> NamingPatterns { get; set; } = new();
}

public class RequiredParameterSpec
{
    [JsonPropertyName("category")]
    public string Category { get; set; } = "";

    [JsonPropertyName("parameter")]
    public string Parameter { get; set; } = "";

    [JsonPropertyName("requireNonEmpty")]
    public bool RequireNonEmpty { get; set; } = true;

    [JsonPropertyName("severity")]
    public string Severity { get; set; } = "error";
}

public class NamingPatternSpec
{
    [JsonPropertyName("target")]
    public string Target { get; set; } = "";

    [JsonPropertyName("pattern")]
    public string Pattern { get; set; } = "";

    [JsonPropertyName("severity")]
    public string Severity { get; set; } = "warning";
}
