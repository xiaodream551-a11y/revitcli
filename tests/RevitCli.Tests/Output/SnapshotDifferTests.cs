using System;
using System.Collections.Generic;
using RevitCli.Output;
using RevitCli.Shared;
using Xunit;

namespace RevitCli.Tests.Output;

public class SnapshotDifferTests
{
    private static ModelSnapshot MakeSnap(params (string cat, long id, string mark, string hash)[] elements)
    {
        var snap = new ModelSnapshot
        {
            SchemaVersion = 1,
            TakenAt = "2026-04-23T00:00:00Z",
            Revit = new SnapshotRevit { Version = "2026", Document = "d", DocumentPath = "/a.rvt" }
        };
        foreach (var (cat, id, mark, hash) in elements)
        {
            if (!snap.Categories.TryGetValue(cat, out var list))
                snap.Categories[cat] = list = new List<SnapshotElement>();
            list.Add(new SnapshotElement
            {
                Id = id, Name = $"E{id}", Parameters = new() { ["Mark"] = mark }, Hash = hash
            });
        }
        return snap;
    }

    [Fact]
    public void Diff_IdenticalSnapshots_HasNoChanges()
    {
        var a = MakeSnap(("walls", 1, "A", "h1"));
        var b = MakeSnap(("walls", 1, "A", "h1"));
        var d = SnapshotDiffer.Diff(a, b);
        Assert.Empty(d.Categories["walls"].Added);
        Assert.Empty(d.Categories["walls"].Removed);
        Assert.Empty(d.Categories["walls"].Modified);
    }

    [Fact]
    public void Diff_AddedElement_AppearsInAddedList()
    {
        var a = MakeSnap(("walls", 1, "A", "h1"));
        var b = MakeSnap(("walls", 1, "A", "h1"), ("walls", 2, "B", "h2"));
        var d = SnapshotDiffer.Diff(a, b);
        var added = Assert.Single(d.Categories["walls"].Added);
        Assert.Equal(2, added.Id);
    }

    [Fact]
    public void Diff_RemovedElement_AppearsInRemovedList()
    {
        var a = MakeSnap(("walls", 1, "A", "h1"), ("walls", 2, "B", "h2"));
        var b = MakeSnap(("walls", 1, "A", "h1"));
        var d = SnapshotDiffer.Diff(a, b);
        var removed = Assert.Single(d.Categories["walls"].Removed);
        Assert.Equal(2, removed.Id);
    }

    [Fact]
    public void Diff_HashChanged_AppearsAsModifiedWithParamDelta()
    {
        var a = MakeSnap(("walls", 1, "OldMark", "h1"));
        var b = MakeSnap(("walls", 1, "NewMark", "h2"));
        var d = SnapshotDiffer.Diff(a, b);
        var mod = Assert.Single(d.Categories["walls"].Modified);
        Assert.Equal(1, mod.Id);
        Assert.Equal("OldMark", mod.Changed["Mark"].From);
        Assert.Equal("NewMark", mod.Changed["Mark"].To);
    }

    [Fact]
    public void Diff_SchemaVersionMismatch_Throws()
    {
        var a = MakeSnap();
        var b = MakeSnap();
        b.SchemaVersion = 2;
        var ex = Assert.Throws<InvalidOperationException>(() => SnapshotDiffer.Diff(a, b));
        Assert.Contains("Schema mismatch", ex.Message);
    }

    [Fact]
    public void Diff_DocumentPathMismatch_AddsWarning()
    {
        var a = MakeSnap();
        var b = MakeSnap();
        b.Revit.DocumentPath = "/different.rvt";
        var d = SnapshotDiffer.Diff(a, b);
        Assert.Contains(d.Warnings, w => w.Contains("DocumentPath"));
    }

    [Fact]
    public void Diff_NewCategoryInB_YieldsAllElementsAsAdded()
    {
        var a = MakeSnap(("walls", 1, "A", "h1"));
        var b = MakeSnap(("walls", 1, "A", "h1"), ("doors", 10, "D", "d1"));
        var d = SnapshotDiffer.Diff(a, b);
        Assert.Single(d.Categories["doors"].Added);
        Assert.Empty(d.Categories["walls"].Added);
    }

    [Fact]
    public void Diff_Sheets_KeyedByNumberNotId()
    {
        var a = MakeSnap();
        a.Sheets.Add(new SnapshotSheet { Number = "A-01", Name = "Old", ViewId = 1, MetaHash = "h1" });
        var b = MakeSnap();
        b.Sheets.Add(new SnapshotSheet { Number = "A-01", Name = "New", ViewId = 2, MetaHash = "h2" });
        var d = SnapshotDiffer.Diff(a, b);
        var mod = Assert.Single(d.Sheets.Modified);
        Assert.Equal("sheet:A-01", mod.Key);
    }

    [Fact]
    public void Diff_Summary_CountsPerCategory()
    {
        var a = MakeSnap(("walls", 1, "A", "h1"));
        var b = MakeSnap(("walls", 1, "A", "h1"),
                          ("walls", 2, "B", "h2"),
                          ("doors", 10, "D", "dh1"));
        var d = SnapshotDiffer.Diff(a, b);
        Assert.Equal(1, d.Summary.PerCategory["walls"].Added);
        Assert.Equal(1, d.Summary.PerCategory["doors"].Added);
    }

    [Fact]
    public void Diff_FromAndToFieldsSetFromLabels()
    {
        var a = MakeSnap();
        var b = MakeSnap();
        var d = SnapshotDiffer.Diff(a, b, "baseline.json", "current.json");
        Assert.Equal("baseline.json", d.From);
        Assert.Equal("current.json", d.To);
    }
}
