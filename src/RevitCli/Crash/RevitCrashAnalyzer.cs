using System.Text.RegularExpressions;

namespace RevitCli.Crash;

internal sealed partial class RevitCrashAnalyzer
{
    private readonly IRevitEventLogReader _eventLogReader;

    public RevitCrashAnalyzer(IRevitEventLogReader? eventLogReader = null)
    {
        _eventLogReader = eventLogReader ?? WindowsEventLogReader.CreateDefault();
    }

    public async Task<RevitCrashAnalysisReport> AnalyzeAsync(
        RevitCrashAnalysisOptions options,
        CancellationToken cancellationToken = default)
    {
        var sinceUtc = options.GeneratedAtUtc - options.Since;
        var report = new RevitCrashAnalysisReport
        {
            GeneratedAtUtc = options.GeneratedAtUtc,
            RevitYear = options.RevitYear,
            Since = options.SinceText,
            SinceUtc = sinceUtc,
            IncludeEventLog = options.IncludeEventLog,
        };

        var journals = RevitJournalLocator.FindJournals(
            options.RevitYear,
            options.JournalPath,
            sinceUtc,
            report.Issues);

        foreach (var journalPath in journals)
        {
            try
            {
                var parsed = RevitJournalParser.Parse(journalPath);
                report.Journals.Add(parsed.Journal);
                AnalyzeJournal(parsed, options.ContextLines, report);
            }
            catch (IOException ex)
            {
                report.Issues.Add(Issue("warning", "journal-read-failed", ex.Message, journalPath));
            }
            catch (UnauthorizedAccessException ex)
            {
                report.Issues.Add(Issue("warning", "journal-read-failed", ex.Message, journalPath));
            }
        }

        if (options.IncludeEventLog)
        {
            var eventLog = await _eventLogReader.ReadAsync(options.RevitYear, sinceUtc, cancellationToken);
            report.EventLog.AddRange(eventLog.Entries);
            report.Issues.AddRange(eventLog.Issues);
            AnalyzeEventLog(eventLog.Entries, report);
        }

        FinalizeReport(report);
        return report;
    }

    private static void AnalyzeJournal(
        RevitJournalParseResult parsed,
        int contextLines,
        RevitCrashAnalysisReport report)
    {
        foreach (var line in parsed.Lines)
        {
            ClassifyText(
                line.Text,
                "journal",
                parsed.Journal.Path,
                line.Number,
                () => RevitJournalParser.BuildContext(parsed.Lines, line.Number, contextLines),
                report);
        }
    }

    private static void AnalyzeEventLog(
        IReadOnlyList<RevitCrashEventLogEntry> entries,
        RevitCrashAnalysisReport report)
    {
        foreach (var entry in entries)
        {
            var source = string.IsNullOrWhiteSpace(entry.ProviderName)
                ? "Windows Application Event Log"
                : entry.ProviderName;
            ClassifyText(entry.Message, "event-log", source, null, null, report);
        }
    }

    private static void ClassifyText(
        string text,
        string source,
        string? path,
        int? lineNumber,
        Func<string?>? contextFactory,
        RevitCrashAnalysisReport report)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        if (ManagedExceptionRegex().IsMatch(text))
        {
            AddFinding(
                report,
                "managed-dotnet-exception",
                "critical",
                "high",
                ".NET managed exception",
                "Revit recorded ExceptionCode=0xe0434352, which usually means a managed .NET exception surfaced inside Revit or an add-in.",
                source,
                path,
                lineNumber,
                text,
                contextFactory,
                new[]
                {
                    "Update Revit to the current build for the target year.",
                    "Temporarily disable third-party add-ins, then re-enable one at a time to isolate the failing add-in.",
                    "If the crash happens during startup, check or recreate the Revit user.config profile.",
                    "Use crash collect to preserve the journal and event-log evidence before escalation.",
                });
        }

        if (AddinRegex().IsMatch(text))
        {
            AddFinding(
                report,
                "addin-api-or-assembly-failure",
                "critical",
                "high",
                "Add-in API or assembly failure",
                "The crash evidence points at an add-in API error, external command, or .NET assembly load/version problem.",
                source,
                path,
                lineNumber,
                text,
                contextFactory,
                new[]
                {
                    "Start Revit with third-party add-ins disabled and retry the same workflow.",
                    "Update or remove the add-in or assembly named in the evidence line.",
                    "Re-enable add-ins one by one to isolate the failing external command.",
                });
        }

        if (SchemaRegex().IsMatch(text))
        {
            AddFinding(
                report,
                "missing-extensible-storage-schema",
                "warning",
                "medium",
                "Missing Extensible Storage schema",
                "The journal mentions Missing ESSchema or Extensible Storage schema issues, which can indicate model/plugin schema drift.",
                source,
                path,
                lineNumber,
                text,
                contextFactory,
                new[]
                {
                    "Open the model with Audit enabled and save a repaired copy.",
                    "Check whether the model expects a plugin/version that owns the missing schema.",
                    "Coordinate schema recovery with the BIM manager before deleting or rewriting extensible storage.",
                });
        }

        if (CloudRegex().IsMatch(text))
        {
            AddFinding(
                report,
                "cloud-worksharing-or-sync",
                "warning",
                "medium",
                "Cloud/worksharing sync issue",
                "The crash evidence references Autodesk Docs/BIM 360/ACC, collaboration cache, central sync, or cloud model access.",
                source,
                path,
                lineNumber,
                text,
                contextFactory,
                new[]
                {
                    "Check Autodesk Docs/BIM 360/ACC access and service status for the affected user.",
                    "Try a detached or local copy to separate cloud sync from model corruption.",
                    "Only rebuild local collaboration cache after backup and according to Autodesk support guidance.",
                });
        }

        if (MemoryRegex().IsMatch(text))
        {
            AddFinding(
                report,
                "memory-pressure",
                "warning",
                "medium",
                "Memory pressure",
                "The crash evidence references memory pressure or an OutOfMemoryException.",
                source,
                path,
                lineNumber,
                text,
                contextFactory,
                new[]
                {
                    "Close other high-memory applications and retry with a local copy.",
                    "Check RAM/page-file pressure during the same open/export operation.",
                    "Audit large imports, linked files, or unusually large families if the crash repeats.",
                });
        }

        if (GraphicsRegex().IsMatch(text))
        {
            AddFinding(
                report,
                "graphics-driver",
                "warning",
                "medium",
                "Graphics or display driver issue",
                "The crash evidence references graphics, DirectX, GPU, or display-driver failures.",
                source,
                path,
                lineNumber,
                text,
                contextFactory,
                new[]
                {
                    "Update or roll back the graphics driver and retry.",
                    "Test with hardware acceleration disabled to isolate graphics-driver involvement.",
                    "Compare the workstation driver against Autodesk certified graphics guidance.",
                });
        }

        if (GenericCrashRegex().IsMatch(text))
        {
            AddFinding(
                report,
                "generic-fatal-crash-context",
                "warning",
                "low",
                "Fatal crash context",
                "Revit recorded fatal crash context, but no higher-confidence rule matched this line.",
                source,
                path,
                lineNumber,
                text,
                contextFactory,
                new[]
                {
                    "Review the last command and model path in the summary.",
                    "Retry with Audit or a detached/local copy if the crash appears model-specific.",
                    "Run crash collect before changing add-ins, profiles, or caches.",
                });
        }
    }

    private static void AddFinding(
        RevitCrashAnalysisReport report,
        string ruleId,
        string severity,
        string confidence,
        string title,
        string message,
        string source,
        string? path,
        int? lineNumber,
        string evidence,
        Func<string?>? contextFactory,
        IReadOnlyList<string> suggestions)
    {
        if (report.Findings.Any(finding =>
                string.Equals(finding.RuleId, ruleId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(finding.Source, source, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var context = contextFactory?.Invoke();
        report.Findings.Add(new RevitCrashFinding
        {
            RuleId = ruleId,
            Severity = severity,
            Confidence = confidence,
            Title = title,
            Message = message,
            Source = source,
            Path = path,
            LineNumber = lineNumber,
            Evidence = RevitJournalParser.TrimEvidence(evidence),
            Context = string.IsNullOrWhiteSpace(context) ? null : context,
            Suggestions = suggestions.ToList(),
        });
    }

    private static void FinalizeReport(RevitCrashAnalysisReport report)
    {
        report.Findings.Sort(CompareFindings);

        var bestFinding = report.Findings.FirstOrDefault();
        report.Summary = new RevitCrashSummary
        {
            JournalCount = report.Journals.Count,
            EventLogEntryCount = report.EventLog.Count,
            FindingCount = report.Findings.Count,
            HighConfidenceFindingCount = report.Findings.Count(finding =>
                string.Equals(finding.Confidence, "high", StringComparison.OrdinalIgnoreCase)),
            MostLikelyRuleId = bestFinding?.RuleId,
            MostLikelyTitle = bestFinding?.Title,
            LastModelPath = report.Journals
                .Select(journal => journal.LastModelPath)
                .LastOrDefault(path => !string.IsNullOrWhiteSpace(path)),
            LastCommand = report.Journals
                .Select(journal => journal.LastCommand)
                .LastOrDefault(command => !string.IsNullOrWhiteSpace(command)),
        };

        if (report.Journals.Count == 0 && report.EventLog.Count == 0)
        {
            report.Issues.Add(Issue(
                "error",
                "no-crash-evidence",
                "No Revit journal files or Windows Application Event Log entries were available for the requested window.",
                null));
        }

        report.Success = report.Issues.All(issue =>
            !string.Equals(issue.Severity, "error", StringComparison.OrdinalIgnoreCase));

        report.NextCommands.Add("revitcli crash collect --year " + report.RevitYear + " --output-dir .revitcli/crash/latest --output markdown");
        report.NextCommands.Add("revitcli library check --year " + report.RevitYear + " --output markdown");
        report.NextCommands.Add("revitcli doctor --check-version " + report.RevitYear + " --output json");
    }

    private static int CompareFindings(RevitCrashFinding left, RevitCrashFinding right)
    {
        var confidence = ConfidenceRank(right.Confidence).CompareTo(ConfidenceRank(left.Confidence));
        if (confidence != 0)
            return confidence;

        var severity = SeverityRank(right.Severity).CompareTo(SeverityRank(left.Severity));
        if (severity != 0)
            return severity;

        return string.Compare(left.RuleId, right.RuleId, StringComparison.OrdinalIgnoreCase);
    }

    private static int ConfidenceRank(string confidence) =>
        confidence.ToLowerInvariant() switch
        {
            "high" => 3,
            "medium" => 2,
            "low" => 1,
            _ => 0,
        };

    private static int SeverityRank(string severity) =>
        severity.ToLowerInvariant() switch
        {
            "critical" => 3,
            "warning" => 2,
            "info" => 1,
            _ => 0,
        };

    private static RevitCrashIssue Issue(string severity, string code, string message, string? path) =>
        new()
        {
            Severity = severity,
            Code = code,
            Message = message,
            Path = path,
        };

    [GeneratedRegex("ExceptionCode\\s*[:=]\\s*0x?e0434352", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ManagedExceptionRegex();

    [GeneratedRegex("API_ERROR|Add-?In|ExternalCommand|ExternalApplication|Could not load file or assembly|FileNotFoundException|TypeLoadException|MethodAccessException|assembly version", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AddinRegex();

    [GeneratedRegex("Missing\\s+ESSchema|Extensible\\s*Storage|ExtensibleStorage", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SchemaRegex();

    [GeneratedRegex("BIM\\s*360|ACC|Autodesk\\s+Docs|CollaborationCache|cloud\\s+model|Sync\\s+with\\s+Central|Synchroni[sz]e\\s+with\\s+Central|central\\s+model|C4R", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CloudRegex();

    [GeneratedRegex("OutOfMemoryException|out of memory|not enough memory|insufficient memory", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MemoryRegex();

    [GeneratedRegex("DirectX|graphics driver|display driver|GPU|hardware acceleration", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GraphicsRegex();

    [GeneratedRegex("fatal error|unrecoverable error|serious error|crash|dump file|Windows Error Reporting|Revit has stopped", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GenericCrashRegex();
}
