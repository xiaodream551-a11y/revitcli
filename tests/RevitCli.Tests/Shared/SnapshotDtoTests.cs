using System.Collections.Generic;
using System.Text.Json;
using RevitCli.Shared;
using Xunit;

namespace RevitCli.Tests.Shared;

public class SnapshotDtoTests
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public void ModelSnapshot_Roundtrip_PreservesAllFields()
    {
        var original = new ModelSnapshot
        {
            SchemaVersion = 1,
            TakenAt = "2026-04-23T10:00:00Z",
            Revit = new SnapshotRevit { Version = "2026", Document = "test", DocumentPath = "D:\\test.rvt" },
            Model = new SnapshotModel { SizeBytes = 100, FileHash = "abc" },
            Categories = new Dictionary<string, List<SnapshotElement>>
            {
                ["walls"] = new() { new SnapshotElement { Id = 1, Name = "W1", TypeName = "T1",
                    Parameters = new() { ["Mark"] = "A" }, Hash = "h1" } }
            },
            Sheets = new() { new SnapshotSheet { Number = "A-01", Name = "Plan",
                ViewId = 99, PlacedViewIds = new() { 1, 2 },
                Parameters = new() { ["Revision"] = "v1" }, MetaHash = "mh", ContentHash = "" } },
            Schedules = new() { new SnapshotSchedule { Id = 55, Name = "S1",
                Category = "walls", RowCount = 3, Hash = "sh" } },
            Summary = new SnapshotSummary
            {
                ElementCounts = new() { ["walls"] = 1 },
                SheetCount = 1, ScheduleCount = 1
            }
        };

        var json = JsonSerializer.Serialize(original);

        // Lock the wire contract: JSON must use camelCase (project convention).
        Assert.Contains("\"schemaVersion\"", json);
        Assert.Contains("\"takenAt\"", json);
        Assert.Contains("\"documentPath\"", json);
        Assert.DoesNotContain("\"SchemaVersion\"", json);

        var restored = JsonSerializer.Deserialize<ModelSnapshot>(json, JsonOpts)!;

        Assert.Equal(1, restored.SchemaVersion);
        Assert.Equal("2026-04-23T10:00:00Z", restored.TakenAt);
        Assert.Equal("2026", restored.Revit.Version);
        Assert.Single(restored.Categories);
        Assert.Equal("W1", restored.Categories["walls"][0].Name);
        Assert.Equal("A", restored.Categories["walls"][0].Parameters["Mark"]);
        Assert.Single(restored.Sheets);
        Assert.Equal("A-01", restored.Sheets[0].Number);
        Assert.Equal(2, restored.Sheets[0].PlacedViewIds.Count);
        Assert.Single(restored.Schedules);
        Assert.Equal(1, restored.Summary.ElementCounts["walls"]);
    }

    [Fact]
    public void SnapshotRequest_DefaultsAreCorrect()
    {
        var r = new SnapshotRequest();
        Assert.Null(r.IncludeCategories);
        Assert.True(r.IncludeSheets);
        Assert.True(r.IncludeSchedules);
        Assert.False(r.SummaryOnly);
    }

    [Fact]
    public void SnapshotDiff_Roundtrip_PreservesAllSections()
    {
        var d = new SnapshotDiff
        {
            SchemaVersion = 1,
            From = "a.json",
            To = "b.json",
            Categories = new() {
                ["walls"] = new CategoryDiff {
                    Added = new() { new AddedItem { Id = 5, Key = "walls:W5", Name = "W5" } },
                    Modified = new() { new ModifiedItem {
                        Id = 1, Key = "walls:W1",
                        Changed = new() { ["Mark"] = new ParamChange { From = "", To = "A" } },
                        OldHash = "h1", NewHash = "h2"
                    } }
                }
            }
        };

        var json = JsonSerializer.Serialize(d);
        var restored = JsonSerializer.Deserialize<SnapshotDiff>(json, JsonOpts)!;

        Assert.Equal("a.json", restored.From);
        Assert.Single(restored.Categories["walls"].Added);
        Assert.Equal("A", restored.Categories["walls"].Modified[0].Changed["Mark"].To);
    }
}
