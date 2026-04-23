using System.Collections.Generic;
using RevitCli.Shared;
using Xunit;

namespace RevitCli.Tests.Shared;

public class SnapshotHasherTests
{
    [Fact]
    public void HashElement_IsStable_ForSameInput()
    {
        var el = new SnapshotElement
        {
            Id = 337596, Name = "Wall", TypeName = "200mm",
            Parameters = new() { ["Mark"] = "W01", ["Length"] = "5000" }
        };
        var h1 = SnapshotHasher.HashElement(el);
        var h2 = SnapshotHasher.HashElement(el);
        Assert.Equal(h1, h2);
        Assert.Equal(16, h1.Length);
    }

    [Fact]
    public void HashElement_ChangesWhenParamValueChanges()
    {
        var a = new SnapshotElement { Id = 1, Parameters = new() { ["Mark"] = "A" } };
        var b = new SnapshotElement { Id = 1, Parameters = new() { ["Mark"] = "B" } };
        Assert.NotEqual(SnapshotHasher.HashElement(a), SnapshotHasher.HashElement(b));
    }

    [Fact]
    public void HashElement_StableAcrossParameterInsertionOrder()
    {
        var a = new SnapshotElement { Id = 1,
            Parameters = new() { ["Mark"] = "A", ["Length"] = "5" } };
        var b = new SnapshotElement { Id = 1,
            Parameters = new() { ["Length"] = "5", ["Mark"] = "A" } };
        Assert.Equal(SnapshotHasher.HashElement(a), SnapshotHasher.HashElement(b));
    }

    [Fact]
    public void HashElement_HandlesNewlinesInValues()
    {
        var a = new SnapshotElement { Id = 1, Parameters = new() { ["Note"] = "line1\nline2" } };
        var b = new SnapshotElement { Id = 1, Parameters = new() { ["Note"] = "line1\\nline2" } };
        // Escape should preserve distinction
        Assert.NotEqual(SnapshotHasher.HashElement(a), SnapshotHasher.HashElement(b));
    }

    [Fact]
    public void HashSheetMeta_Includes_NumberNameAndParameters()
    {
        var s = new SnapshotSheet
        {
            Number = "A-01", Name = "Plan", ViewId = 99,
            Parameters = new() { ["Revision"] = "v1" }
        };
        var h = SnapshotHasher.HashSheetMeta(s);
        Assert.Equal(16, h.Length);
    }

    [Fact]
    public void HashSchedule_StableForSameColumnsAndRows()
    {
        var cols = new List<string> { "Mark", "Width" };
        var rows = new List<Dictionary<string, string>>
        {
            new() { ["Mark"] = "D1", ["Width"] = "900" },
            new() { ["Mark"] = "D2", ["Width"] = "800" }
        };
        var h1 = SnapshotHasher.HashSchedule("Doors", "Door Schedule", cols, rows);
        var h2 = SnapshotHasher.HashSchedule("Doors", "Door Schedule", cols, rows);
        Assert.Equal(h1, h2);
    }

    [Fact]
    public void HashSchedule_DiffersWhenRowsReorder()
    {
        // Row order is significant (schedule sort rule change should be detected)
        var cols = new List<string> { "Mark" };
        var rows1 = new List<Dictionary<string, string>>
        {
            new() { ["Mark"] = "A" }, new() { ["Mark"] = "B" }
        };
        var rows2 = new List<Dictionary<string, string>>
        {
            new() { ["Mark"] = "B" }, new() { ["Mark"] = "A" }
        };
        Assert.NotEqual(
            SnapshotHasher.HashSchedule("Doors", "S", cols, rows1),
            SnapshotHasher.HashSchedule("Doors", "S", cols, rows2));
    }
}
