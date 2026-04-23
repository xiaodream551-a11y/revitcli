using System.Collections.Generic;
using RevitCli.Output;
using RevitCli.Shared;
using Xunit;

namespace RevitCli.Tests.Output;

public class SinceModeTests
{
    private static ModelSnapshot SnapWithSheet(string number, string metaHash, string contentHash)
    {
        return new ModelSnapshot
        {
            SchemaVersion = 1,
            Revit = new SnapshotRevit { DocumentPath = "/a.rvt" },
            Sheets = new List<SnapshotSheet>
            {
                new() { Number = number, Name = "S", ViewId = 99,
                        MetaHash = metaHash, ContentHash = contentHash }
            }
        };
    }

    [Fact]
    public void Diff_ContentMode_DetectsContentHashChange_EvenIfMetaSame()
    {
        var a = SnapWithSheet("A-01", metaHash: "m1", contentHash: "c1");
        var b = SnapWithSheet("A-01", metaHash: "m1", contentHash: "c2");
        var d = SnapshotDiffer.Diff(a, b, sinceMode: SinceMode.Content);
        Assert.Single(d.Sheets.Modified);
        Assert.Equal("sheet:A-01", d.Sheets.Modified[0].Key);
    }

    [Fact]
    public void Diff_MetaMode_IgnoresContentHashChange_WhenMetaSame()
    {
        var a = SnapWithSheet("A-01", metaHash: "m1", contentHash: "c1");
        var b = SnapWithSheet("A-01", metaHash: "m1", contentHash: "c2");
        var d = SnapshotDiffer.Diff(a, b, sinceMode: SinceMode.Meta);
        Assert.Empty(d.Sheets.Modified);
    }

    [Fact]
    public void Diff_ContentMode_EmptyBaselineContentHash_FallsBackToMeta()
    {
        // P1 baseline had empty ContentHash. v1.2.0 diff should NOT mark this sheet as
        // modified just because content is filled now — fall back to MetaHash comparison.
        var a = SnapWithSheet("A-01", metaHash: "m1", contentHash: "");     // P1-era baseline
        var b = SnapWithSheet("A-01", metaHash: "m1", contentHash: "c2");   // v1.2.0 snapshot
        var d = SnapshotDiffer.Diff(a, b, sinceMode: SinceMode.Content);
        Assert.Empty(d.Sheets.Modified);
    }

    [Fact]
    public void Diff_ContentMode_EmptyBaselineContentHash_DetectsMetaChange()
    {
        var a = SnapWithSheet("A-01", metaHash: "m1", contentHash: "");
        var b = SnapWithSheet("A-01", metaHash: "m2", contentHash: "c2");
        var d = SnapshotDiffer.Diff(a, b, sinceMode: SinceMode.Content);
        Assert.Single(d.Sheets.Modified);
    }

    [Fact]
    public void Diff_NoSinceModeArgument_DefaultsToPreExistingMetaHashBehavior()
    {
        // The original Diff(from, to) overload (no sinceMode) keeps its P1 semantics
        // (compare MetaHash). This is source compat for any caller that passed nothing.
        var a = SnapWithSheet("A-01", metaHash: "m1", contentHash: "c1");
        var b = SnapWithSheet("A-01", metaHash: "m1", contentHash: "c2");
        var d = SnapshotDiffer.Diff(a, b);
        Assert.Empty(d.Sheets.Modified);  // MetaHash-only under the no-arg overload
    }

    [Fact]
    public void Diff_ContentMode_DetectsBothMetaAndContentChanges()
    {
        var a = SnapWithSheet("A-01", metaHash: "m1", contentHash: "c1");
        var b = SnapWithSheet("A-01", metaHash: "m2", contentHash: "c2");
        var d = SnapshotDiffer.Diff(a, b, sinceMode: SinceMode.Content);
        Assert.Single(d.Sheets.Modified);
    }
}
