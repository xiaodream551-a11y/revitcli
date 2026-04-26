using RevitCli.Fix.Strategies;
using RevitCli.Profile;
using RevitCli.Shared;
using Xunit;

namespace RevitCli.Tests.Fix;

public class SetParamStrategyTests
{
    [Fact]
    public void Plan_CreatesHighConfidenceAction()
    {
        var strategy = new SetParamStrategy();
        var issue = new AuditIssue
        {
            Rule = "required-parameter",
            ElementId = 123,
            Category = "doors",
            Parameter = "Mark",
            CurrentValue = ""
        };
        var recipe = new FixRecipe { Strategy = "setParam", Parameter = "Mark", Value = "{category}-{element.id}" };

        var result = strategy.Plan(issue, recipe, inferred: false, confidence: "high");

        Assert.True(result.Success);
        var action = Assert.Single(result.Actions);
        Assert.Equal(123, action.ElementId);
        Assert.Equal("Mark", action.Parameter);
        Assert.Equal("doors-123", action.NewValue);
        Assert.False(action.Inferred);
        Assert.Equal("high", action.Confidence);
    }

    [Fact]
    public void Plan_MissingElementId_Skips()
    {
        var strategy = new SetParamStrategy();
        var result = strategy.Plan(
            new AuditIssue { Rule = "required-parameter", Parameter = "Mark" },
            new FixRecipe { Strategy = "setParam", Parameter = "Mark", Value = "X" },
            inferred: false,
            confidence: "high");

        Assert.False(result.Success);
        Assert.Contains("element id", result.Error.ToLowerInvariant());
    }

    [Fact]
    public void Plan_NullRecipe_Skips()
    {
        var strategy = new SetParamStrategy();
        var result = strategy.Plan(
            new AuditIssue { Rule = "required-parameter", ElementId = 1, Parameter = "Mark" },
            null!,
            inferred: false,
            confidence: "high");

        Assert.False(result.Success);
        Assert.Contains("recipe is null", result.Error.ToLowerInvariant());
    }

    [Fact]
    public void Plan_NoChange_Skips()
    {
        var strategy = new SetParamStrategy();
        var issue = new AuditIssue
        {
            Rule = "required-parameter",
            ElementId = 123,
            Category = "doors",
            Parameter = "Mark",
            CurrentValue = "D-123"
        };
        var recipe = new FixRecipe { Strategy = "setParam", Parameter = "Mark", Value = "D-123" };

        var result = strategy.Plan(issue, recipe, inferred: false, confidence: "high");

        Assert.False(result.Success);
        Assert.Contains("equals current value", result.Error);
    }
}
