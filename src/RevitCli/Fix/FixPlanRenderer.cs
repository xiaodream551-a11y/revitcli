using System.Linq;
using System.Text;

namespace RevitCli.Fix;

internal static class FixPlanRenderer
{
    public static string Render(FixPlan? plan)
    {
        plan ??= new FixPlan();

        var actions = plan.Actions;
        var skipped = plan.Skipped;
        var warnings = plan.Warnings;
        var inferred = actions.Count(a => a.Inferred);

        var builder = new StringBuilder();
        builder.AppendLine($"Fix plan for check '{plan.CheckName}': {actions.Count} action(s), {skipped.Count} skipped, {inferred} inferred");

        foreach (var action in actions)
        {
            var oldValue = action.OldValue ?? string.Empty;
            builder.AppendLine($"  [{action.Strategy}] {action.Rule} Element {action.ElementId} {action.Parameter}: \"{oldValue}\" -> \"{action.NewValue}\" ({action.Confidence})");
        }

        foreach (var skippedIssue in skipped)
        {
            var id = skippedIssue.ElementId?.ToString() ?? "-";
            builder.AppendLine($"  [SKIPPED] {skippedIssue.Rule} Element {id}: {skippedIssue.Reason}");
        }

        foreach (var warning in warnings)
        {
            builder.AppendLine($"Warning: {warning}");
        }

        if (inferred > 0)
        {
            builder.AppendLine("Warning: inferred actions require --allow-inferred before apply.");
        }

        return builder.ToString().TrimEnd();
    }
}
