using System;
using RevitCli.Fix;
using RevitCli.Shared;
using Xunit;

namespace RevitCli.Tests.Fix;

public class FixTemplateRendererTests
{
    [Fact]
    public void Render_ReplacesSupportedTokens()
    {
        var issue = new AuditIssue
        {
            ElementId = 123,
            Category = "doors",
            Parameter = "Mark",
            CurrentValue = "",
            ExpectedValue = "D-123"
        };

        var rendered = FixTemplateRenderer.Render("{category}-{element.id}-{parameter}-{expectedValue}", issue, "Mark");

        Assert.Equal("doors-123-Mark-D-123", rendered);
    }

    [Fact]
    public void Render_UnknownToken_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            FixTemplateRenderer.Render("{unknown}", new AuditIssue { ElementId = 1 }, "Mark"));

        Assert.Contains("unknown", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Render_NullTemplate_ThrowsArgumentNull()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            FixTemplateRenderer.Render(null!, new AuditIssue { ElementId = 1 }, "Mark"));

        Assert.Equal("template", ex.ParamName);
    }

    [Fact]
    public void Render_NullIssue_ThrowsArgumentNull()
    {
        var ex = Assert.Throws<ArgumentNullException>(() =>
            FixTemplateRenderer.Render("{category}", null!, "Mark"));

        Assert.Equal("issue", ex.ParamName);
    }
}
