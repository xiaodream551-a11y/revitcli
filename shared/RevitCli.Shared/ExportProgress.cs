using System.Text.Json.Serialization;

namespace RevitCli.Shared;

public class ExportProgress
{
    [JsonPropertyName("taskId")]
    public string TaskId { get; set; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("progress")]
    public int Progress { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}
