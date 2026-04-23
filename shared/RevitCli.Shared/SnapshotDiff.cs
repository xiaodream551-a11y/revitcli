using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace RevitCli.Shared;

public class SnapshotDiff
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;

    [JsonPropertyName("from")]
    public string From { get; set; } = "";

    [JsonPropertyName("to")]
    public string To { get; set; } = "";

    [JsonPropertyName("categories")]
    public Dictionary<string, CategoryDiff> Categories { get; set; } = new();

    [JsonPropertyName("sheets")]
    public CategoryDiff Sheets { get; set; } = new();

    [JsonPropertyName("schedules")]
    public CategoryDiff Schedules { get; set; } = new();

    [JsonPropertyName("summary")]
    public DiffSummary Summary { get; set; } = new();

    [JsonPropertyName("warnings")]
    public List<string> Warnings { get; set; } = new();
}

public class CategoryDiff
{
    [JsonPropertyName("added")]
    public List<AddedItem> Added { get; set; } = new();

    [JsonPropertyName("removed")]
    public List<RemovedItem> Removed { get; set; } = new();

    [JsonPropertyName("modified")]
    public List<ModifiedItem> Modified { get; set; } = new();
}

public class AddedItem
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
}

public class RemovedItem
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
}

public class ModifiedItem
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    [JsonPropertyName("changed")]
    public Dictionary<string, ParamChange> Changed { get; set; } = new();

    [JsonPropertyName("oldHash")]
    public string? OldHash { get; set; }

    [JsonPropertyName("newHash")]
    public string? NewHash { get; set; }
}

public class ParamChange
{
    [JsonPropertyName("from")]
    public string From { get; set; } = "";

    [JsonPropertyName("to")]
    public string To { get; set; } = "";
}

public class DiffSummary
{
    [JsonPropertyName("perCategory")]
    public Dictionary<string, CategoryCount> PerCategory { get; set; } = new();

    [JsonPropertyName("sheets")]
    public CategoryCount Sheets { get; set; } = new();

    [JsonPropertyName("schedules")]
    public CategoryCount Schedules { get; set; } = new();
}

public class CategoryCount
{
    [JsonPropertyName("added")]
    public int Added { get; set; }

    [JsonPropertyName("removed")]
    public int Removed { get; set; }

    [JsonPropertyName("modified")]
    public int Modified { get; set; }
}
