using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using RevitCli.Journal;

namespace RevitCli.Commands;

/// <summary>
/// <c>revitcli journal</c> — read the audit trail at
/// <c>.revitcli/journal.jsonl</c>. Closes the operator-observability
/// gap left by <see cref="RevitCli.Output.JournalLogger"/>: every
/// CLI and MCP write tool logs there, but until now there was no
/// first-class way to read or summarize.
/// </summary>
public static class JournalCommand
{
    // CamelCase matches the journal's own field naming (`action`,
    // `outcome`, `timestamp`) — keeping consumers consistent so a
    // tail/stats round-trip doesn't change field-name conventions.
    private static readonly JsonSerializerOptions PrettyJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static Command Create()
    {
        var command = new Command("journal", "Inspect the .revitcli/journal.jsonl audit log");
        command.AddCommand(CreateTailCommand());
        command.AddCommand(CreateStatsCommand());
        return command;
    }

    // ─── tail ─────────────────────────────────────────────────────────────

    private static Command CreateTailCommand()
    {
        var limitOpt = new Option<int>("--limit", () => 20, "Maximum number of entries to show");
        var actionOpt = new Option<string?>("--action", "Filter to a single action name (case-insensitive)");
        var sinceOpt = new Option<string?>("--since", "Only entries newer than this duration (e.g. 1h, 30m, 7d)");
        var outputOpt = new Option<string>("--output", () => "table", "Output format: table, json");
        var pathOpt = new Option<string?>("--path", "Override the journal path (default: .revitcli/journal.jsonl)");

        var cmd = new Command("tail", "Show the most recent journal entries (newest first)")
        {
            limitOpt, actionOpt, sinceOpt, outputOpt, pathOpt
        };

        cmd.SetHandler(async (limit, action, since, outputFormat, path) =>
        {
            Environment.ExitCode = await ExecuteTailAsync(limit, action, since, outputFormat, path, Console.Out);
        }, limitOpt, actionOpt, sinceOpt, outputOpt, pathOpt);

        return cmd;
    }

    public static async Task<int> ExecuteTailAsync(
        int limit, string? action, string? since, string outputFormat, string? path, TextWriter output)
    {
        var journalPath = ResolveJournalPath(path);
        var sinceUtc = JournalReader.ResolveSinceUtc(since, DateTime.UtcNow);
        if (!string.IsNullOrWhiteSpace(since) && sinceUtc is null)
        {
            await output.WriteLineAsync($"Error: --since must be of the form Nm/Nh/Nd (e.g. 30m, 24h, 7d). Got: {since}");
            return 1;
        }

        var entries = JournalReader.Read(
            journalPath,
            limit: limit > 0 ? limit : null,
            actionFilter: action,
            sinceUtc: sinceUtc);

        switch ((outputFormat ?? "table").ToLowerInvariant())
        {
            case "json":
                await output.WriteLineAsync(SerializeEntries(entries));
                break;
            default:
                await output.WriteLineAsync(FormatTailTable(entries, journalPath));
                break;
        }
        return 0;
    }

    private static string FormatTailTable(IReadOnlyList<JournalEntry> entries, string journalPath)
    {
        if (entries.Count == 0)
            return $"No matching entries in {journalPath}.";

        var sb = new StringBuilder();
        sb.AppendLine($"{"Timestamp",-26} {"Action",-16} {"Transport",-9} {"Outcome",-26} User");
        sb.AppendLine(new string('-', 95));
        foreach (var e in entries)
        {
            sb.AppendLine(
                $"{Truncate(e.Timestamp ?? "(none)", 26),-26} " +
                $"{Truncate(e.Action ?? "(none)", 16),-16} " +
                $"{Truncate(e.Transport ?? "cli", 9),-9} " +
                $"{Truncate(e.Outcome ?? "(none)", 26),-26} " +
                $"{e.User ?? ""}");
        }
        sb.AppendLine();
        sb.AppendLine($"{entries.Count} entry/entries from {journalPath}.");
        return sb.ToString().TrimEnd();
    }

    // ─── stats ────────────────────────────────────────────────────────────

    private static Command CreateStatsCommand()
    {
        var sinceOpt = new Option<string?>("--since", () => "24h",
            "Only count entries newer than this duration (e.g. 1h, 30m, 7d). " +
            "Use --since 0 to disable the window.");
        var outputOpt = new Option<string>("--output", () => "table", "Output format: table, json");
        var pathOpt = new Option<string?>("--path", "Override the journal path (default: .revitcli/journal.jsonl)");

        var cmd = new Command("stats", "Aggregate counters over the audit log")
        {
            sinceOpt, outputOpt, pathOpt
        };

        cmd.SetHandler(async (since, outputFormat, path) =>
        {
            Environment.ExitCode = await ExecuteStatsAsync(since, outputFormat, path, Console.Out);
        }, sinceOpt, outputOpt, pathOpt);

        return cmd;
    }

    public static async Task<int> ExecuteStatsAsync(
        string? since, string outputFormat, string? path, TextWriter output)
    {
        var journalPath = ResolveJournalPath(path);
        var sinceUtc = JournalReader.ResolveSinceUtc(since, DateTime.UtcNow);
        if (!string.IsNullOrWhiteSpace(since) && sinceUtc is null && since != "0")
        {
            await output.WriteLineAsync($"Error: --since must be of the form Nm/Nh/Nd (e.g. 30m, 24h, 7d). Got: {since}");
            return 1;
        }

        var entries = JournalReader.Read(
            journalPath,
            limit: null,
            actionFilter: null,
            sinceUtc: sinceUtc);
        var stats = JournalReader.Summarize(entries);

        switch ((outputFormat ?? "table").ToLowerInvariant())
        {
            case "json":
                await output.WriteLineAsync(JsonSerializer.Serialize(stats, PrettyJson));
                break;
            default:
                await output.WriteLineAsync(FormatStatsTable(stats, journalPath, sinceUtc));
                break;
        }
        return 0;
    }

    private static string FormatStatsTable(JournalStats stats, string journalPath, DateTime? sinceUtc)
    {
        var sb = new StringBuilder();
        var window = sinceUtc.HasValue ? $"since {sinceUtc.Value:yyyy-MM-ddTHH:mm:ssZ}" : "all time";
        sb.AppendLine($"Audit log: {journalPath} ({window})");
        sb.AppendLine($"Total entries: {stats.Total}");
        sb.AppendLine();
        EmitBreakdown(sb, "By action", stats.ByAction);
        EmitBreakdown(sb, "By outcome", stats.ByOutcome);
        EmitBreakdown(sb, "By transport", stats.ByTransport);
        EmitBreakdown(sb, "By user", stats.ByUser);
        return sb.ToString().TrimEnd();
    }

    private static void EmitBreakdown(StringBuilder sb, string title, Dictionary<string, int> bucket)
    {
        sb.AppendLine($"{title}:");
        if (bucket.Count == 0)
        {
            sb.AppendLine("  (none)");
            return;
        }
        foreach (var kvp in bucket.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key))
            sb.AppendLine($"  {kvp.Value,5}  {kvp.Key}");
    }

    // ─── helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Default journal location: <c>.revitcli/journal.jsonl</c> under
    /// the discovered profile dir, falling back to the cwd if no
    /// profile is found. Mirrors the path
    /// <see cref="RevitCli.Output.JournalLogger"/> writes to.
    /// </summary>
    public static string ResolveJournalPath(string? overridePath)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
            return Path.GetFullPath(overridePath);

        var baseDir = Profile.ProfileLoader.Discover() is { } p
            ? Path.GetDirectoryName(Path.GetFullPath(p)) ?? Directory.GetCurrentDirectory()
            : Directory.GetCurrentDirectory();
        return Path.Combine(baseDir, ".revitcli", "journal.jsonl");
    }

    /// <summary>
    /// Serialize parsed entries back to JSON for the <c>--output json</c>
    /// path. We pass through the original <see cref="JournalEntry.Raw"/>
    /// element so tool-specific fields survive the read/write round-trip.
    /// </summary>
    internal static string SerializeEntries(IReadOnlyList<JournalEntry> entries)
    {
        var arr = entries.Select(e => e.Raw.Clone()).ToList();
        return JsonSerializer.Serialize(arr, PrettyJson);
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s.Substring(0, Math.Max(0, max - 1)) + "…";
    }
}
