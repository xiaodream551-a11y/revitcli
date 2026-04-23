using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using RevitCli.Shared;

namespace RevitCli.Output;

public static class DiffRenderer
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static string Render(SnapshotDiff diff, string format, int maxRows)
    {
        return format?.ToLowerInvariant() switch
        {
            "json" => JsonSerializer.Serialize(diff, JsonOpts),
            "markdown" or "md" => RenderMarkdown(diff, maxRows),
            _ => RenderTable(diff, maxRows)
        };
    }

    private static string RenderTable(SnapshotDiff diff, int maxRows)
    {
        var sb = new StringBuilder();
        foreach (var w in diff.Warnings) sb.AppendLine($"[warn] {w}");

        foreach (var kv in diff.Summary.PerCategory)
        {
            var c = kv.Value;
            sb.AppendLine($"{kv.Key}: +{c.Added} / -{c.Removed} / ~{c.Modified}");
        }
        var sh = diff.Summary.Sheets;
        if (sh.Added + sh.Removed + sh.Modified > 0)
            sb.AppendLine($"sheets: +{sh.Added} / -{sh.Removed} / ~{sh.Modified}");
        var sc = diff.Summary.Schedules;
        if (sc.Added + sc.Removed + sc.Modified > 0)
            sb.AppendLine($"schedules: +{sc.Added} / -{sc.Removed} / ~{sc.Modified}");

        foreach (var kv in diff.Categories)
        {
            RenderCategorySection(sb, kv.Key, kv.Value, maxRows);
        }
        if (diff.Sheets.Added.Count + diff.Sheets.Removed.Count + diff.Sheets.Modified.Count > 0)
            RenderCategorySection(sb, "sheets", diff.Sheets, maxRows);
        if (diff.Schedules.Added.Count + diff.Schedules.Removed.Count + diff.Schedules.Modified.Count > 0)
            RenderCategorySection(sb, "schedules", diff.Schedules, maxRows);

        return sb.ToString().TrimEnd();
    }

    private static void RenderCategorySection(StringBuilder sb, string name, CategoryDiff d, int maxRows)
    {
        if (d.Added.Count == 0 && d.Removed.Count == 0 && d.Modified.Count == 0) return;
        sb.AppendLine();
        sb.AppendLine($"=== {name} ===");
        PrintList(sb, "+ ", d.Added.Count, maxRows, i => $"[{d.Added[i].Id}] {d.Added[i].Name}");
        PrintList(sb, "- ", d.Removed.Count, maxRows, i => $"[{d.Removed[i].Id}] {d.Removed[i].Name}");
        for (int i = 0; i < d.Modified.Count && i < maxRows; i++)
        {
            var m = d.Modified[i];
            sb.AppendLine($"~ [{m.Id}] {m.Key}");
            foreach (var ch in m.Changed)
                sb.AppendLine($"    {ch.Key}: \"{ch.Value.From}\" → \"{ch.Value.To}\"");
        }
        if (d.Modified.Count > maxRows)
            sb.AppendLine($"...and {d.Modified.Count - maxRows} more modified");
    }

    private static void PrintList(StringBuilder sb, string prefix, int count, int max, Func<int, string> fmt)
    {
        var shown = count < max ? count : max;
        for (int i = 0; i < shown; i++) sb.AppendLine(prefix + fmt(i));
        if (count > shown) sb.AppendLine($"{prefix}...and {count - shown} more");
    }

    private static string RenderMarkdown(SnapshotDiff diff, int maxRows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Model changes");
        foreach (var w in diff.Warnings) sb.AppendLine($"> ⚠ {w}").AppendLine();

        foreach (var kv in diff.Summary.PerCategory)
        {
            var c = kv.Value;
            sb.AppendLine($"**{kv.Key}**: +{c.Added} / -{c.Removed} / ~{c.Modified}");
        }
        if (diff.Summary.Sheets.Added + diff.Summary.Sheets.Removed + diff.Summary.Sheets.Modified > 0)
        {
            var s = diff.Summary.Sheets;
            sb.AppendLine($"**sheets**: +{s.Added} / -{s.Removed} / ~{s.Modified}");
        }

        foreach (var kv in diff.Categories)
        {
            RenderMdSection(sb, kv.Key, kv.Value, maxRows);
        }
        RenderMdSection(sb, "sheets", diff.Sheets, maxRows);
        RenderMdSection(sb, "schedules", diff.Schedules, maxRows);

        return sb.ToString().TrimEnd();
    }

    private static void RenderMdSection(StringBuilder sb, string name, CategoryDiff d, int maxRows)
    {
        if (d.Modified.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"### Modified {name}");
            sb.AppendLine("| Id | Key | Changed |");
            sb.AppendLine("|---|---|---|");
            var show = Math.Min(d.Modified.Count, maxRows);
            for (int i = 0; i < show; i++)
            {
                var m = d.Modified[i];
                var changes = new List<string>();
                foreach (var c in m.Changed)
                    changes.Add($"{EscapeMdCell(c.Key)}: \"{EscapeMdCell(c.Value.From)}\" → \"{EscapeMdCell(c.Value.To)}\"");
                sb.AppendLine($"| {m.Id} | {EscapeMdCell(m.Key)} | {string.Join("; ", changes)} |");
            }
            if (d.Modified.Count > show)
                sb.AppendLine($"\n...and {d.Modified.Count - show} more");
        }
        if (d.Added.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"### Added {name}");
            var show = Math.Min(d.Added.Count, maxRows);
            for (int i = 0; i < show; i++) sb.AppendLine($"- `{d.Added[i].Id}` {d.Added[i].Name}");
            if (d.Added.Count > show) sb.AppendLine($"...and {d.Added.Count - show} more");
        }
        if (d.Removed.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"### Removed {name}");
            var show = Math.Min(d.Removed.Count, maxRows);
            for (int i = 0; i < show; i++) sb.AppendLine($"- `{d.Removed[i].Id}` {d.Removed[i].Name}");
            if (d.Removed.Count > show) sb.AppendLine($"...and {d.Removed.Count - show} more");
        }
    }

    // Escape a value for embedding inside a Markdown table cell. Pipes would otherwise
    // be parsed as column separators and break the table.
    private static string EscapeMdCell(string s) => s.Replace("|", "\\|");
}
