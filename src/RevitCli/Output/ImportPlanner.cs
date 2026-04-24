using System;
using System.Collections.Generic;
using System.Linq;
using RevitCli.Shared;

namespace RevitCli.Output;

public class ImportGroup
{
    public string Param { get; init; } = "";
    public string Value { get; init; } = "";
    public List<long> ElementIds { get; init; } = new();
}

public class ImportMiss
{
    public int RowNumber { get; init; }
    public string MatchByValue { get; init; } = "";
}

public class ImportDuplicate
{
    public int RowNumber { get; init; }
    public string MatchByValue { get; init; } = "";
    public List<long> ElementIds { get; init; } = new();
}

public class ImportSkip
{
    public int RowNumber { get; init; }
    public string MatchByValue { get; init; } = "";
    public string Param { get; init; } = "";
    public string Reason { get; init; } = "";
}

public class ImportPlan
{
    public List<ImportGroup> Groups { get; init; } = new();
    public List<ImportMiss> Misses { get; init; } = new();
    public List<ImportDuplicate> Duplicates { get; init; } = new();
    public List<ImportSkip> Skipped { get; init; } = new();
    public List<string> Warnings { get; init; } = new();
}

public static class ImportPlanner
{
    /// <summary>
    /// Build an import plan: match each CSV row to Revit elements via matchBy parameter,
    /// then group write operations by (revitParam, value) for batched submission.
    /// </summary>
    /// <param name="onMissing">"error" | "warn" | "skip" — only "error" and "warn" record a Miss; "skip" silently drops.</param>
    /// <param name="onDuplicate">"error" | "first" | "all"</param>
    public static ImportPlan Plan(
        CsvData csv,
        IReadOnlyList<ElementInfo> elements,
        IReadOnlyDictionary<string, string> mapping,
        string matchBy,
        string onMissing,
        string onDuplicate)
    {
        var plan = new ImportPlan();

        // Index elements by matchBy parameter value.
        var index = new Dictionary<string, List<long>>(StringComparer.Ordinal);
        var seenIds = new HashSet<long>();
        foreach (var el in elements)
        {
            if (!seenIds.Add(el.Id)) continue;            // deduplicate by element Id
            if (!el.Parameters.TryGetValue(matchBy, out var key)) continue;
            var trimmed = key?.Trim() ?? "";
            if (trimmed.Length == 0) continue;
            if (!index.TryGetValue(trimmed, out var list))
            {
                list = new List<long>();
                index[trimmed] = list;
            }
            list.Add(el.Id);
        }
        foreach (var list in index.Values) list.Sort();

        // Resolve column index for matchBy and each mapped column.
        var matchByCol = csv.Headers.IndexOf(matchBy);
        if (matchByCol < 0)
            throw new InvalidOperationException(
                $"--match-by column '{matchBy}' not found in CSV headers.");

        var mappedColumns = csv.Headers
            .Select((h, idx) => (Header: h, Index: idx))
            .Where(t => mapping.ContainsKey(t.Header))
            .ToList();

        // (elementId, revitParam) -> latest (value, rowNumber, matchByValue) so "last wins" + warning.
        var assignments = new Dictionary<(long Id, string Param), (string Value, int Row, string MatchKey)>();
        var warnedKeys = new HashSet<(long Id, string Param)>();

        for (var rowIdx = 0; rowIdx < csv.Rows.Count; rowIdx++)
        {
            var rowNum = rowIdx + 2; // header + 0-based → 1-based data row
            var row = csv.Rows[rowIdx];
            if (matchByCol >= row.Count) continue;

            var matchKey = row[matchByCol]?.Trim() ?? "";
            if (matchKey.Length == 0)
            {
                plan.Skipped.Add(new ImportSkip
                {
                    RowNumber = rowNum,
                    MatchByValue = "",
                    Param = matchBy,
                    Reason = "match-by cell is empty"
                });
                continue;
            }

            if (!index.TryGetValue(matchKey, out var matched))
            {
                if (onMissing != "skip")
                    plan.Misses.Add(new ImportMiss { RowNumber = rowNum, MatchByValue = matchKey });
                continue;
            }

            List<long> targets;
            if (matched.Count > 1)
            {
                if (onDuplicate == "first")
                {
                    targets = new List<long> { matched[0] };
                }
                else if (onDuplicate == "all")
                {
                    // share reference — read-only iteration, no mutation
                    targets = matched;
                }
                else if (onDuplicate == "error")
                {
                    plan.Duplicates.Add(new ImportDuplicate
                    {
                        RowNumber = rowNum,
                        MatchByValue = matchKey,
                        ElementIds = new List<long>(matched)   // defensive copy — see Fix 2
                    });
                    continue;
                }
                else
                {
                    throw new ArgumentException(
                        $"Unknown onDuplicate value: '{onDuplicate}'. Expected: error, first, all.",
                        nameof(onDuplicate));
                }
            }
            else
            {
                targets = matched;
            }

            foreach (var (header, colIdx) in mappedColumns)
            {
                var revitParam = mapping[header];
                var raw = colIdx < row.Count ? (row[colIdx] ?? "") : "";
                if (raw.Length == 0)
                {
                    plan.Skipped.Add(new ImportSkip
                    {
                        RowNumber = rowNum,
                        MatchByValue = matchKey,
                        Param = revitParam,
                        Reason = "cell is empty"
                    });
                    continue;
                }

                foreach (var id in targets)
                {
                    var key = (id, revitParam);
                    if (assignments.TryGetValue(key, out var prev))
                    {
                        // Emit at most one warning per (element, param) — prevents log spam when a CSV
                        // is reordered or sliced; user sees the first override and the final value
                        // (assignments[key] = ... still records the latest, so result is deterministic).
                        if (prev.Value != raw && warnedKeys.Add(key))
                            plan.Warnings.Add(
                                $"Row {rowNum}: '{matchKey}' / '{revitParam}' " +
                                $"overrides earlier value from row {prev.Row} ('{prev.Value}' → '{raw}').");
                    }
                    assignments[key] = (raw, rowNum, matchKey);
                }
            }
        }

        // Group by (param, value) → ImportGroup.
        var grouped = assignments
            .GroupBy(kv => (kv.Key.Param, kv.Value.Value))
            .OrderBy(g => g.Key.Param, StringComparer.Ordinal)
            .ThenBy(g => g.Key.Value, StringComparer.Ordinal);

        foreach (var grp in grouped)
        {
            var ids = grp.Select(kv => kv.Key.Id).Distinct().OrderBy(x => x).ToList();
            plan.Groups.Add(new ImportGroup
            {
                Param = grp.Key.Param,
                Value = grp.Key.Value,
                ElementIds = ids
            });
        }

        return plan;
    }
}
