using System.Collections.Generic;
using System.Linq;
using RevitCli.Reports;
using RevitCli.Shared;
using Xunit;

namespace RevitCli.Tests.Reports;

public class PrCommentWriterTests
{
    [Fact]
    public void Render_EmptyList_ReportsNoIssues()
    {
        var output = PrCommentWriter.Render(new List<AuditIssue>());

        Assert.Contains("RevitCli Check Report", output);
        Assert.Contains("No issues found.", output);
    }

    [Fact]
    public void Render_PrependsStickyMarkerAsFirstLine()
    {
        // The companion GitHub Action ("PR comment bot") locates the
        // previous comment to update by looking for this exact marker.
        // It must be:
        //   - Present in EVERY rendered comment, including the empty
        //     "no issues" path
        //   - The first line, so the bot's substring search has the
        //     fewest false-positive hits in long PR histories
        var emptyOutput = PrCommentWriter.Render(new List<AuditIssue>());
        var firstLineEmpty = emptyOutput.Split('\n').First().TrimEnd('\r');
        Assert.Equal(PrCommentWriter.StickyMarker, firstLineEmpty);

        var nonEmpty = PrCommentWriter.Render(new[]
        {
            new AuditIssue { Rule = "r", Severity = "error", Message = "m", ElementId = 1, Category = "Doors" }
        });
        var firstLineNonEmpty = nonEmpty.Split('\n').First().TrimEnd('\r');
        Assert.Equal(PrCommentWriter.StickyMarker, firstLineNonEmpty);
    }

    [Fact]
    public void StickyMarker_IsHtmlComment_NotRenderedToReader()
    {
        // The marker is a sticky-comment lookup token, not user-visible
        // text. Pin its shape so a future "let's add an emoji" change
        // doesn't accidentally make it visible in rendered markdown
        // (and break the bot's substring match in old PR threads).
        Assert.StartsWith("<!--", PrCommentWriter.StickyMarker);
        Assert.EndsWith("-->", PrCommentWriter.StickyMarker);
        Assert.Contains("revitcli", PrCommentWriter.StickyMarker);
    }

    [Fact]
    public void Render_FewIssues_RendersTableHeaderAndRows()
    {
        var issues = new List<AuditIssue>
        {
            new()
            {
                Rule = "naming-mark",
                Severity = "error",
                Message = "Door is missing Mark",
                ElementId = 12345,
                Category = "Doors"
            },
            new()
            {
                Rule = "room-area",
                Severity = "warning",
                Message = "Room area below threshold",
                ElementId = 6789,
                Category = "Rooms"
            }
        };

        var output = PrCommentWriter.Render(issues);

        Assert.Contains("**Summary:** 1 errors, 1 warnings", output);
        Assert.Contains("| Rule | Severity | Element | Message |", output);
        Assert.Contains("| --- | --- | --- | --- |", output);
        Assert.Contains("| naming-mark | error | Doors:12345 | Door is missing Mark |", output);
        Assert.Contains("| room-area | warning | Rooms:6789 | Room area below threshold |", output);
    }

    [Fact]
    public void Render_OverMaxRows_AppendsTruncationFooter()
    {
        var issues = Enumerable.Range(0, 65)
            .Select(i => new AuditIssue
            {
                Rule = $"rule-{i}",
                Severity = "warning",
                Message = $"issue {i}",
                ElementId = i,
                Category = "Walls"
            })
            .ToList();

        var output = PrCommentWriter.Render(issues);

        // 50 rows by default, 65 - 50 = 15 hidden.
        Assert.Contains("_+ 15 more not shown._", output);
        // First and 50th row both present.
        Assert.Contains("| rule-0 |", output);
        Assert.Contains("| rule-49 |", output);
        // 50th index (51st item) NOT shown.
        Assert.DoesNotContain("| rule-50 |", output);
    }

    [Fact]
    public void Render_GroupByCategory_HonoursOption()
    {
        var issues = new List<AuditIssue>
        {
            new()
            {
                Rule = "r1",
                Severity = "error",
                Message = "msg-a",
                ElementId = 1,
                Category = "Doors"
            },
            new()
            {
                Rule = "r2",
                Severity = "warning",
                Message = "msg-b",
                ElementId = 2,
                Category = "Walls"
            },
            new()
            {
                Rule = "r3",
                Severity = "warning",
                Message = "msg-c",
                ElementId = 3,
                Category = "Walls"
            }
        };

        var output = PrCommentWriter.Render(
            issues,
            new PrCommentOptions { GroupByCategory = true });

        Assert.Contains("### Doors", output);
        Assert.Contains("### Walls", output);

        var doorsIdx = output.IndexOf("### Doors", System.StringComparison.Ordinal);
        var wallsIdx = output.IndexOf("### Walls", System.StringComparison.Ordinal);
        Assert.True(doorsIdx >= 0 && wallsIdx >= 0);
        Assert.True(doorsIdx < wallsIdx, "Groups should be ordered alphabetically by category.");
    }

    [Fact]
    public void Render_PipeInMessage_IsEscaped()
    {
        var issues = new List<AuditIssue>
        {
            new()
            {
                Rule = "rule",
                Severity = "warning",
                Message = "value | with pipe",
                ElementId = 1,
                Category = "Doors"
            }
        };

        var output = PrCommentWriter.Render(issues);
        Assert.Contains("value \\| with pipe", output);
    }

    [Fact]
    public void Render_NewlineInMessage_IsCollapsed()
    {
        var issues = new List<AuditIssue>
        {
            new()
            {
                Rule = "rule",
                Severity = "warning",
                Message = "line1\nline2",
                ElementId = 1,
                Category = "Doors"
            }
        };

        var output = PrCommentWriter.Render(issues);
        Assert.Contains("line1 line2", output);
    }

    [Fact]
    public void Render_IssueWithoutElement_RendersDashElementCell()
    {
        var issues = new List<AuditIssue>
        {
            new()
            {
                Rule = "global",
                Severity = "warning",
                Message = "project wide"
            }
        };

        var output = PrCommentWriter.Render(issues);
        Assert.Contains("| global | warning | - | project wide |", output);
    }
}
