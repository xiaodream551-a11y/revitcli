using System.Collections.Generic;
using RevitCli.Output;
using RevitCli.Shared;
using Xunit;

namespace RevitCli.Tests.Output;

public class DiffRendererTests
{
    private static SnapshotDiff SampleDiff()
    {
        var d = new SnapshotDiff { SchemaVersion = 1, From = "a.json", To = "b.json" };
        d.Categories["walls"] = new CategoryDiff
        {
            Added = new() { new AddedItem { Id = 5, Key = "walls:W5", Name = "W5" } },
            Modified = new()
            {
                new ModifiedItem
                {
                    Id = 1, Key = "walls:W1",
                    Changed = new() { ["Mark"] = new ParamChange { From = "", To = "A" } },
                    OldHash = "h1", NewHash = "h2"
                }
            }
        };
        d.Summary.PerCategory["walls"] = new CategoryCount { Added = 1, Modified = 1 };
        return d;
    }

    [Fact]
    public void RenderTable_IncludesSummaryLine()
    {
        var output = DiffRenderer.Render(SampleDiff(), "table", maxRows: 20);
        Assert.Contains("walls", output);
        Assert.Contains("+1", output);
        Assert.Contains("~1", output);
    }

    [Fact]
    public void RenderTable_ShowsModifiedParamDelta()
    {
        var output = DiffRenderer.Render(SampleDiff(), "table", maxRows: 20);
        Assert.Contains("Mark", output);
        Assert.Contains("\"A\"", output);
    }

    [Fact]
    public void RenderJson_IsValidJson()
    {
        var output = DiffRenderer.Render(SampleDiff(), "json", maxRows: 20);
        var parsed = System.Text.Json.JsonSerializer.Deserialize<SnapshotDiff>(
            output, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal("a.json", parsed.From);
        Assert.Equal(1, parsed.Summary.PerCategory["walls"].Added);
    }

    [Fact]
    public void RenderMarkdown_StartsWithHeader()
    {
        var output = DiffRenderer.Render(SampleDiff(), "markdown", maxRows: 20);
        Assert.StartsWith("## Model changes", output);
        Assert.Contains("### Modified walls", output);
    }

    [Fact]
    public void RenderMarkdown_TruncatesAboveMaxRows()
    {
        var big = new SnapshotDiff { SchemaVersion = 1 };
        var cat = new CategoryDiff();
        for (int i = 0; i < 30; i++)
            cat.Added.Add(new AddedItem { Id = i, Key = $"walls:W{i}", Name = $"W{i}" });
        big.Categories["walls"] = cat;
        big.Summary.PerCategory["walls"] = new CategoryCount { Added = 30 };

        var output = DiffRenderer.Render(big, "markdown", maxRows: 5);
        Assert.Contains("+30", output);
        Assert.Contains("...and 25 more", output);
    }

    [Fact]
    public void Render_UnknownFormat_DefaultsToTable()
    {
        var output = DiffRenderer.Render(SampleDiff(), "xml-whatever", maxRows: 20);
        Assert.Contains("walls", output);
    }

    [Fact]
    public void RenderMarkdown_EscapesPipeInCellValues()
    {
        var d = new SnapshotDiff { SchemaVersion = 1 };
        d.Categories["walls"] = new CategoryDiff
        {
            Modified = new()
            {
                new ModifiedItem
                {
                    Id = 1, Key = "walls:Level|One",
                    Changed = new() { ["Note"] = new ParamChange { From = "a|b", To = "c|d" } },
                    OldHash = "h1", NewHash = "h2"
                }
            }
        };
        d.Summary.PerCategory["walls"] = new CategoryCount { Modified = 1 };

        var output = DiffRenderer.Render(d, "markdown", maxRows: 20);

        Assert.Contains("walls:Level\\|One", output);
        Assert.Contains("\"a\\|b\"", output);
        Assert.Contains("\"c\\|d\"", output);
    }
}
