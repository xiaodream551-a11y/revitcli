using System.Collections.Generic;

namespace RevitCli.Shared;

public class ModelSnapshot
{
    public int SchemaVersion { get; set; } = 1;
    public string TakenAt { get; set; } = "";
    public SnapshotRevit Revit { get; set; } = new();
    public SnapshotModel Model { get; set; } = new();
    public Dictionary<string, List<SnapshotElement>> Categories { get; set; } = new();
    public List<SnapshotSheet> Sheets { get; set; } = new();
    public List<SnapshotSchedule> Schedules { get; set; } = new();
    public SnapshotSummary Summary { get; set; } = new();
}

public class SnapshotRevit
{
    public string Version { get; set; } = "";
    public string Document { get; set; } = "";
    public string DocumentPath { get; set; } = "";
}

public class SnapshotModel
{
    public long SizeBytes { get; set; }
    public string FileHash { get; set; } = "";
}

public class SnapshotElement
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string TypeName { get; set; } = "";
    public Dictionary<string, string> Parameters { get; set; } = new();
    public string Hash { get; set; } = "";
}

public class SnapshotSheet
{
    public string Number { get; set; } = "";
    public string Name { get; set; } = "";
    public long ViewId { get; set; }
    public List<long> PlacedViewIds { get; set; } = new();
    public Dictionary<string, string> Parameters { get; set; } = new();
    public string MetaHash { get; set; } = "";
    public string ContentHash { get; set; } = "";
}

public class SnapshotSchedule
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public int RowCount { get; set; }
    public string Hash { get; set; } = "";
}

public class SnapshotSummary
{
    public Dictionary<string, int> ElementCounts { get; set; } = new();
    public int SheetCount { get; set; }
    public int ScheduleCount { get; set; }
}
