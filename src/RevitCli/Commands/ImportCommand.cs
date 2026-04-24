using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using RevitCli.Client;
using RevitCli.Output;
using RevitCli.Shared;
using Spectre.Console;

namespace RevitCli.Commands;

public static class ImportCommand
{
    public static Command Create(RevitClient client)
    {
        var fileArg = new Argument<string>("file", "Path to CSV file");
        var categoryOpt = new Option<string>("--category", "Revit category (walls, doors, 墙, 门, etc.)") { IsRequired = true };
        var matchByOpt = new Option<string>("--match-by", "Parameter name linking CSV row to Revit element (e.g. Mark)") { IsRequired = true };
        var mapOpt = new Option<string?>("--map", "Explicit column→param mapping (e.g. \"col:Param,col2:Param2\")");
        var dryRunOpt = new Option<bool>("--dry-run", "Preview changes without committing");
        var onMissingOpt = new Option<string>("--on-missing", () => "warn", "error|warn|skip — when CSV row has no Revit match");
        var onDuplicateOpt = new Option<string>("--on-duplicate", () => "error", "error|first|all — when Revit has multiple matches");
        var encodingOpt = new Option<string>("--encoding", () => "auto", "utf-8|gbk|auto");
        var batchSizeOpt = new Option<int>("--batch-size", () => 100, "Max ElementIds per SetRequest (1..1000)");

        var command = new Command("import", "Batch-write Revit element parameters from a CSV file")
        {
            fileArg, categoryOpt, matchByOpt, mapOpt, dryRunOpt,
            onMissingOpt, onDuplicateOpt, encodingOpt, batchSizeOpt
        };

        command.SetHandler(async ctx =>
        {
            var file      = ctx.ParseResult.GetValueForArgument(fileArg);
            var category  = ctx.ParseResult.GetValueForOption(categoryOpt)!;
            var matchBy   = ctx.ParseResult.GetValueForOption(matchByOpt)!;
            var map       = ctx.ParseResult.GetValueForOption(mapOpt);
            var dryRun    = ctx.ParseResult.GetValueForOption(dryRunOpt);
            var onMissing = ctx.ParseResult.GetValueForOption(onMissingOpt)!;
            var onDup     = ctx.ParseResult.GetValueForOption(onDuplicateOpt)!;
            var encoding  = ctx.ParseResult.GetValueForOption(encodingOpt)!;
            var batchSize = ctx.ParseResult.GetValueForOption(batchSizeOpt);
            ctx.ExitCode = await ExecuteAsync(
                client, file, category, matchBy, map, dryRun, onMissing, onDup, encoding, batchSize, Console.Out);
        });

        return command;
    }

    public static async Task<int> ExecuteAsync(
        RevitClient client,
        string file,
        string category,
        string matchBy,
        string? rawMap,
        bool dryRun,
        string onMissing,
        string onDuplicate,
        string encodingHint,
        int batchSize,
        TextWriter output)
    {
        if (!ValidatePolicies(onMissing, onDuplicate, batchSize, output))
            return 1;

        if (!File.Exists(file))
        {
            await output.WriteLineAsync($"Error: CSV file not found: {file}");
            return 1;
        }

        CsvData csv;
        try
        {
            csv = CsvParser.ParseFile(file, encodingHint);
        }
        catch (Exception ex)
        {
            await output.WriteLineAsync($"Error: failed to parse CSV: {ex.Message}");
            return 1;
        }

        if (csv.Rows.Count == 0)
        {
            await output.WriteLineAsync($"No rows to import (encoding={csv.EncodingName}, headers={csv.Headers.Count}).");
            return 0;
        }

        Dictionary<string, string> mapping;
        try
        {
            mapping = CsvMapping.Build(rawMap, csv.Headers, matchBy);
        }
        catch (InvalidOperationException ex)
        {
            await output.WriteLineAsync($"Error: {ex.Message}");
            return 1;
        }

        if (mapping.Count == 0)
        {
            await output.WriteLineAsync(
                $"Error: no writable columns. CSV has only the --match-by column '{matchBy}' " +
                "or all other columns are excluded by --map.");
            return 1;
        }

        var query = await client.QueryElementsAsync(category, filter: null);
        if (!query.Success)
        {
            await output.WriteLineAsync($"Error: {query.Error}");
            return 1;
        }

        var plan = ImportPlanner.Plan(csv, query.Data!, mapping, matchBy, onMissing, onDuplicate);

        await EmitPlanSummary(plan, csv, mapping, matchBy, dryRun, output);

        if (HasFatalPlanIssue(plan, onMissing, onDuplicate))
            return 1;

        if (dryRun || plan.Groups.Count == 0)
            return 0;

        var (totalAffected, failures) = await ApplyPlan(client, plan, batchSize, output);

        await output.WriteLineAsync($"Modified {totalAffected} element-parameter pair(s) across {plan.Groups.Count} group(s).");
        if (failures.Count > 0)
        {
            await output.WriteLineAsync($"Failed: {failures.Count} group(s):");
            foreach (var (g, msg) in failures)
                await output.WriteLineAsync($"  - {g.Param}={g.Value} (ids={string.Join(",", g.ElementIds)}): {msg}");
            return 2;
        }
        return 0;
    }

    private static bool ValidatePolicies(string onMissing, string onDuplicate, int batchSize, TextWriter output)
    {
        if (onMissing != "error" && onMissing != "warn" && onMissing != "skip")
        {
            output.WriteLine($"Error: --on-missing must be one of: error, warn, skip (got '{onMissing}').");
            return false;
        }
        if (onDuplicate != "error" && onDuplicate != "first" && onDuplicate != "all")
        {
            output.WriteLine($"Error: --on-duplicate must be one of: error, first, all (got '{onDuplicate}').");
            return false;
        }
        if (batchSize < 1 || batchSize > 1000)
        {
            output.WriteLine($"Error: --batch-size must be between 1 and 1000 (got {batchSize}).");
            return false;
        }
        return true;
    }

    private static async Task EmitPlanSummary(
        ImportPlan plan, CsvData csv, IReadOnlyDictionary<string, string> mapping,
        string matchBy, bool dryRun, TextWriter output)
    {
        var totalIds = plan.Groups.Sum(g => g.ElementIds.Count);
        var prefix = dryRun ? "Dry run:" : "Plan:";
        await output.WriteLineAsync(
            $"{prefix} encoding={csv.EncodingName}, csvRows={csv.Rows.Count}, mappedColumns={mapping.Count}, " +
            $"matchBy={matchBy}, groups={plan.Groups.Count}, elementWrites={totalIds}.");

        if (plan.Skipped.Count > 0)
            await output.WriteLineAsync($"  Skipped cells: {plan.Skipped.Count} (empty values or empty match-by).");
        if (plan.Misses.Count > 0)
        {
            await output.WriteLineAsync($"  Misses: {plan.Misses.Count}");
            foreach (var m in plan.Misses.Take(10))
                await output.WriteLineAsync($"    row {m.RowNumber}: '{m.MatchByValue}' has no match in Revit.");
            if (plan.Misses.Count > 10)
                await output.WriteLineAsync($"    ... and {plan.Misses.Count - 10} more.");
        }
        if (plan.Duplicates.Count > 0)
        {
            await output.WriteLineAsync($"  Duplicates: {plan.Duplicates.Count}");
            foreach (var d in plan.Duplicates.Take(10))
                await output.WriteLineAsync(
                    $"    row {d.RowNumber}: '{d.MatchByValue}' matches {d.ElementIds.Count} elements ({string.Join(",", d.ElementIds)}).");
            if (plan.Duplicates.Count > 10)
                await output.WriteLineAsync($"    ... and {plan.Duplicates.Count - 10} more.");
        }
        foreach (var w in plan.Warnings)
            await output.WriteLineAsync($"  Warning: {w}");

        if (dryRun)
        {
            foreach (var g in plan.Groups.Take(20))
                await output.WriteLineAsync(
                    $"  [{g.Param}] = '{g.Value}' on {g.ElementIds.Count} element(s): {string.Join(",", g.ElementIds.Take(20))}{(g.ElementIds.Count > 20 ? ",..." : "")}");
            if (plan.Groups.Count > 20)
                await output.WriteLineAsync($"  ... and {plan.Groups.Count - 20} more groups.");
        }
    }

    private static bool HasFatalPlanIssue(ImportPlan plan, string onMissing, string onDuplicate)
    {
        if (onMissing == "error" && plan.Misses.Count > 0) return true;
        if (onDuplicate == "error" && plan.Duplicates.Count > 0) return true;
        return false;
    }

    private static async Task<(int Affected, List<(ImportGroup, string)> Failures)> ApplyPlan(
        RevitClient client, ImportPlan plan, int batchSize, TextWriter output)
    {
        var failures = new List<(ImportGroup, string)>();
        var affected = 0;

        foreach (var group in plan.Groups)
        {
            for (var off = 0; off < group.ElementIds.Count; off += batchSize)
            {
                var slice = group.ElementIds.GetRange(off, Math.Min(batchSize, group.ElementIds.Count - off));
                var req = new SetRequest
                {
                    ElementIds = slice,
                    Param = group.Param,
                    Value = group.Value,
                    DryRun = false
                };
                var resp = await client.SetParameterAsync(req);
                if (!resp.Success)
                {
                    failures.Add((group, resp.Error ?? "unknown"));
                    break; // skip remaining slices of this group
                }
                affected += resp.Data?.Affected ?? 0;
            }
        }

        return (affected, failures);
    }
}
