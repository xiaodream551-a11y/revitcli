using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace RevitCli.Shared;

public class ModelSnapshot
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = 1;

    [JsonPropertyName("takenAt")]
    public string TakenAt { get; set; } = "";

    [JsonPropertyName("revit")]
    public SnapshotRevit Revit { get; set; } = new();

    [JsonPropertyName("model")]
    public SnapshotModel Model { get; set; } = new();

    [JsonPropertyName("categories")]
    public Dictionary<string, List<SnapshotElement>> Categories { get; set; } = new();

    [JsonPropertyName("sheets")]
    public List<SnapshotSheet> Sheets { get; set; } = new();

    [JsonPropertyName("schedules")]
    public List<SnapshotSchedule> Schedules { get; set; } = new();

    [JsonPropertyName("summary")]
    public SnapshotSummary Summary { get; set; } = new();
}

public class SnapshotRevit
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("document")]
    public string Document { get; set; } = "";

    [JsonPropertyName("documentPath")]
    public string DocumentPath { get; set; } = "";
}

public class SnapshotModel
{
    [JsonPropertyName("sizeBytes")]
    public long SizeBytes { get; set; }

    [JsonPropertyName("fileHash")]
    public string FileHash { get; set; } = "";
}

public class SnapshotElement
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("typeName")]
    public string TypeName { get; set; } = "";

    [JsonPropertyName("parameters")]
    public Dictionary<string, string> Parameters { get; set; } = new();

    [JsonPropertyName("hash")]
    public string Hash { get; set; } = "";
}

public class SnapshotSheet
{
    [JsonPropertyName("number")]
    public string Number { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("viewId")]
    public long ViewId { get; set; }

    [JsonPropertyName("placedViewIds")]
    public List<long> PlacedViewIds { get; set; } = new();

    [JsonPropertyName("parameters")]
    public Dictionary<string, string> Parameters { get; set; } = new();

    [JsonPropertyName("metaHash")]
    public string MetaHash { get; set; } = "";

    [JsonPropertyName("contentHash")]
    public string ContentHash { get; set; } = "";
}

public class SnapshotSchedule
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("category")]
    public string Category { get; set; } = "";

    [JsonPropertyName("rowCount")]
    public int RowCount { get; set; }

    [JsonPropertyName("hash")]
    public string Hash { get; set; } = "";
}

public class SnapshotSummary
{
    [JsonPropertyName("elementCounts")]
    public Dictionary<string, int> ElementCounts { get; set; } = new();

    [JsonPropertyName("sheetCount")]
    public int SheetCount { get; set; }

    [JsonPropertyName("scheduleCount")]
    public int ScheduleCount { get; set; }
}
