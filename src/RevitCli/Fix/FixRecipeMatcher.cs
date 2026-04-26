using System;
using System.Collections.Generic;
using RevitCli.Profile;
using RevitCli.Shared;

namespace RevitCli.Fix;

internal static class FixRecipeMatcher
{
    public static FixRecipeMatch Match(AuditIssue issue, IReadOnlyList<FixRecipe> recipes)
    {
        if (issue is null)
        {
            return FixRecipeMatch.Fail("The issue parameter cannot be null.");
        }

        if (recipes is null)
        {
            return FixRecipeMatch.NoMatch();
        }

        var candidates = new List<(FixRecipe Recipe, int Priority)>();

        foreach (var recipe in recipes)
        {
            if (recipe is null)
            {
                continue;
            }

            var priority = GetPriority(issue, recipe);
            if (priority > 0)
            {
                candidates.Add((recipe, priority));
            }
        }

        if (candidates.Count == 0)
        {
            return FixRecipeMatch.NoMatch();
        }

        var bestPriority = 0;
        foreach (var item in candidates)
        {
            if (item.Priority > bestPriority)
            {
                bestPriority = item.Priority;
            }
        }

        var bestCandidates = new List<FixRecipe>();
        foreach (var item in candidates)
        {
            if (item.Priority == bestPriority)
            {
                bestCandidates.Add(item.Recipe);
            }
        }

        if (bestCandidates.Count != 1)
        {
            return FixRecipeMatch.Fail("Ambiguous fix recipe match.");
        }

        return FixRecipeMatch.Ok(bestCandidates[0], inferred: false);
    }

    private static int GetPriority(AuditIssue issue, FixRecipe recipe)
    {
        if (!MatchesRule(issue, recipe))
        {
            return 0;
        }

        var hasCategoryFilter = !string.IsNullOrWhiteSpace(recipe.Category);
        var hasParameterFilter = !string.IsNullOrWhiteSpace(recipe.Parameter);

        var categoryMatches = !hasCategoryFilter
            || string.Equals(recipe.Category, issue.Category, StringComparison.OrdinalIgnoreCase);
        var parameterMatches = !hasParameterFilter
            || string.Equals(recipe.Parameter, issue.Parameter, StringComparison.OrdinalIgnoreCase);

        if (!categoryMatches || !parameterMatches)
        {
            return 0;
        }

        if (hasCategoryFilter && hasParameterFilter)
        {
            return 5;
        }

        if (hasCategoryFilter)
        {
            return 4;
        }

        if (hasParameterFilter)
        {
            return 3;
        }

        return 2;
    }

    private static bool MatchesRule(AuditIssue issue, FixRecipe recipe)
    {
        if (string.IsNullOrWhiteSpace(recipe.Rule))
        {
            return true;
        }

        return string.Equals(issue.Rule, recipe.Rule, StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class FixRecipeMatch
{
    public bool Success { get; init; }
    public bool HasRecipe { get; init; }
    public FixRecipe? Recipe { get; init; }
    public bool Inferred { get; init; }
    public string Error { get; init; } = "";

    public static FixRecipeMatch Ok(FixRecipe recipe, bool inferred)
    {
        return new FixRecipeMatch
        {
            Success = true,
            HasRecipe = true,
            Recipe = recipe,
            Inferred = inferred
        };
    }

    public static FixRecipeMatch NoMatch()
    {
        return new FixRecipeMatch
        {
            Success = true,
            HasRecipe = false,
            Inferred = false
        };
    }

    public static FixRecipeMatch Fail(string error)
    {
        return new FixRecipeMatch
        {
            Success = false,
            HasRecipe = false,
            Inferred = false,
            Error = error
        };
    }
}
