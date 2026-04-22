using System.Text.Json.Serialization;

namespace RevitCli.Shared;

public class ServerInfo
{
    [JsonPropertyName("port")]
    public int Port { get; set; }

    [JsonPropertyName("pid")]
    public int Pid { get; set; }

    [JsonPropertyName("revitVersion")]
    public string RevitVersion { get; set; } = "";

    [JsonPropertyName("startedAt")]
    public string StartedAt { get; set; } = "";

    [JsonPropertyName("token")]
    public string Token { get; set; } = "";
}
