using RevitCli.Profile;
using RevitCli.Shared;

namespace RevitCli.Fix.Strategies;

internal sealed class SetParamStrategy : IFixStrategy
{
    public string Name => "setParam";

    public FixStrategyPlanResult Plan(AuditIssue issue, FixRecipe recipe, bool inferred, string confidence)
    {
        if (issue is null)
        {
            return FixStrategyPlanResult.Skip("Issue is null.");
        }

        if (recipe is null)
        {
            return FixStrategyPlanResult.Skip("Recipe is null.");
        }

        if (issue.ElementId is null)
        {
            return FixStrategyPlanResult.Skip("Issue has no element id.");
        }

        var parameter = recipe.Parameter ?? issue.Parameter;
        if (string.IsNullOrWhiteSpace(parameter))
        {
            return FixStrategyPlanResult.Skip("No target parameter.");
        }

        if (recipe.Value is null)
        {
            return FixStrategyPlanResult.Skip("setParam recipe has no value.");
        }

        string rendered;
        try
        {
            rendered = FixTemplateRenderer.Render(recipe.Value, issue, parameter);
        }
        catch (InvalidOperationException ex)
        {
            return FixStrategyPlanResult.Skip(ex.Message);
        }
        catch (ArgumentNullException ex)
        {
            return FixStrategyPlanResult.Skip(ex.Message);
        }

        if (string.IsNullOrWhiteSpace(rendered))
        {
            return FixStrategyPlanResult.Skip("Rendered value is empty.");
        }

        if (string.Equals(rendered, issue.CurrentValue, StringComparison.Ordinal))
        {
            return FixStrategyPlanResult.Skip("Rendered value equals current value.");
        }

        var action = new FixAction
        {
            Rule = issue.Rule,
            Strategy = Name,
            ElementId = issue.ElementId.Value,
            Category = recipe.Category ?? issue.Category ?? string.Empty,
            Parameter = parameter,
            OldValue = issue.CurrentValue,
            NewValue = rendered,
            Inferred = inferred,
            Confidence = confidence,
            Reason = inferred ? "inferred" : "explicit"
        };

        return FixStrategyPlanResult.Ok(action);
    }
}
