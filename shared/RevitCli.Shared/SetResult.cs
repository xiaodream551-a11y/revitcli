using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace RevitCli.Shared;

public class SetResult
{
    [JsonPropertyName("affected")]
    public int Affected { get; set; }

    [JsonPropertyName("preview")]
    public List<SetPreviewItem> Preview { get; set; } = new();
}

public class SetPreviewItem
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("oldValue")]
    public string? OldValue { get; set; }

    [JsonPropertyName("newValue")]
    public string NewValue { get; set; } = "";
}
