using System.Collections.Generic;

namespace RevitCli.Shared;

public class SnapshotDiff
{
    public int SchemaVersion { get; set; } = 1;
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public Dictionary<string, CategoryDiff> Categories { get; set; } = new();
    public CategoryDiff Sheets { get; set; } = new();
    public CategoryDiff Schedules { get; set; } = new();
    public DiffSummary Summary { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}

public class CategoryDiff
{
    public List<AddedItem> Added { get; set; } = new();
    public List<RemovedItem> Removed { get; set; } = new();
    public List<ModifiedItem> Modified { get; set; } = new();
}

public class AddedItem
{
    public long Id { get; set; }
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
}

public class RemovedItem
{
    public long Id { get; set; }
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
}

public class ModifiedItem
{
    public long Id { get; set; }
    public string Key { get; set; } = "";
    public Dictionary<string, ParamChange> Changed { get; set; } = new();
    public string? OldHash { get; set; }
    public string? NewHash { get; set; }
}

public class ParamChange
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
}

public class DiffSummary
{
    public Dictionary<string, CategoryCount> PerCategory { get; set; } = new();
    public CategoryCount Sheets { get; set; } = new();
    public CategoryCount Schedules { get; set; } = new();
}

public class CategoryCount
{
    public int Added { get; set; }
    public int Removed { get; set; }
    public int Modified { get; set; }
}
