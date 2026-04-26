namespace RevitCli.Fix;

internal static class FixPlanSafety
{
    public static FixSafetyResult ValidateApply(FixPlan? plan, bool yes, bool allowInferred, int maxChanges)
    {
        if (!yes)
        {
            return FixSafetyResult.Fail("Apply requires --yes in non-interactive mode.");
        }

        if (maxChanges <= 0)
        {
            return FixSafetyResult.Fail("--max-changes must be greater than 0.");
        }

        var actions = plan?.Actions.Where(a => a is not null).ToList() ?? [];

        if (actions.Count > maxChanges)
        {
            return FixSafetyResult.Fail($"Planned action count {actions.Count} exceeds --max-changes {maxChanges}.");
        }

        if (actions.Any(a => a.Inferred) && !allowInferred)
        {
            return FixSafetyResult.Fail("Inferred actions require --allow-inferred before apply.");
        }

        if (actions.Any(a => string.Equals(a.Confidence, "low", StringComparison.OrdinalIgnoreCase)))
        {
            return FixSafetyResult.Fail("low-confidence fallback actions are dry-run only in v1.5.");
        }

        return FixSafetyResult.Ok();
    }
}

internal sealed class FixSafetyResult
{
    public bool Success { get; init; }
    public string Error { get; init; } = "";

    public static FixSafetyResult Ok() => new() { Success = true };
    public static FixSafetyResult Fail(string error) => new() { Success = false, Error = error };
}
