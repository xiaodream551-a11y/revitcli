using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using RevitCli.Client;
using RevitCli.History;
using RevitCli.Output;
using RevitCli.Shared;
using Spectre.Console;

namespace RevitCli.Commands;

public static class ScoreCommand
{
    internal const string ScoreSchemaVersion = "model-health-score.v1";
    internal const string HistorySchemaVersion = "model-health-history.v1";
    internal const string FindingScoreSchemaVersion = "model-health-finding-score.v1";
    internal static readonly string[] OutputFormats = { "table", "json", "markdown" };
    internal static readonly string[] FindingFailOnValues = { "none", "warning", "error", "blocker" };

    private static readonly Dictionary<string, int> RuleWeights = new(StringComparer.OrdinalIgnoreCase)
    {
        ["room-bounds"] = 15,
        ["unplaced-rooms"] = 10,
        ["duplicate-room-numbers"] = 15,
        ["room-metadata"] = 10,
        ["level-consistency"] = 10,
        ["naming"] = 5,
        ["views-not-on-sheets"] = 10,
        ["sheets-missing-info"] = 10,
        ["imported-dwg"] = 10,
        ["in-place-families"] = 5,
    };

    public static Command Create(RevitClient client)
    {
        var historyOpt = new Option<string?>("--history",
            "Render a per-day score time series over a window (e.g. 7d, 30d). Reads .revitcli/history/.");
        var findingsOpt = new Option<string?>("--from-findings",
            "Score a bimops-finding.v1 JSON file without contacting Revit.");
        var waiversOpt = new Option<string?>("--waivers",
            "Optional bimops-waivers.v1 JSON file for --from-findings.");
        var failOnOpt = new Option<string>("--fail-on", () => "none",
            "Gate threshold for --from-findings: none, warning, error, blocker");
        var dirOpt = new Option<string?>("--dir", "Override history directory (paired with --history)");
        var outputOpt = new Option<string>("--output", () => "table", "Output format: table, json, markdown");

        var command = new Command("score", "Calculate model health score (0-100)")
        {
            historyOpt,
            findingsOpt,
            waiversOpt,
            failOnOpt,
            dirOpt,
            outputOpt,
        };

        command.SetHandler(async (string? history, string? findingsPath, string? waiversPath, string failOn, string? dir, string outputFormat) =>
        {
            if (!string.IsNullOrWhiteSpace(findingsPath))
            {
                if (!string.IsNullOrWhiteSpace(history))
                {
                    Environment.ExitCode = await WriteFindingsScoreErrorAsync(
                        Console.Out,
                        outputFormat,
                        "Use either --history or --from-findings, not both.");
                    return;
                }

                Environment.ExitCode = await ExecuteFindingsAsync(findingsPath, Console.Out, outputFormat, failOn, waiversPath);
                return;
            }

            if (!string.IsNullOrWhiteSpace(waiversPath))
            {
                Environment.ExitCode = await WriteFindingsScoreErrorAsync(
                    Console.Out,
                    outputFormat,
                    "Use --waivers with --from-findings.");
                return;
            }

            if (!string.Equals(failOn, "none", StringComparison.OrdinalIgnoreCase))
            {
                Environment.ExitCode = await WriteFindingsScoreErrorAsync(
                    Console.Out,
                    outputFormat,
                    "Use --fail-on with --from-findings.");
                return;
            }

            if (!string.IsNullOrWhiteSpace(history))
            {
                Environment.ExitCode = await ExecuteHistoryAsync(history, dir, Console.Out, outputFormat);
                return;
            }

            if (!ConsoleHelper.IsInteractive)
            {
                Environment.ExitCode = await ExecuteAsync(client, Console.Out, outputFormat);
                return;
            }

            var result = await RunScore(client);
            if (result < 0)
            {
                AnsiConsole.MarkupLine("[red]Error: could not calculate score.[/]");
                Environment.ExitCode = 1;
                return;
            }

            var color = result >= 80 ? "green" : result >= 60 ? "yellow" : "red";
            var grade = result >= 90 ? "A" : result >= 80 ? "B" : result >= 70 ? "C" : result >= 60 ? "D" : "F";

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"  Model Health Score: [{color} bold]{result}[/] / 100  [{color}]({grade})[/]");
            AnsiConsole.WriteLine();
        }, historyOpt, findingsOpt, waiversOpt, failOnOpt, dirOpt, outputOpt);

        return command;
    }

    public static Task<int> ExecuteAsync(RevitClient client, TextWriter output) =>
        ExecuteAsync(client, output, "table");

    public static async Task<int> ExecuteAsync(RevitClient client, TextWriter output, string outputFormat)
    {
        if (!TerminalOutputFormat.TryNormalize(outputFormat, out var normalized, OutputFormats))
        {
            await WriteOutputFormatErrorAsync(output);
            return 1;
        }

        var score = await RunScore(client);
        if (score < 0)
        {
            await WriteErrorAsync(output, normalized, ScoreSchemaVersion, "live-audit",
                "could not calculate score. Is Revit connected?");
            return 1;
        }

        var grade = LetterGrade(score);
        var report = new ModelHealthScoreReport(
            ScoreSchemaVersion,
            DateTimeOffset.UtcNow,
            true,
            "live-audit",
            score,
            grade,
            null);
        await WriteScoreReportAsync(output, normalized, report);
        return 0;
    }

    /// <summary>
    /// Render one row per day for the past <paramref name="window"/> using the LAST
    /// snapshot of each day. Score is computed offline from the snapshot itself
    /// (see <see cref="SnapshotScore"/>) so this works without a live Revit
    /// connection.
    /// </summary>
    public static Task<int> ExecuteHistoryAsync(
        string window,
        string? overrideDir,
        TextWriter output) =>
        ExecuteHistoryAsync(window, overrideDir, output, "table");

    public static async Task<int> ExecuteHistoryAsync(
        string window,
        string? overrideDir,
        TextWriter output,
        string outputFormat)
    {
        if (!TerminalOutputFormat.TryNormalize(outputFormat, out var normalized, OutputFormats))
        {
            await WriteOutputFormatErrorAsync(output);
            return 1;
        }

        TimeSpan windowSpan;
        try
        {
            windowSpan = HistoryCommand.ParseWindow(window);
        }
        catch (FormatException ex)
        {
            await WriteErrorAsync(output, normalized, HistorySchemaVersion, "history", ex.Message);
            return 1;
        }

        HistoryStore store;
        try
        {
            store = string.IsNullOrWhiteSpace(overrideDir)
                ? HistoryStore.ForProject(Directory.GetCurrentDirectory())
                : new HistoryStore(overrideDir!);
        }
        catch (ArgumentException ex)
        {
            await WriteErrorAsync(output, normalized, HistorySchemaVersion, "history", ex.Message);
            return 1;
        }

        if (!Directory.Exists(store.RootDirectory))
        {
            await WriteErrorAsync(output, normalized, HistorySchemaVersion, "history",
                $"history store not initialised. Run 'revitcli history init' (looked in {store.RootDirectory}).");
            return 1;
        }

        IReadOnlyList<SnapshotMetadata> entries;
        try
        {
            entries = await store.ListAsync(includeFixBaselines: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await WriteErrorAsync(output, normalized, HistorySchemaVersion, "history",
                $"failed to read history: {ex.Message}");
            return 1;
        }

        var now = DateTimeOffset.UtcNow;
        var cutoff = now - windowSpan;

        // Group by UTC date, keep latest entry per day, ascending by date.
        var perDay = entries
            .Select(meta => new { Meta = meta, At = ParseCapturedAt(meta.CapturedAt) })
            .Where(x => x.At >= cutoff)
            .GroupBy(x => x.At.UtcDateTime.Date)
            .Select(g => g.OrderByDescending(x => x.At).ThenByDescending(x => x.Meta.Id, StringComparer.Ordinal).First())
            .OrderBy(x => x.At)
            .ToList();

        // Load each day's snapshot once; compute score and inter-day diffs.
        var rows = new List<HistoryScoreRow>(perDay.Count);
        ModelSnapshot? prevSnap = null;
        foreach (var entry in perDay)
        {
            ModelSnapshot? snap = null;
            try
            {
                snap = await store.ReadAsync(entry.Meta);
            }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidDataException)
            {
                // Missing/corrupt snapshot — emit row with score=null so the user still sees the gap.
                _ = ex;
            }

            int? score = snap == null ? null : SnapshotScoreInt(snap);
            int newCount = 0, resolvedCount = 0, unchangedCount = 0;
            if (prevSnap != null && snap != null)
            {
                ComputeChangeStats(prevSnap, snap, out newCount, out resolvedCount, out unchangedCount);
            }
            else if (snap != null)
            {
                // First row in the window: nothing to compare against; report element count as unchanged.
                unchangedCount = TotalElementCount(snap);
            }

            rows.Add(new HistoryScoreRow(
                entry.At.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                score,
                newCount,
                resolvedCount,
                unchangedCount));

            if (snap != null)
            {
                prevSnap = snap;
            }
        }

        var report = new ModelHealthHistoryReport(
            HistorySchemaVersion,
            DateTimeOffset.UtcNow,
            true,
            window,
            store.RootDirectory,
            rows);
        await WriteHistoryReportAsync(output, normalized, report);
        return 0;
    }

    public static async Task<int> ExecuteFindingsAsync(
        string findingsPath,
        TextWriter output,
        string outputFormat = "table",
        string failOn = "none",
        string? waiversPath = null)
    {
        if (!TerminalOutputFormat.TryNormalize(outputFormat, out var normalized, OutputFormats))
        {
            await WriteOutputFormatErrorAsync(output);
            return 3;
        }
        if (!TryNormalizeFindingFailOn(failOn, out var normalizedFailOn))
        {
            await WriteFindingsScoreErrorAsync(output, normalized,
                "--fail-on must be one of: none, warning, error, blocker.");
            return 3;
        }

        BimOpsFindingEnvelope envelope;
        var fullPath = Path.GetFullPath(findingsPath);
        try
        {
            if (!File.Exists(fullPath))
            {
                await WriteFindingsScoreErrorAsync(output, normalized, $"findings file not found: {fullPath}");
                return 3;
            }

            envelope = JsonSerializer.Deserialize<BimOpsFindingEnvelope>(
                await File.ReadAllTextAsync(fullPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new JsonException("findings file deserialized to null.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            await WriteFindingsScoreErrorAsync(output, normalized, $"failed to read findings: {ex.Message}");
            return 3;
        }

        if (!string.Equals(envelope.SchemaVersion, BimOpsFindingSchema.Version, StringComparison.Ordinal))
        {
            await WriteFindingsScoreErrorAsync(output, normalized,
                $"schemaVersion must be {BimOpsFindingSchema.Version}, got {envelope.SchemaVersion ?? "(missing)"}.");
            return 3;
        }

        FindingWaiverApplication waiverApplication;
        try
        {
            waiverApplication = ReadAndApplyWaivers(envelope.Findings, waiversPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            await WriteFindingsScoreErrorAsync(output, normalized, $"failed to read waivers: {ex.Message}");
            return 3;
        }

        var effectiveFindings = waiverApplication.EffectiveFindings;
        var summary = SummarizeFindings(effectiveFindings);
        var score = FindingsScore(summary);
        var exitCode = FindingsGateExitCode(summary, normalizedFailOn);
        var report = new ModelHealthFindingScoreReport(
            FindingScoreSchemaVersion,
            DateTimeOffset.UtcNow,
            true,
            fullPath,
            envelope.Source,
            false,
            waiverApplication.WaiversPath,
            normalizedFailOn,
            exitCode,
            score,
            LetterGrade(score),
            summary,
            effectiveFindings.Count,
            envelope.Findings.Count,
            waiverApplication.WaivedCount,
            waiverApplication.ActiveWaiverCount,
            waiverApplication.ExpiredWaiverCount,
            null);

        await WriteFindingsScoreReportAsync(output, normalized, report);
        return exitCode;
    }

    /// <summary>
    /// Compute a deterministic 0-100 score directly from a snapshot, without
    /// requiring a live Revit connection. Used by the v1.6 trend renderer
    /// (<c>history trend --metric score</c>) and by <c>score --history</c>.
    /// The score blends three completeness ratios:
    /// <list type="bullet">
    ///   <item>Sheets that carry a non-empty <c>Number</c>.</item>
    ///   <item>Rooms (any case) that carry a non-empty <c>Number</c> and Name.</item>
    ///   <item>Schedules that have a non-empty name and at least one row.</item>
    /// </list>
    /// Empty snapshots default to 100 (no signals to penalise).
    /// </summary>
    public static double? SnapshotScore(ModelSnapshot snapshot)
    {
        if (snapshot == null)
        {
            return null;
        }

        var components = new List<double>();
        var sheetRatio = SheetCompleteness(snapshot);
        if (sheetRatio.HasValue)
        {
            components.Add(sheetRatio.Value);
        }

        var roomRatio = RoomMetadataCompleteness(snapshot);
        if (roomRatio.HasValue)
        {
            components.Add(roomRatio.Value);
        }

        var scheduleRatio = ScheduleCompleteness(snapshot);
        if (scheduleRatio.HasValue)
        {
            components.Add(scheduleRatio.Value);
        }

        if (components.Count == 0)
        {
            return 100.0;
        }

        return Math.Round(components.Average() * 100.0, 0, MidpointRounding.AwayFromZero);
    }

    internal static int SnapshotScoreInt(ModelSnapshot snapshot)
    {
        var v = SnapshotScore(snapshot);
        if (!v.HasValue)
        {
            return 0;
        }

        var clamped = Math.Min(100.0, Math.Max(0.0, v.Value));
        return (int)Math.Round(clamped, MidpointRounding.AwayFromZero);
    }

    internal static string LetterGrade(int score)
    {
        if (score >= 90) return "A";
        if (score >= 80) return "B";
        if (score >= 70) return "C";
        if (score >= 60) return "D";
        return "F";
    }

    internal static int FindingsScore(BimOpsFindingSummary summary)
    {
        var penalty =
            (summary.Blockers * 25) +
            (summary.Errors * 15) +
            (summary.Warnings * 5);
        return Math.Max(0, 100 - penalty);
    }

    internal static int FindingsGateExitCode(BimOpsFindingSummary summary, string failOn)
    {
        return failOn switch
        {
            "none" => 0,
            "warning" when summary.Blockers > 0 || summary.Errors > 0 => 2,
            "warning" when summary.Warnings > 0 => 1,
            "warning" => 0,
            "error" when summary.Blockers > 0 || summary.Errors > 0 => 2,
            "error" => 0,
            "blocker" when summary.Blockers > 0 => 2,
            "blocker" => 0,
            _ => 3,
        };
    }

    private static bool TryNormalizeFindingFailOn(string? failOn, out string normalized)
    {
        normalized = string.IsNullOrWhiteSpace(failOn) ? "none" : failOn.Trim().ToLowerInvariant();
        return FindingFailOnValues.Contains(normalized, StringComparer.OrdinalIgnoreCase);
    }

    private static BimOpsFindingSummary SummarizeFindings(IReadOnlyList<BimOpsFinding> findings)
    {
        return new BimOpsFindingSummary
        {
            Info = findings.Count(finding => string.Equals(finding.Severity, BimOpsFindingSchema.SeverityInfo, StringComparison.OrdinalIgnoreCase)),
            Warnings = findings.Count(finding => string.Equals(finding.Severity, BimOpsFindingSchema.SeverityWarning, StringComparison.OrdinalIgnoreCase)),
            Errors = findings.Count(finding => string.Equals(finding.Severity, BimOpsFindingSchema.SeverityError, StringComparison.OrdinalIgnoreCase)),
            Blockers = findings.Count(finding => string.Equals(finding.Severity, BimOpsFindingSchema.SeverityBlocker, StringComparison.OrdinalIgnoreCase)),
        };
    }

    private static FindingWaiverApplication ReadAndApplyWaivers(
        IReadOnlyList<BimOpsFinding> findings,
        string? waiversPath)
    {
        if (string.IsNullOrWhiteSpace(waiversPath))
            return new FindingWaiverApplication("", findings, 0, 0, 0);

        var fullPath = Path.GetFullPath(waiversPath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"waivers file not found: {fullPath}", fullPath);

        var document = JsonSerializer.Deserialize<FindingWaiverDocument>(
            File.ReadAllText(fullPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new JsonException("waivers file deserialized to null.");
        if (!string.Equals(document.SchemaVersion, "bimops-waivers.v1", StringComparison.Ordinal))
            throw new InvalidOperationException($"schemaVersion must be bimops-waivers.v1, got {document.SchemaVersion ?? "(missing)"}.");

        var now = DateTimeOffset.UtcNow;
        var activeWaivers = new List<FindingWaiver>();
        var expiredCount = 0;
        foreach (var waiver in document.Waivers)
        {
            if (WaiverExpired(waiver, now))
                expiredCount++;
            else
                activeWaivers.Add(waiver);
        }

        if (activeWaivers.Count == 0)
            return new FindingWaiverApplication(fullPath, findings, 0, 0, expiredCount);

        var effective = new List<BimOpsFinding>();
        var waived = 0;
        foreach (var finding in findings)
        {
            if (activeWaivers.Any(waiver => WaiverMatches(waiver, finding)))
            {
                waived++;
                continue;
            }

            effective.Add(finding);
        }

        return new FindingWaiverApplication(fullPath, effective, waived, activeWaivers.Count, expiredCount);
    }

    private static bool WaiverExpired(FindingWaiver waiver, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(waiver.Expires))
            return false;

        // An unparseable expiry must not silently keep a waiver active forever;
        // treat it as expired so a malformed date never suppresses real findings.
        if (!DateTimeOffset.TryParse(
                waiver.Expires,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var expires))
        {
            return true;
        }

        return expires < now;
    }

    private static bool WaiverMatches(FindingWaiver waiver, BimOpsFinding finding)
    {
        if (!string.IsNullOrWhiteSpace(waiver.FindingId) &&
            string.Equals(waiver.FindingId, finding.FindingId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(waiver.RuleId) &&
            string.Equals(waiver.RuleId, finding.RuleId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static double? SheetCompleteness(ModelSnapshot snapshot)
    {
        if (snapshot.Sheets == null || snapshot.Sheets.Count == 0)
        {
            return null;
        }

        var withNumber = snapshot.Sheets.Count(s => !string.IsNullOrWhiteSpace(s.Number));
        return (double)withNumber / snapshot.Sheets.Count;
    }

    private static double? RoomMetadataCompleteness(ModelSnapshot snapshot)
    {
        if (snapshot.Categories == null)
        {
            return null;
        }

        // Find any category whose name resembles "rooms" — fixture data uses lower-case "rooms".
        List<SnapshotElement>? rooms = null;
        foreach (var pair in snapshot.Categories)
        {
            if (string.Equals(pair.Key, "rooms", StringComparison.OrdinalIgnoreCase))
            {
                rooms = pair.Value;
                break;
            }
        }

        if (rooms == null || rooms.Count == 0)
        {
            return null;
        }

        var complete = 0;
        foreach (var room in rooms)
        {
            var hasNumber = room.Parameters != null
                && room.Parameters.TryGetValue("Number", out var num)
                && !string.IsNullOrWhiteSpace(num);
            var hasName = !string.IsNullOrWhiteSpace(room.Name);
            if (hasNumber && hasName)
            {
                complete++;
            }
        }

        return (double)complete / rooms.Count;
    }

    private static double? ScheduleCompleteness(ModelSnapshot snapshot)
    {
        if (snapshot.Schedules == null || snapshot.Schedules.Count == 0)
        {
            return null;
        }

        var ok = snapshot.Schedules.Count(s => !string.IsNullOrWhiteSpace(s.Name) && s.RowCount > 0);
        return (double)ok / snapshot.Schedules.Count;
    }

    private static int TotalElementCount(ModelSnapshot snapshot)
    {
        if (snapshot.Summary?.ElementCounts != null && snapshot.Summary.ElementCounts.Count > 0)
        {
            return snapshot.Summary.ElementCounts.Values.Sum();
        }

        var total = 0;
        if (snapshot.Categories != null)
        {
            foreach (var entries in snapshot.Categories.Values)
            {
                if (entries != null)
                {
                    total += entries.Count;
                }
            }
        }
        return total;
    }

    private static void ComputeChangeStats(
        ModelSnapshot from,
        ModelSnapshot to,
        out int added,
        out int removed,
        out int unchanged)
    {
        added = 0;
        removed = 0;
        unchanged = 0;

        var fromIds = new HashSet<long>();
        if (from.Categories != null)
        {
            foreach (var list in from.Categories.Values)
            {
                if (list == null) continue;
                foreach (var el in list)
                {
                    fromIds.Add(el.Id);
                }
            }
        }

        var toIds = new HashSet<long>();
        if (to.Categories != null)
        {
            foreach (var list in to.Categories.Values)
            {
                if (list == null) continue;
                foreach (var el in list)
                {
                    toIds.Add(el.Id);
                }
            }
        }

        foreach (var id in toIds)
        {
            if (fromIds.Contains(id)) unchanged++;
            else added++;
        }

        foreach (var id in fromIds)
        {
            if (!toIds.Contains(id)) removed++;
        }
    }

    private static DateTimeOffset ParseCapturedAt(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DateTimeOffset.MinValue;
        }

        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;
    }

    private static async Task WriteHistoryTableAsync(IReadOnlyList<HistoryScoreRow> rows, TextWriter output)
    {
        if (rows.Count == 0)
        {
            return;
        }

        const string dateHeader = "date";
        const string scoreHeader = "score";
        const string letterHeader = "letter";
        const string newHeader = "new";
        const string resolvedHeader = "resolved";
        const string unchangedHeader = "unchanged";

        var dateW = Math.Max(dateHeader.Length, rows.Max(r => r.Date.Length));
        var scoreW = Math.Max(scoreHeader.Length, rows.Max(r =>
            (r.Score?.ToString(CultureInfo.InvariantCulture) ?? "-").Length));
        var letterW = Math.Max(letterHeader.Length, rows.Max(r =>
            (r.Score.HasValue ? LetterGrade(r.Score.Value) : "-").Length));
        var newW = Math.Max(newHeader.Length, rows.Max(r =>
            r.NewCount.ToString(CultureInfo.InvariantCulture).Length));
        var resolvedW = Math.Max(resolvedHeader.Length, rows.Max(r =>
            r.ResolvedCount.ToString(CultureInfo.InvariantCulture).Length));
        var unchangedW = Math.Max(unchangedHeader.Length, rows.Max(r =>
            r.UnchangedCount.ToString(CultureInfo.InvariantCulture).Length));

        await output.WriteLineAsync(string.Join("  ", new[]
        {
            dateHeader.PadRight(dateW),
            scoreHeader.PadLeft(scoreW),
            letterHeader.PadRight(letterW),
            newHeader.PadLeft(newW),
            resolvedHeader.PadLeft(resolvedW),
            unchangedHeader.PadLeft(unchangedW),
        }));

        foreach (var row in rows)
        {
            var scoreText = row.Score?.ToString(CultureInfo.InvariantCulture) ?? "-";
            await output.WriteLineAsync(string.Join("  ", new[]
            {
                row.Date.PadRight(dateW),
                scoreText.PadLeft(scoreW),
                row.Letter.PadRight(letterW),
                row.NewCount.ToString(CultureInfo.InvariantCulture).PadLeft(newW),
                row.ResolvedCount.ToString(CultureInfo.InvariantCulture).PadLeft(resolvedW),
                row.UnchangedCount.ToString(CultureInfo.InvariantCulture).PadLeft(unchangedW),
            }));
        }
    }

    internal sealed record HistoryScoreRow(
        string Date,
        int? Score,
        int NewCount,
        int ResolvedCount,
        int UnchangedCount)
    {
        public string Letter => Score.HasValue ? LetterGrade(Score.Value) : "-";
    }

    internal sealed record ModelHealthScoreReport(
        string SchemaVersion,
        DateTimeOffset GeneratedAt,
        bool Success,
        string Source,
        int? Score,
        string? Letter,
        string? Error);

    internal sealed record ModelHealthHistoryReport(
        string SchemaVersion,
        DateTimeOffset GeneratedAt,
        bool Success,
        string Window,
        string HistoryDirectory,
        IReadOnlyList<HistoryScoreRow> Rows)
    {
        public int RowCount => Rows.Count;
    }

    internal sealed record ModelHealthErrorReport(
        string SchemaVersion,
        DateTimeOffset GeneratedAt,
        bool Success,
        string Source,
        string Error);

    internal sealed record ModelHealthFindingScoreReport(
        string SchemaVersion,
        DateTimeOffset GeneratedAt,
        bool Success,
        string FindingsPath,
        string FindingsSource,
        bool RequiresRevit,
        string WaiversPath,
        string FailOn,
        int ExitCode,
        int Score,
        string Letter,
        BimOpsFindingSummary Summary,
        int FindingCount,
        int OriginalFindingCount,
        int WaivedCount,
        int ActiveWaiverCount,
        int ExpiredWaiverCount,
        string? Error);

    private sealed record FindingWaiverApplication(
        string WaiversPath,
        IReadOnlyList<BimOpsFinding> EffectiveFindings,
        int WaivedCount,
        int ActiveWaiverCount,
        int ExpiredWaiverCount);

    private sealed class FindingWaiverDocument
    {
        public string SchemaVersion { get; set; } = "bimops-waivers.v1";
        public List<FindingWaiver> Waivers { get; set; } = new();
    }

    private sealed class FindingWaiver
    {
        public string? FindingId { get; set; }
        public string? RuleId { get; set; }
        public string? Expires { get; set; }
        public string? Reason { get; set; }
    }

    private static Task WriteOutputFormatErrorAsync(TextWriter output) =>
        output.WriteLineAsync("Error: --output must be 'table', 'json', or 'markdown'.");

    private static async Task WriteErrorAsync(
        TextWriter output,
        string outputFormat,
        string schemaVersion,
        string source,
        string message)
    {
        if (outputFormat == "json")
        {
            var report = new ModelHealthErrorReport(
                schemaVersion,
                DateTimeOffset.UtcNow,
                false,
                source,
                message);
            await output.WriteLineAsync(JsonSerializer.Serialize(report, TerminalJsonOptions.CompactContract));
            return;
        }

        await output.WriteLineAsync($"Error: {message}");
    }

    private static async Task<int> WriteFindingsScoreErrorAsync(
        TextWriter output,
        string outputFormat,
        string message)
    {
        var normalized = TerminalOutputFormat.TryNormalize(outputFormat, out var value, OutputFormats)
            ? value
            : "table";

        if (normalized == "json")
        {
            var report = new ModelHealthFindingScoreReport(
                FindingScoreSchemaVersion,
                DateTimeOffset.UtcNow,
                false,
                "",
                "",
                false,
                "",
                "none",
                3,
                0,
                "F",
                new BimOpsFindingSummary(),
                0,
                0,
                0,
                0,
                0,
                message);
            await output.WriteLineAsync(JsonSerializer.Serialize(report, TerminalJsonOptions.CompactContract));
            return 3;
        }

        await output.WriteLineAsync($"Error: {message}");
        return 3;
    }

    private static async Task WriteScoreReportAsync(
        TextWriter output,
        string outputFormat,
        ModelHealthScoreReport report)
    {
        switch (outputFormat)
        {
            case "json":
                await output.WriteLineAsync(JsonSerializer.Serialize(report, TerminalJsonOptions.CompactContract));
                break;
            case "markdown":
                await output.WriteLineAsync("# Model Health Score");
                await output.WriteLineAsync();
                await output.WriteLineAsync($"Schema: `{report.SchemaVersion}`");
                await output.WriteLineAsync($"Source: `{report.Source}`");
                await output.WriteLineAsync($"Score: **{report.Score}/100 ({report.Letter})**");
                break;
            default:
                await output.WriteLineAsync($"Model Health Score: {report.Score}/100 ({report.Letter})");
                break;
        }
    }

    private static async Task WriteHistoryReportAsync(
        TextWriter output,
        string outputFormat,
        ModelHealthHistoryReport report)
    {
        switch (outputFormat)
        {
            case "json":
                await output.WriteLineAsync(JsonSerializer.Serialize(report, TerminalJsonOptions.CompactContract));
                break;
            case "markdown":
                await WriteHistoryMarkdownAsync(output, report);
                break;
            default:
                if (report.Rows.Count == 0)
                {
                    await output.WriteLineAsync($"No snapshots in window ({report.Window}).");
                }
                else
                {
                    await WriteHistoryTableAsync(report.Rows, output);
                }
                break;
        }
    }

    private static async Task WriteFindingsScoreReportAsync(
        TextWriter output,
        string outputFormat,
        ModelHealthFindingScoreReport report)
    {
        switch (outputFormat)
        {
            case "json":
                await output.WriteLineAsync(JsonSerializer.Serialize(report, TerminalJsonOptions.CompactContract));
                break;
            case "markdown":
                await output.WriteLineAsync("# Model Health Finding Score");
                await output.WriteLineAsync();
                await output.WriteLineAsync($"Schema: `{report.SchemaVersion}`");
                await output.WriteLineAsync($"Findings: `{report.FindingsPath}`");
                await output.WriteLineAsync($"Source: `{report.FindingsSource}`");
                await output.WriteLineAsync($"Waivers: `{(string.IsNullOrWhiteSpace(report.WaiversPath) ? "-" : report.WaiversPath)}`");
                await output.WriteLineAsync($"Fail on: `{report.FailOn}`");
                await output.WriteLineAsync($"Exit code: `{report.ExitCode}`");
                await output.WriteLineAsync($"Score: **{report.Score}/100 ({report.Letter})**");
                await output.WriteLineAsync();
                await output.WriteLineAsync("| Severity | Count |");
                await output.WriteLineAsync("|---|---:|");
                await output.WriteLineAsync($"| Blocker | {report.Summary.Blockers} |");
                await output.WriteLineAsync($"| Error | {report.Summary.Errors} |");
                await output.WriteLineAsync($"| Warning | {report.Summary.Warnings} |");
                await output.WriteLineAsync($"| Info | {report.Summary.Info} |");
                await output.WriteLineAsync();
                await output.WriteLineAsync($"Waived findings: `{report.WaivedCount}` of `{report.OriginalFindingCount}`");
                await output.WriteLineAsync($"Active waivers: `{report.ActiveWaiverCount}`");
                await output.WriteLineAsync($"Expired waivers: `{report.ExpiredWaiverCount}`");
                break;
            default:
                await output.WriteLineAsync(
                    $"Model Health Finding Score: {report.Score}/100 ({report.Letter}) failOn={report.FailOn} exitCode={report.ExitCode} findings={report.FindingCount}/{report.OriginalFindingCount} waived={report.WaivedCount} expiredWaivers={report.ExpiredWaiverCount} blockers={report.Summary.Blockers} errors={report.Summary.Errors} warnings={report.Summary.Warnings}");
                break;
        }
    }

    private static async Task WriteHistoryMarkdownAsync(TextWriter output, ModelHealthHistoryReport report)
    {
        await output.WriteLineAsync("# Model Health History");
        await output.WriteLineAsync();
        await output.WriteLineAsync($"Schema: `{report.SchemaVersion}`");
        await output.WriteLineAsync($"Window: `{report.Window}`");
        await output.WriteLineAsync($"Rows: {report.RowCount}");
        await output.WriteLineAsync();

        if (report.Rows.Count == 0)
        {
            await output.WriteLineAsync($"No snapshots in window `{report.Window}`.");
            return;
        }

        await output.WriteLineAsync("| Date | Score | Letter | New | Resolved | Unchanged |");
        await output.WriteLineAsync("|---|---:|---|---:|---:|---:|");
        foreach (var row in report.Rows)
        {
            var score = row.Score?.ToString(CultureInfo.InvariantCulture) ?? "-";
            await output.WriteLineAsync(
                $"| {row.Date} | {score} | {row.Letter} | {row.NewCount} | {row.ResolvedCount} | {row.UnchangedCount} |");
        }
    }

    private static async Task<int> RunScore(RevitClient client)
    {
        var ruleNames = AuditCommand.AvailableRules.ToList();
        var request = new AuditRequest { Rules = ruleNames };

        var result = await client.AuditAsync(request);
        if (!result.Success)
            return -1;

        var data = result.Data!;
        var totalWeight = 0;
        var earnedWeight = 0;

        var issuesByRule = data.Issues.GroupBy(i => i.Rule)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        foreach (var rule in AuditCommand.AvailableRules)
        {
            var weight = RuleWeights.GetValueOrDefault(rule, 5);
            totalWeight += weight;

            var issueCount = issuesByRule.GetValueOrDefault(rule, 0);
            if (issueCount == 0)
            {
                earnedWeight += weight;
            }
            else if (issueCount <= 3)
            {
                earnedWeight += weight / 2;
            }
        }

        return totalWeight == 0 ? 100 : (int)Math.Round(100.0 * earnedWeight / totalWeight);
    }
}
