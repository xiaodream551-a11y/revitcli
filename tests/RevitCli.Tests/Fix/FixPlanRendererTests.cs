using RevitCli.Fix;
using Xunit;

namespace RevitCli.Tests.Fix;

public class FixPlanRendererTests
{
    [Fact]
    public void Render_UsesDashDashForSkippedElementId()
    {
        var plan = new FixPlan();
        plan.Actions.Add(new FixAction
        {
            Rule = "required-parameter",
            Strategy = "setParam",
            ElementId = 1,
            Category = "doors",
            Parameter = "Mark",
            OldValue = "A",
            NewValue = "B",
            Confidence = "high"
        });
        plan.Skipped.Add(new FixSkippedIssue
        {
            Rule = "required-parameter",
            ElementId = null,
            Reason = "no valid target"
        });

        var output = FixPlanRenderer.Render(plan);

        Assert.Contains("Element --: no valid target", output);
        Assert.Contains("  [setParam] required-parameter Element 1 Mark: \"A\" -> \"B\" (high)", output);
        Assert.Contains("Fix plan for check 'default': 1 action(s), 1 skipped, 0 inferred", output);
    }

    [Fact]
    public void Render_FiltersNullEntries()
    {
        var plan = new FixPlan();
        plan.Actions.Add(null!);
        plan.Actions.Add(new FixAction
        {
            Rule = "required-parameter",
            Strategy = "setParam",
            ElementId = 1,
            Parameter = "Mark",
            OldValue = "A",
            NewValue = "B",
            Confidence = "high"
        });

        plan.Skipped.Add(new FixSkippedIssue
        {
            Rule = "required-parameter",
            ElementId = 1,
            Reason = "missing value"
        });
        plan.Skipped.Add(null!);

        plan.Warnings.Add("existing warning");
        plan.Warnings.Add(null!);

        var output = FixPlanRenderer.Render(plan);

        Assert.Contains("Fix plan for check 'default': 1 action(s), 1 skipped, 0 inferred", output);
        Assert.Contains("  [setParam] required-parameter Element 1 Mark: \"A\" -> \"B\" (high)", output);
        Assert.Contains("  [SKIPPED] required-parameter Element 1: missing value", output);
        Assert.Contains("Warning: existing warning", output);
        Assert.Equal(1, output.Split("Warning:", StringSplitOptions.None).Length - 1);
    }
}
