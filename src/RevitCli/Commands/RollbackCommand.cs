using System;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using RevitCli.Client;
using RevitCli.Fix;
using RevitCli.Shared;

namespace RevitCli.Commands;

public static class RollbackCommand
{
    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    public static Command Create(RevitClient client)
    {
        var baselineArg = new Argument<string>("baseline", "Baseline snapshot path written by fix --apply");
        var dryRunOpt = new Option<bool>("--dry-run", "Preview rollback without applying");
        var yesOpt = new Option<bool>("--yes", "Confirm rollback apply in non-interactive mode");
        var maxChangesOpt = new Option<int>("--max-changes", () => 50, "Maximum number of rollback writes");

        var command = new Command("rollback", "Restore parameters changed by a fix baseline")
        {
            baselineArg,
            dryRunOpt,
            yesOpt,
            maxChangesOpt
        };

        command.SetHandler(async (baselinePath, dryRun, yes, maxChanges) =>
        {
            Environment.ExitCode = await ExecuteAsync(
                client, baselinePath, dryRun, yes, maxChanges, Console.Out);
        }, baselineArg, dryRunOpt, yesOpt, maxChangesOpt);

        return command;
    }

    public static async Task<int> ExecuteAsync(
        RevitClient client,
        string baselinePath,
        bool dryRun,
        bool yes,
        int maxChanges,
        TextWriter output)
    {
        if (string.IsNullOrWhiteSpace(baselinePath))
        {
            await output.WriteLineAsync("Error: baseline path is required.");
            return 1;
        }

        if (maxChanges <= 0)
        {
            await output.WriteLineAsync("Error: --max-changes must be greater than 0.");
            return 1;
        }

        if (!File.Exists(baselinePath))
        {
            await output.WriteLineAsync($"Error: baseline file not found: {baselinePath}");
            return 1;
        }

        ModelSnapshot? snapshot;
        try
        {
            var json = await File.ReadAllTextAsync(baselinePath);
            snapshot = JsonSerializer.Deserialize<ModelSnapshot>(json, ReadOptions);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            await output.WriteLineAsync($"Error: failed to parse baseline snapshot: {ex.Message}");
            return 1;
        }

        if (snapshot == null)
        {
            await output.WriteLineAsync($"Error: invalid baseline snapshot: {baselinePath}");
            return 1;
        }

        FixJournal journal;
        try
        {
            journal = FixJournalStore.LoadForBaseline(baselinePath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            await output.WriteLineAsync($"Error: {ex.Message}");
            return 1;
        }

        var actions = new System.Collections.Generic.List<FixAction>();
        if (journal.Actions != null)
        {
            foreach (var action in journal.Actions)
            {
                if (action != null)
                {
                    actions.Add(action);
                }
            }
        }

        if (actions.Count > maxChanges)
        {
            await output.WriteLineAsync($"Error: rollback journal has {actions.Count} action(s), exceeds --max-changes {maxChanges}.");
            return 1;
        }

        if (!dryRun && !yes)
        {
            await output.WriteLineAsync("Error: use --yes to apply rollback changes.");
            return 1;
        }

        foreach (var action in actions)
        {
            await output.WriteLineAsync(
                $"[{action.ElementId}] {action.Parameter}: \"{action.NewValue ?? string.Empty}\" -> \"{action.OldValue ?? string.Empty}\"");
        }

        if (dryRun)
        {
            await output.WriteLineAsync($"Dry run: {actions.Count} rollback action(s).");
            return 0;
        }

        var restoredCount = 0;
        var conflictCount = 0;

        foreach (var action in actions)
        {
            var previewResult = await client.SetParameterAsync(new SetRequest
            {
                ElementId = action.ElementId,
                Param = action.Parameter,
                Value = action.OldValue ?? string.Empty,
                DryRun = true
            });

            if (!previewResult.Success || previewResult.Data == null)
            {
                await output.WriteLineAsync(
                    $"Error: failed to preview rollback for element {action.ElementId}: {previewResult.Error}");
                return 1;
            }

            var previewItem = previewResult.Data.Preview?.FirstOrDefault();
            var currentValue = previewItem?.OldValue ?? string.Empty;
            var expectedValue = action.NewValue ?? string.Empty;

            if (!string.Equals(currentValue, expectedValue, StringComparison.Ordinal))
            {
                conflictCount++;
                await output.WriteLineAsync(
                    $"Conflict: element {action.ElementId} parameter {action.Parameter} changed from \"{expectedValue}\" to \"{currentValue}\"; skipping.");
                continue;
            }

            var applyResult = await client.SetParameterAsync(new SetRequest
            {
                ElementId = action.ElementId,
                Param = action.Parameter,
                Value = action.OldValue ?? string.Empty,
                DryRun = false
            });

            if (!applyResult.Success || applyResult.Data == null)
            {
                await output.WriteLineAsync(
                    $"Error: failed to apply rollback for element {action.ElementId}: {applyResult.Error}");
                return 1;
            }

            restoredCount += applyResult.Data.Affected;
        }

        await output.WriteLineAsync($"Restored {restoredCount} rollback action(s); {conflictCount} conflict(s).");
        return 0;
    }
}
