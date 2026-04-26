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
}
