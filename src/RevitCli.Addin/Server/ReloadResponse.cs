using System.Text.Json.Serialization;

namespace RevitCli.Addin.Server;

public sealed class ReloadResponse
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";
}
