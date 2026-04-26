using System;
using System.Collections.Generic;
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

        if (!JournalMatchesBaseline(journal, baselinePath, out var journalError))
        {
            await output.WriteLineAsync($"Error: {journalError}");
            return 1;
        }

        var actions = journal.Actions?.ToList() ?? new List<FixAction>();

        if (!await TryValidateActionsAsync(actions, output))
        {
            return 1;
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

        if (!dryRun && !await TryValidateCurrentDocumentAsync(client, snapshot, output))
        {
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
        var errorCount = 0;

        foreach (var action in actions)
        {
            ApiResponse<SetResult>? previewResult;
            try
            {
                previewResult = await client.SetParameterAsync(new SetRequest
                {
                    ElementId = action.ElementId,
                    Param = action.Parameter,
                    Value = action.OldValue ?? string.Empty,
                    DryRun = true
                });
            }
            catch (Exception ex)
            {
                errorCount++;
                await output.WriteLineAsync(
                    $"Error: failed to preview rollback for element {action.ElementId}: {ex.Message}");
                continue;
            }

            if (previewResult == null || !previewResult.Success || previewResult.Data == null)
            {
                errorCount++;
                await output.WriteLineAsync(
                    $"Error: failed to preview rollback for element {action.ElementId}: {previewResult?.Error}");
                continue;
            }

            var previewItem = previewResult.Data.Preview?.FirstOrDefault(item => item != null && item.Id == action.ElementId);
            if (previewItem == null)
            {
                errorCount++;
                await output.WriteLineAsync(
                    $"Error: preview response did not include a matching item for element {action.ElementId}.");
                continue;
            }

            var currentValue = previewItem.OldValue ?? string.Empty;
            var expectedValue = action.NewValue ?? string.Empty;

            if (!string.Equals(currentValue, expectedValue, StringComparison.Ordinal))
            {
                conflictCount++;
                await output.WriteLineAsync(
                    $"Conflict: element {action.ElementId} parameter {action.Parameter} changed from \"{expectedValue}\" to \"{currentValue}\"; skipping.");
                continue;
            }

            ApiResponse<SetResult>? applyResult;
            try
            {
                applyResult = await client.SetParameterAsync(new SetRequest
                {
                    ElementId = action.ElementId,
                    Param = action.Parameter,
                    Value = action.OldValue ?? string.Empty,
                    DryRun = false
                });
            }
            catch (Exception ex)
            {
                errorCount++;
                await output.WriteLineAsync(
                    $"Error: failed to apply rollback for element {action.ElementId}: {ex.Message}");
                continue;
            }

            if (applyResult == null || !applyResult.Success || applyResult.Data == null)
            {
                errorCount++;
                await output.WriteLineAsync(
                    $"Error: failed to apply rollback for element {action.ElementId}: {applyResult?.Error}");
                continue;
            }

            restoredCount += applyResult.Data.Affected;
        }

        await output.WriteLineAsync(
            $"Restored {restoredCount} element parameter(s); {conflictCount} conflict(s); {errorCount} error(s).");
        return errorCount == 0 ? 0 : 1;
    }

    private static bool JournalMatchesBaseline(FixJournal journal, string baselinePath, out string error)
    {
        error = "";
        if (journal == null)
        {
            error = "invalid fix journal.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(journal.BaselinePath))
        {
            error = "fix journal does not identify its baseline path.";
            return false;
        }

        try
        {
            var expected = Path.GetFullPath(baselinePath);
            if (!GetBaselinePathCandidates(journal.BaselinePath, expected)
                .Any(actual => string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase)))
            {
                error = $"fix journal baseline path '{journal.BaselinePath}' does not match '{baselinePath}'.";
                return false;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"invalid fix journal baseline path: {ex.Message}";
            return false;
        }

        return true;
    }

    private static IEnumerable<string> GetBaselinePathCandidates(string recordedBaselinePath, string expectedBaselinePath)
    {
        var candidates = new List<string>();
        if (Path.IsPathRooted(recordedBaselinePath))
        {
            candidates.Add(Path.GetFullPath(recordedBaselinePath));
            return candidates;
        }

        candidates.Add(Path.GetFullPath(recordedBaselinePath));

        var expectedDirectory = Path.GetDirectoryName(expectedBaselinePath);
        if (!string.IsNullOrWhiteSpace(expectedDirectory))
        {
            candidates.Add(Path.GetFullPath(Path.Combine(expectedDirectory, recordedBaselinePath)));

            var expectedParent = Directory.GetParent(expectedDirectory);
            if (expectedParent != null)
            {
                candidates.Add(Path.GetFullPath(Path.Combine(expectedParent.FullName, recordedBaselinePath)));
            }
        }

        return candidates;
    }

    private static async Task<bool> TryValidateCurrentDocumentAsync(
        RevitClient client,
        ModelSnapshot snapshot,
        TextWriter output)
    {
        ApiResponse<StatusInfo>? status;
        try
        {
            status = await client.GetStatusAsync();
        }
        catch (Exception ex)
        {
            await output.WriteLineAsync($"Error: failed to validate current document: {ex.Message}");
            return false;
        }

        if (status == null || !status.Success || status.Data == null)
        {
            await output.WriteLineAsync($"Error: failed to validate current document: {status?.Error}");
            return false;
        }

        var baselineDocumentPath = snapshot.Revit?.DocumentPath;
        var currentDocumentPath = status.Data.DocumentPath;
        if (!string.IsNullOrWhiteSpace(baselineDocumentPath))
        {
            if (string.IsNullOrWhiteSpace(currentDocumentPath))
            {
                await output.WriteLineAsync(
                    $"Error: current document path is empty; expected baseline document '{baselineDocumentPath}'.");
                return false;
            }

            if (!DocumentPathsEqual(baselineDocumentPath, currentDocumentPath))
            {
                await output.WriteLineAsync(
                    $"Error: current document '{currentDocumentPath}' does not match baseline document '{baselineDocumentPath}'.");
                return false;
            }

            return true;
        }

        var baselineDocument = snapshot.Revit?.Document;
        var currentDocument = status.Data.DocumentName;
        if (!string.IsNullOrWhiteSpace(baselineDocument))
        {
            if (!string.Equals(baselineDocument.Trim(), currentDocument?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                await output.WriteLineAsync(
                    $"Error: current document '{currentDocument}' does not match baseline document '{baselineDocument}'.");
                return false;
            }

            return true;
        }

        await output.WriteLineAsync("Error: baseline snapshot does not include a document identity.");
        return false;
    }

    private static bool DocumentPathsEqual(string expected, string actual)
    {
        return string.Equals(
            NormalizeDocumentPath(expected),
            NormalizeDocumentPath(actual),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeDocumentPath(string path)
    {
        var trimmed = path.Trim();
        try
        {
            if (Path.IsPathRooted(trimmed))
            {
                trimmed = Path.GetFullPath(trimmed);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Fall back to the raw value; invalid paths will still compare unequal.
        }

        return trimmed.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static async Task<bool> TryValidateActionsAsync(IReadOnlyList<FixAction> actions, TextWriter output)
    {
        for (var i = 0; i < actions.Count; i++)
        {
            var action = actions[i];
            if (action == null)
            {
                await output.WriteLineAsync($"Error: invalid rollback journal action at index {i}: entry is null.");
                return false;
            }

            if (action.ElementId <= 0 || string.IsNullOrWhiteSpace(action.Parameter) || action.NewValue == null)
            {
                var issues = new List<string>();
                if (action.ElementId <= 0)
                {
                    issues.Add("ElementId must be > 0");
                }

                if (string.IsNullOrWhiteSpace(action.Parameter))
                {
                    issues.Add("Parameter is required");
                }

                if (action.NewValue == null)
                {
                    issues.Add("NewValue is required");
                }

                await output.WriteLineAsync(
                    $"Error: invalid rollback journal action at index {i}: {string.Join(", ", issues)}.");
                return false;
            }
        }

        return true;
    }
}
