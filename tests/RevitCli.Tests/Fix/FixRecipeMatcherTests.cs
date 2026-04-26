using System;
using System.Collections.Generic;
using RevitCli.Fix;
using RevitCli.Profile;
using RevitCli.Shared;
using Xunit;

namespace RevitCli.Tests.Fix;

public class FixRecipeMatcherTests
{
    [Fact]
    public void Match_PrefersRuleCategoryParameter()
    {
        var issue = new AuditIssue
        {
            Rule = "required-parameter",
            Category = "doors",
            Parameter = "Mark",
            ElementId = 10
        };
        var recipes = new List<FixRecipe>
        {
            new() { Rule = "required-parameter", Strategy = "setParam", Parameter = "Comments", Value = "broad" },
            new() { Rule = "required-parameter", Category = "doors", Strategy = "setParam", Value = "category" },
            new() { Rule = "required-parameter", Category = "doors", Parameter = "Mark", Strategy = "setParam", Value = "exact" }
        };

        var match = FixRecipeMatcher.Match(issue, recipes);

        Assert.True(match.Success);
        Assert.Equal("exact", match.Recipe!.Value);
        Assert.False(match.Inferred);
    }

    [Fact]
    public void Match_DuplicateSamePriority_ReturnsAmbiguousFailure()
    {
        var issue = new AuditIssue { Rule = "naming", Category = "rooms", Parameter = "Name", ElementId = 20 };
        var recipes = new List<FixRecipe>
        {
            new() { Rule = "naming", Category = "rooms", Parameter = "Name", Strategy = "renameByPattern", Match = "^A$", Replace = "B" },
            new() { Rule = "naming", Category = "rooms", Parameter = "Name", Strategy = "renameByPattern", Match = "^C$", Replace = "D" }
        };

        var match = FixRecipeMatcher.Match(issue, recipes);

        Assert.False(match.Success);
        Assert.Contains("ambiguous", match.Error, StringComparison.OrdinalIgnoreCase);
    }
}
