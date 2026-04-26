using System.Text.RegularExpressions;
using RevitCli.Profile;
using RevitCli.Shared;

namespace RevitCli.Fix.Strategies;

internal sealed class RenameByPatternStrategy : IFixStrategy
{
    public string Name => "renameByPattern";

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

        if (issue.CurrentValue is null)
        {
            return FixStrategyPlanResult.Skip("No current value to rename.");
        }

        if (string.IsNullOrWhiteSpace(recipe.Match))
        {
            return FixStrategyPlanResult.Skip("No match pattern.");
        }

        if (recipe.Replace is null)
        {
            return FixStrategyPlanResult.Skip("No replace pattern.");
        }

        string newValue;
        try
        {
            if (!Regex.IsMatch(issue.CurrentValue, recipe.Match))
            {
                return FixStrategyPlanResult.Skip($"Current value '{issue.CurrentValue}' does not match pattern.");
            }

            newValue = Regex.Replace(issue.CurrentValue, recipe.Match, recipe.Replace);
            if (newValue == issue.CurrentValue)
            {
                return FixStrategyPlanResult.Skip("Replacement does not change current value.");
            }
        }
        catch (ArgumentException ex)
        {
            return FixStrategyPlanResult.Skip(ex.Message);
        }

        var action = new FixAction
        {
            Rule = issue.Rule,
            Strategy = Name,
            ElementId = issue.ElementId.Value,
            Category = recipe.Category ?? issue.Category ?? string.Empty,
            Parameter = parameter,
            OldValue = issue.CurrentValue,
            NewValue = newValue,
            Inferred = inferred,
            Confidence = confidence,
            Reason = inferred ? "inferred" : "explicit",
            RecipeKey = FixRecipeIdentity.Create(recipe),
            RecipeMaxChanges = recipe.MaxChanges
        };

        return FixStrategyPlanResult.Ok(action);
    }
}
