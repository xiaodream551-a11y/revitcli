using RevitCli.Fix;
using Xunit;

namespace RevitCli.Tests.Fix;

public class FixPlanSafetyTests
{
    [Fact]
    public void ValidateApply_BlocksInferredWithoutFlag()
    {
        var plan = new FixPlan();
        plan.Actions.Add(new FixAction { ElementId = 1, Parameter = "Mark", NewValue = "A", Inferred = true, Confidence = "medium" });

        var result = FixPlanSafety.ValidateApply(plan, yes: true, allowInferred: false, maxChanges: 50);

        Assert.False(result.Success);
        Assert.Contains("allow-inferred", result.Error);
    }

    [Fact]
    public void ValidateApply_BlocksLowConfidenceEvenWithFlag()
    {
        var plan = new FixPlan();
        plan.Actions.Add(new FixAction { ElementId = 1, Parameter = "Mark", NewValue = "A", Inferred = true, Confidence = "low" });

        var result = FixPlanSafety.ValidateApply(plan, yes: true, allowInferred: true, maxChanges: 50);

        Assert.False(result.Success);
        Assert.Contains("low-confidence", result.Error);
    }

    [Fact]
    public void ValidateApply_BlocksMaxChanges()
    {
        var plan = new FixPlan();
        plan.Actions.Add(new FixAction { ElementId = 1, Parameter = "Mark", NewValue = "A" });
        plan.Actions.Add(new FixAction { ElementId = 2, Parameter = "Mark", NewValue = "B" });

        var result = FixPlanSafety.ValidateApply(plan, yes: true, allowInferred: false, maxChanges: 1);

        Assert.False(result.Success);
        Assert.Contains("max", result.Error.ToLowerInvariant());
    }

    [Fact]
    public void ValidateApply_BlocksMissingYes()
    {
        var plan = new FixPlan();
        plan.Actions.Add(new FixAction { ElementId = 1, Parameter = "Mark", NewValue = "A" });

        var result = FixPlanSafety.ValidateApply(plan, yes: false, allowInferred: true, maxChanges: 50);

        Assert.False(result.Success);
        Assert.Contains("--yes", result.Error);
    }

    [Fact]
    public void ValidateApply_BlocksNonPositiveMaxChanges()
    {
        var plan = new FixPlan();
        plan.Actions.Add(new FixAction { ElementId = 1, Parameter = "Mark", NewValue = "A" });

        var result = FixPlanSafety.ValidateApply(plan, yes: true, allowInferred: true, maxChanges: 0);

        Assert.False(result.Success);
        Assert.Contains("max-changes", result.Error);
    }

    [Fact]
    public void ValidateApply_AllowsNullActionEntries()
    {
        var plan = new FixPlan();
        plan.Actions.Add(null!);
        plan.Actions.Add(new FixAction { ElementId = 1, Parameter = "Mark", NewValue = "A" });

        var result = FixPlanSafety.ValidateApply(plan, yes: true, allowInferred: true, maxChanges: 50);

        Assert.True(result.Success);
    }

    [Fact]
    public void ValidateApply_BlocksLowConfidenceCaseInsensitive()
    {
        var plan = new FixPlan();
        plan.Actions.Add(new FixAction { ElementId = 1, Parameter = "Mark", NewValue = "A", Confidence = "Low" });

        var result = FixPlanSafety.ValidateApply(plan, yes: true, allowInferred: true, maxChanges: 50);

        Assert.False(result.Success);
        Assert.Contains("low-confidence", result.Error);
    }
}
