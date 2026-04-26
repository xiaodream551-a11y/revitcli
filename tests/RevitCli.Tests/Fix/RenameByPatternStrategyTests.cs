using RevitCli.Fix.Strategies;
using RevitCli.Profile;
using RevitCli.Shared;
using Xunit;

namespace RevitCli.Tests.Fix;

public class RenameByPatternStrategyTests
{
    [Fact]
    public void Plan_ReplacesCurrentValue()
    {
        var strategy = new RenameByPatternStrategy();
        var issue = new AuditIssue
        {
            Rule = "naming",
            ElementId = 20,
            Category = "rooms",
            Parameter = "Name",
            CurrentValue = "Room Lobby"
        };
        var recipe = new FixRecipe
        {
            Strategy = "renameByPattern",
            Parameter = "Name",
            Match = "^Room (.+)$",
            Replace = "$1"
        };

        var result = strategy.Plan(issue, recipe, inferred: false, confidence: "high");

        Assert.True(result.Success);
        var action = Assert.Single(result.Actions);
        Assert.Equal("Lobby", action.NewValue);
        Assert.Equal("Room Lobby", action.OldValue);
    }

    [Fact]
    public void Plan_NonMatchingValue_Skips()
    {
        var strategy = new RenameByPatternStrategy();
        var result = strategy.Plan(
            new AuditIssue { Rule = "naming", ElementId = 20, Parameter = "Name", CurrentValue = "Lobby" },
            new FixRecipe { Strategy = "renameByPattern", Parameter = "Name", Match = "^Room (.+)$", Replace = "$1" },
            inferred: false,
            confidence: "high");

        Assert.False(result.Success);
        Assert.Contains("does not match", result.Error);
    }
}
