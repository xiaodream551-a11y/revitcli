using System.Collections.Generic;
using RevitCli.Fix;
using RevitCli.Profile;
using RevitCli.Shared;
using Xunit;

namespace RevitCli.Tests.Fix;

public class FixPlannerTests
{
    [Fact]
    public void Plan_ExplicitRecipe_CreatesAction()
    {
        var issues = new List<AuditIssue>
        {
            new()
            {
                Rule = "required-parameter",
                Severity = "warning",
                ElementId = 1,
                Category = "doors",
                Parameter = "Mark",
                CurrentValue = "",
                Source = "structured"
            }
        };
        var profile = new ProjectProfile
        {
            Fixes = new List<FixRecipe>
            {
                new() { Rule = "required-parameter", Category = "doors", Parameter = "Mark", Strategy = "setParam", Value = "D-{element.id}" }
            }
        };

        var plan = FixPlanner.Plan("default", issues, profile, new FixPlanOptions());

        var action = Assert.Single(plan.Actions);
        Assert.Equal("D-1", action.NewValue);
        Assert.Equal("high", action.Confidence);
    }

    [Fact]
    public void Plan_SeverityFilter_SkipsNonMatchingSeverity()
    {
        var issues = new List<AuditIssue>
        {
            new() { Rule = "required-parameter", Severity = "info", ElementId = 1, Category = "doors", Parameter = "Mark" }
        };

        var plan = FixPlanner.Plan(
            "default",
            issues,
            new ProjectProfile(),
            new FixPlanOptions { Severity = "error" });

        Assert.Empty(plan.Actions);
        Assert.Single(plan.Skipped);
    }

    [Fact]
    public void Plan_InferExpectedValue_MediumConfidenceForStructuredIssue()
    {
        var issues = new List<AuditIssue>
        {
            new()
            {
                Rule = "required-parameter",
                Severity = "warning",
                ElementId = 7,
                Category = "doors",
                Parameter = "Mark",
                ExpectedValue = "D-7",
                Source = "structured",
                CurrentValue = "X"
            }
        };

        var plan = FixPlanner.Plan("default", issues, new ProjectProfile(), new FixPlanOptions());

        var action = Assert.Single(plan.Actions);
        Assert.Equal("D-7", action.NewValue);
        Assert.Equal("medium", action.Confidence);
    }

    [Fact]
    public void Plan_UnsupportedStrategy_IsSkippedWithWarning()
    {
        var issues = new List<AuditIssue>
        {
            new()
            {
                Rule = "required-parameter",
                Severity = "warning",
                ElementId = 1,
                Category = "doors",
                Parameter = "Mark",
                CurrentValue = ""
            }
        };
        var profile = new ProjectProfile
        {
            Fixes = new List<FixRecipe>
            {
                new()
                {
                    Rule = "required-parameter",
                    Category = "doors",
                    Parameter = "Mark",
                    Strategy = "unsupported",
                    Value = "X"
                }
            }
        };

        var plan = FixPlanner.Plan("default", issues, profile, new FixPlanOptions());

        Assert.Empty(plan.Actions);
        var skipped = Assert.Single(plan.Skipped);
        var warning = Assert.Single(plan.Warnings);
        Assert.Equal("Unsupported strategy 'unsupported'.", warning);
        Assert.Contains("Unsupported strategy", skipped.Reason);
    }

    [Fact]
    public void Plan_RecipeMaxChanges_SkipsIssuesBeyondRecipeLimit()
    {
        var issues = new List<AuditIssue>
        {
            new() { Rule = "required-parameter", Severity = "warning", ElementId = 1, Category = "doors", Parameter = "Mark", CurrentValue = "" },
            new() { Rule = "required-parameter", Severity = "warning", ElementId = 2, Category = "doors", Parameter = "Mark", CurrentValue = "" }
        };
        var profile = new ProjectProfile
        {
            Fixes = new List<FixRecipe>
            {
                new()
                {
                    Rule = "required-parameter",
                    Category = "doors",
                    Parameter = "Mark",
                    Strategy = "setParam",
                    Value = "D-{element.id}",
                    MaxChanges = 1
                }
            }
        };

        var plan = FixPlanner.Plan("default", issues, profile, new FixPlanOptions());

        var action = Assert.Single(plan.Actions);
        Assert.Equal(1, action.ElementId);
        Assert.Equal(1, action.RecipeMaxChanges);
        var skipped = Assert.Single(plan.Skipped);
        Assert.Equal(2, skipped.ElementId);
        Assert.Contains("maxChanges", skipped.Reason);
    }
}
