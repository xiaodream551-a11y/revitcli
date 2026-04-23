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
        var baseline = new SnapshotSheet
        {
            Number = "A-01", Name = "Plan", ViewId = 99,
            Parameters = new() { ["Revision"] = "v1" }
        };
        var baseHash = SnapshotHasher.HashSheetMeta(baseline);
        Assert.Equal(16, baseHash.Length);

        // Changing each tracked field must change the hash.
        var diffNumber = new SnapshotSheet
        {
            Number = "A-02", Name = "Plan", ViewId = 99,
            Parameters = new() { ["Revision"] = "v1" }
        };
        Assert.NotEqual(baseHash, SnapshotHasher.HashSheetMeta(diffNumber));

        var diffName = new SnapshotSheet
        {
            Number = "A-01", Name = "Elevation", ViewId = 99,
            Parameters = new() { ["Revision"] = "v1" }
        };
        Assert.NotEqual(baseHash, SnapshotHasher.HashSheetMeta(diffName));

        var diffViewId = new SnapshotSheet
        {
            Number = "A-01", Name = "Plan", ViewId = 100,
            Parameters = new() { ["Revision"] = "v1" }
        };
        Assert.NotEqual(baseHash, SnapshotHasher.HashSheetMeta(diffViewId));

        var diffParam = new SnapshotSheet
        {
            Number = "A-01", Name = "Plan", ViewId = 99,
            Parameters = new() { ["Revision"] = "v2" }
        };
        Assert.NotEqual(baseHash, SnapshotHasher.HashSheetMeta(diffParam));
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

    [Fact]
    public void HashSchedule_PipeInValue_DoesNotCollideWithSeparator()
    {
        // "A|B" in a single-column row must not hash the same as two separate
        // rows with "A" and "B", since `|` is used as the column separator internally.
        var cols = new List<string> { "Mark" };
        var rowsPipeValue = new List<Dictionary<string, string>>
        {
            new() { ["Mark"] = "A|B" }
        };
        var rowsTwoValues = new List<Dictionary<string, string>>
        {
            new() { ["Mark"] = "A" },
            new() { ["Mark"] = "B" }
        };
        Assert.NotEqual(
            SnapshotHasher.HashSchedule("Doors", "S", cols, rowsPipeValue),
            SnapshotHasher.HashSchedule("Doors", "S", cols, rowsTwoValues));
    }

    [Fact]
    public void HashSheetContent_StableForSameInputs()
    {
        var perView = new List<(long viewId, List<string> elementHashes)>
        {
            (200, new List<string> { "h-wall-1", "h-wall-2" }),
            (201, new List<string> { "h-door-1" })
        };
        var h1 = SnapshotHasher.HashSheetContent("meta-hash-A", perView);
        var h2 = SnapshotHasher.HashSheetContent("meta-hash-A", perView);
        Assert.Equal(h1, h2);
        Assert.Equal(16, h1.Length);
    }

    [Fact]
    public void HashSheetContent_DiffersWhenMetaHashChanges()
    {
        var perView = new List<(long, List<string>)>
        {
            (200, new List<string> { "h-wall-1" })
        };
        var a = SnapshotHasher.HashSheetContent("meta-A", perView);
        var b = SnapshotHasher.HashSheetContent("meta-B", perView);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void HashSheetContent_DiffersWhenElementHashesChange()
    {
        var perViewA = new List<(long, List<string>)> { (200, new() { "h1" }) };
        var perViewB = new List<(long, List<string>)> { (200, new() { "h2" }) };
        Assert.NotEqual(
            SnapshotHasher.HashSheetContent("m", perViewA),
            SnapshotHasher.HashSheetContent("m", perViewB));
    }

    [Fact]
    public void HashSheetContent_StableAcrossViewInsertionOrder()
    {
        var a = new List<(long, List<string>)>
        {
            (200, new() { "h1" }),
            (201, new() { "h2" })
        };
        var b = new List<(long, List<string>)>
        {
            (201, new() { "h2" }),
            (200, new() { "h1" })
        };
        Assert.Equal(
            SnapshotHasher.HashSheetContent("m", a),
            SnapshotHasher.HashSheetContent("m", b));
    }

    [Fact]
    public void HashSheetContent_StableAcrossElementOrderWithinView()
    {
        // Element hashes within a view: sorted stably so hash doesn't depend on
        // the order Revit returned elements in.
        var a = new List<(long, List<string>)> { (200, new() { "h-a", "h-b", "h-c" }) };
        var b = new List<(long, List<string>)> { (200, new() { "h-c", "h-a", "h-b" }) };
        Assert.Equal(
            SnapshotHasher.HashSheetContent("m", a),
            SnapshotHasher.HashSheetContent("m", b));
    }
}
