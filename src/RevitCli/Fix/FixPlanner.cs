using System;
using System.Collections.Generic;
using RevitCli.Fix.Strategies;
using RevitCli.Profile;
using RevitCli.Shared;

namespace RevitCli.Fix;

internal static class FixPlanner
{
    private static readonly Dictionary<string, IFixStrategy> Strategies =
        new(StringComparer.OrdinalIgnoreCase)
        {
            { "setParam", new SetParamStrategy() },
            { "renameByPattern", new RenameByPatternStrategy() }
        };

    public static FixPlan Plan(string checkName, IReadOnlyList<AuditIssue> issues, ProjectProfile profile, FixPlanOptions options)
    {
        var plan = new FixPlan { CheckName = checkName };

        if (issues is null || issues.Count == 0)
        {
            return plan;
        }

        if (options is null)
        {
            options = new FixPlanOptions();
        }

        var recipeActionCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var issue in issues)
        {
            if (issue is null)
            {
                AddSkipped(plan, string.Empty, string.Empty, null, "Issue is null.");
                continue;
            }

            if (options.Rules is { Count: > 0 } && !options.Rules.Contains(issue.Rule))
            {
                AddSkipped(plan, issue.Rule, issue.Severity, issue.ElementId, "Filtered by --rule.");
                continue;
            }

            if (!string.IsNullOrWhiteSpace(options.Severity)
                && !string.Equals(options.Severity, issue.Severity, StringComparison.OrdinalIgnoreCase))
            {
                AddSkipped(plan, issue.Rule, issue.Severity, issue.ElementId, "Filtered by --severity.");
                continue;
            }

            var recipes = profile?.Fixes ?? new List<FixRecipe>();
            var match = FixRecipeMatcher.Match(issue, recipes);
            if (!match.Success)
            {
                plan.Warnings.Add($"Could not resolve fix recipe for rule '{issue.Rule}'. {match.Error}");
                AddSkipped(plan, issue.Rule, issue.Severity, issue.ElementId, match.Error);
                continue;
            }

            var recipe = match.Recipe;
            var inferred = match.Recipe is null;
            if (!inferred)
            {
                if (recipe is null)
                {
                    AddSkipped(plan, issue.Rule, issue.Severity, issue.ElementId, "No matching fix recipe and no safe inference.");
                    continue;
                }
            }
            else
            {
                recipe = InferRecipe(issue);
                if (recipe is null)
                {
                    AddSkipped(plan, issue.Rule, issue.Severity, issue.ElementId, "No matching fix recipe and no safe inference.");
                    continue;
                }
            }

            if (!Strategies.TryGetValue(recipe.Strategy, out var strategy))
            {
                var reason = $"Unsupported strategy '{recipe.Strategy}'.";
                plan.Warnings.Add(reason);
                AddSkipped(plan, issue.Rule, issue.Severity, issue.ElementId, reason);
                continue;
            }

            var recipeKey = FixRecipeIdentity.Create(recipe);
            if (recipe.MaxChanges.HasValue
                && recipeActionCounts.TryGetValue(recipeKey, out var existingRecipeCount)
                && existingRecipeCount >= recipe.MaxChanges.Value)
            {
                AddSkipped(
                    plan,
                    issue.Rule,
                    issue.Severity,
                    issue.ElementId,
                    $"Recipe maxChanges {recipe.MaxChanges.Value} reached.");
                continue;
            }

            var confidence = inferred &&
                string.Equals(issue.Source, "structured", StringComparison.OrdinalIgnoreCase)
                    ? "medium"
                    : "low";
            if (!inferred)
            {
                confidence = "high";
            }

            var strategyResult = strategy.Plan(issue, recipe, inferred, confidence);
            if (!strategyResult.Success)
            {
                AddSkipped(plan, issue.Rule, issue.Severity, issue.ElementId, strategyResult.Error);
                continue;
            }

            var actions = (strategyResult.Actions ?? new List<FixAction>()).Where(a => a is not null).ToList();
            if (recipe.MaxChanges.HasValue)
            {
                recipeActionCounts.TryGetValue(recipeKey, out var currentRecipeCount);
                var remaining = recipe.MaxChanges.Value - currentRecipeCount;
                if (remaining <= 0)
                {
                    AddSkipped(
                        plan,
                        issue.Rule,
                        issue.Severity,
                        issue.ElementId,
                        $"Recipe maxChanges {recipe.MaxChanges.Value} reached.");
                    continue;
                }

                if (actions.Count > remaining)
                {
                    plan.Actions.AddRange(actions.Take(remaining));
                    recipeActionCounts[recipeKey] = currentRecipeCount + remaining;
                    AddSkipped(
                        plan,
                        issue.Rule,
                        issue.Severity,
                        issue.ElementId,
                        $"Recipe maxChanges {recipe.MaxChanges.Value} reached.");
                    continue;
                }

                recipeActionCounts[recipeKey] = currentRecipeCount + actions.Count;
            }

            plan.Actions.AddRange(actions);
        }

        return plan;
    }

    public static FixRecipe? InferRecipe(AuditIssue issue)
    {
        if (issue is null)
        {
            return null;
        }

        if (issue.ElementId is null || string.IsNullOrWhiteSpace(issue.Parameter))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(issue.ExpectedValue))
        {
            return new FixRecipe
            {
                Rule = issue.Rule,
                Category = issue.Category,
                Parameter = issue.Parameter,
                Strategy = "setParam",
                Value = "{expectedValue}"
            };
        }

        if (string.Equals(issue.Rule, "required-parameter", StringComparison.OrdinalIgnoreCase))
        {
            return new FixRecipe
            {
                Rule = issue.Rule,
                Category = issue.Category,
                Parameter = issue.Parameter,
                Strategy = "setParam",
                Value = "{category}-{element.id}"
            };
        }

        return null;
    }

    private static void AddSkipped(FixPlan plan, string rule, string severity, long? elementId, string reason)
    {
        plan.Skipped.Add(new FixSkippedIssue
        {
            Rule = rule,
            Severity = severity,
            ElementId = elementId,
            Reason = reason
        });
    }
}
