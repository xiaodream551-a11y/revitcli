using System.Collections.Generic;
using RevitCli.Profile;
using RevitCli.Shared;

namespace RevitCli.Fix.Strategies;

internal interface IFixStrategy
{
    string Name { get; }
    FixStrategyPlanResult Plan(AuditIssue issue, FixRecipe recipe, bool inferred, string confidence);
}

internal sealed class FixStrategyPlanResult
{
    public bool Success { get; init; }
    public List<FixAction> Actions { get; init; } = new();
    public string Error { get; init; } = "";

    public static FixStrategyPlanResult Ok(FixAction action) => new()
    {
        Success = true,
        Actions = new List<FixAction> { action }
    };

    public static FixStrategyPlanResult Skip(string error) => new()
    {
        Success = false,
        Error = error
    };
}
