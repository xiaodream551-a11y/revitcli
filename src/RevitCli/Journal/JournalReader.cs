using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace RevitCli.Journal;

/// <summary>
/// Reads <c>.revitcli/journal.jsonl</c> entries written by
/// <see cref="RevitCli.Output.JournalLogger"/>. The journal is the
/// canonical audit trail across CLI and MCP write operations:
/// every <c>set</c> / <c>fix</c> / <c>rollback</c> / <c>import</c> /
/// <c>publish</c> / <c>family-purge</c> / <c>family-export</c> call
/// — successful or refused — appends a line.
///
/// This module's job is to make those entries observable: filter by
/// action, by time window, by limit; project them as structured
/// <see cref="JournalEntry"/> objects so the CLI <c>journal</c>
/// command and the MCP <c>revitcli://journal/recent</c> resource
/// share one parsing path.
///
/// Robustness: the reader silently SKIPS lines that fail to parse as
/// JSON. The journal is append-only and its writer survives
/// concurrent partial writes by line-buffering, but a crash mid-write
/// could leave a half-line. Skipping keeps the rest readable rather
/// than crashing the operator's <c>journal tail</c>.
/// </summary>
public static class JournalReader
{
    /// <summary>
    /// Parse and return entries from the journal file at
    /// <paramref name="journalPath"/>. Honors all filter parameters;
    /// returns most-recent-first up to <paramref name="limit"/>.
    /// </summary>
    public static List<JournalEntry> Read(
        string journalPath,
        int? limit = null,
        string? actionFilter = null,
        DateTime? sinceUtc = null)
    {
        if (!File.Exists(journalPath))
            return new List<JournalEntry>();

        var entries = new List<JournalEntry>();
        // ReadAllLines is fine for journals up to a few hundred MB —
        // beyond that we'd want a streaming reader, but the journal
        // is typically operator-pruned (or rotated) before that.
        var lines = File.ReadAllLines(journalPath);
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JournalEntry? entry;
            try
            {
                entry = ParseLine(line);
            }
            catch (JsonException)
            {
                // Skip malformed lines — see class comment for rationale.
                continue;
            }
            if (entry is null) continue;

            if (!string.IsNullOrEmpty(actionFilter)
                && !string.Equals(entry.Action, actionFilter, StringComparison.OrdinalIgnoreCase))
                continue;

            if (sinceUtc.HasValue && entry.TimestampUtc.HasValue
                && entry.TimestampUtc.Value < sinceUtc.Value)
                continue;

            entries.Add(entry);
        }

        // Most-recent-first. Within the same timestamp keep file order
        // (rare in practice — timestamps go to ms — but keeps the
        // output deterministic).
        entries.Reverse();

        if (limit.HasValue && limit.Value > 0 && entries.Count > limit.Value)
            entries = entries.Take(limit.Value).ToList();

        return entries;
    }

    /// <summary>
    /// Parse <paramref name="durationSpec"/> as a relative duration
    /// (<c>1h</c>, <c>30m</c>, <c>7d</c>, <c>24h</c>) and return the
    /// UTC instant <em>that long ago</em>. Used to back the
    /// <c>--since</c> flag on the journal CLI.
    /// </summary>
    public static DateTime? ResolveSinceUtc(string? durationSpec, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(durationSpec)) return null;
        var spec = durationSpec.Trim();
        if (spec.Length < 2) return null;
        var suffix = spec[^1];
        if (!int.TryParse(spec[..^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n <= 0)
            return null;
        return suffix switch
        {
            'm' => nowUtc.AddMinutes(-n),
            'h' => nowUtc.AddHours(-n),
            'd' => nowUtc.AddDays(-n),
            _   => null,
        };
    }

    /// <summary>
    /// Aggregate counters used by <c>journal stats</c>. We pre-compute
    /// the breakdowns here so the CLI rendering layer is a thin
    /// projection over a typed result, not a tree of LINQ groupings
    /// inlined into the formatter.
    /// </summary>
    public static JournalStats Summarize(IReadOnlyList<JournalEntry> entries)
    {
        var stats = new JournalStats { Total = entries.Count };
        foreach (var e in entries)
        {
            Bump(stats.ByAction, e.Action ?? "(unknown)");
            Bump(stats.ByOutcome, e.Outcome ?? "(none)");
            if (!string.IsNullOrEmpty(e.Transport))
                Bump(stats.ByTransport, e.Transport);
            if (!string.IsNullOrEmpty(e.User))
                Bump(stats.ByUser, e.User);
        }
        return stats;
    }

    private static void Bump(Dictionary<string, int> bucket, string key)
    {
        bucket[key] = (bucket.TryGetValue(key, out var v) ? v : 0) + 1;
    }

    private static JournalEntry? ParseLine(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;

        var entry = new JournalEntry
        {
            Action = TryGetString(root, "action"),
            Transport = TryGetString(root, "transport"),
            Outcome = TryGetString(root, "outcome"),
            User = TryGetString(root, "user"),
            Timestamp = TryGetString(root, "timestamp"),
            // Keep the raw object so consumers (the MCP resource, the
            // CLI --output json) can pass through any tool-specific
            // fields (param/category/baseline/etc.) without us hard-
            // coding every shape.
            Raw = root.Clone(),
        };

        if (!string.IsNullOrEmpty(entry.Timestamp)
            && DateTime.TryParse(
                entry.Timestamp,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            entry.TimestampUtc = parsed;
        }

        return entry;
    }

    private static string? TryGetString(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var prop)) return null;
        if (prop.ValueKind != JsonValueKind.String) return null;
        return prop.GetString();
    }
}

/// <summary>One parsed journal line.</summary>
public sealed class JournalEntry
{
    public string? Action { get; set; }
    public string? Transport { get; set; }
    public string? Outcome { get; set; }
    public string? User { get; set; }
    public string? Timestamp { get; set; }
    public DateTime? TimestampUtc { get; set; }

    /// <summary>Original JSON for pass-through emission.</summary>
    public JsonElement Raw { get; set; }
}

/// <summary>Aggregated counters over a set of <see cref="JournalEntry"/>.</summary>
public sealed class JournalStats
{
    public int Total { get; set; }
    public Dictionary<string, int> ByAction { get; } = new();
    public Dictionary<string, int> ByOutcome { get; } = new();
    public Dictionary<string, int> ByTransport { get; } = new();
    public Dictionary<string, int> ByUser { get; } = new();
}
