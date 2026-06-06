using System.Text.Json.Serialization;

namespace RevitCli.Crash;

internal sealed class RevitCrashAnalysisOptions
{
    public int RevitYear { get; init; } = 2026;
    public string? JournalPath { get; init; }
    public TimeSpan Since { get; init; } = TimeSpan.FromHours(24);
    public string SinceText { get; init; } = "24h";
    public bool IncludeEventLog { get; init; } = true;
    public int ContextLines { get; init; } = 20;
    public DateTimeOffset GeneratedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

internal sealed class RevitCrashAnalysisReport
{
    public string SchemaVersion { get; init; } = "revit-crash-analysis.v1";
    public bool Success { get; set; }
    public string Mode { get; set; } = "analyze";
    public DateTimeOffset GeneratedAtUtc { get; init; }
    public int RevitYear { get; init; }
    public string Since { get; init; } = "";
    public DateTimeOffset SinceUtc { get; init; }
    public bool IncludeEventLog { get; init; }
    public RevitCrashSummary Summary { get; set; } = new();
    public List<RevitCrashJournal> Journals { get; } = new();
    public List<RevitCrashEventLogEntry> EventLog { get; } = new();
    public List<RevitCrashFinding> Findings { get; } = new();
    public List<RevitCrashIssue> Issues { get; } = new();
    public List<string> NextCommands { get; } = new();
}

internal sealed class RevitCrashCollectionReport
{
    public string SchemaVersion { get; init; } = "revit-crash-collection.v1";
    public bool Success { get; set; }
    public string OutputDirectory { get; set; } = "";
    public string AnalysisPath { get; set; } = "";
    public string ReadmePath { get; set; } = "";
    public string RedactionReportPath { get; set; } = "";
    public string ManifestPath { get; set; } = "";
    public RevitCrashAnalysisReport Analysis { get; set; } = new();
    public List<RevitCrashCollectedFile> Files { get; } = new();
    public List<RevitCrashIssue> Issues { get; } = new();
}

internal sealed class RevitCrashReproReport
{
    public string SchemaVersion { get; init; } = "revit-crash-repro.v1";
    public bool Success { get; set; } = true;
    public DateTimeOffset GeneratedAtUtc { get; init; }
    public int RevitYear { get; init; }
    public string CaseName { get; init; } = "";
    public string JournalDirectory { get; init; } = "";
    public int RevitProcessCount { get; set; }
    public List<RevitCrashReproProcess> RevitProcesses { get; } = new();
    public List<RevitCrashReproStep> Checklist { get; } = new();
    public List<string> Commands { get; } = new();
}

internal sealed class RevitCrashReproProcess
{
    public int ProcessId { get; init; }
    public string ProcessName { get; init; } = "";
    public DateTimeOffset? StartTimeUtc { get; init; }
}

internal sealed class RevitCrashReproStep
{
    public string StepId { get; init; } = "";
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
}

internal sealed class RevitCrashPacketManifest
{
    public string SchemaVersion { get; set; } = "revit-crash-packet-manifest.v1";
    public DateTimeOffset GeneratedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public string AnalysisSchemaVersion { get; set; } = "";
    public string RedactionSchemaVersion { get; set; } = "";
    public int RevitYear { get; set; }
    public int FindingCount { get; set; }
    public List<RevitCrashPacketManifestFile> Files { get; set; } = new();
}

internal sealed class RevitCrashPacketManifestFile
{
    public string Kind { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public long Bytes { get; set; }
    public string Sha256 { get; set; } = "";
}

internal sealed class RevitCrashVerificationReport
{
    public string SchemaVersion { get; init; } = "revit-crash-packet-verify.v1";
    public bool Success { get; set; }
    public string PacketDirectory { get; set; } = "";
    public string ManifestPath { get; set; } = "";
    public bool HasRedactionReport { get; set; }
    public int ExpectedFileCount { get; set; }
    public int VerifiedFileCount { get; set; }
    public List<RevitCrashPacketVerificationFile> Files { get; } = new();
    public List<RevitCrashIssue> Issues { get; } = new();
}

internal sealed class RevitCrashPacketVerificationFile
{
    public string Kind { get; init; } = "";
    public string RelativePath { get; init; } = "";
    public long ExpectedBytes { get; init; }
    public long ActualBytes { get; set; }
    public string ExpectedSha256 { get; init; } = "";
    public string ActualSha256 { get; set; } = "";
    public bool Exists { get; set; }
    public bool HashMatches { get; set; }
}

internal sealed class RevitCrashSummary
{
    public int JournalCount { get; set; }
    public int EventLogEntryCount { get; set; }
    public int FindingCount { get; set; }
    public int HighConfidenceFindingCount { get; set; }
    public string? MostLikelyRuleId { get; set; }
    public string? MostLikelyTitle { get; set; }
    public string? LastModelPath { get; set; }
    public string? LastCommand { get; set; }
}

internal sealed class RevitCrashJournal
{
    public string Path { get; init; } = "";
    public DateTimeOffset LastWriteUtc { get; init; }
    public long SizeBytes { get; init; }
    public int AnalyzedLineCount { get; set; }
    public string? LastModelPath { get; set; }
    public string? LastCommand { get; set; }
}

internal sealed class RevitCrashFinding
{
    public string RuleId { get; init; } = "";
    public string Severity { get; init; } = "warning";
    public string Confidence { get; init; } = "low";
    public string Title { get; init; } = "";
    public string Message { get; init; } = "";
    public string Source { get; init; } = "";
    public string? Path { get; init; }
    public int? LineNumber { get; init; }
    public string Evidence { get; init; } = "";
    public string? Context { get; init; }
    public List<string> Suggestions { get; init; } = new();
}

internal sealed class RevitCrashIssue
{
    public string Severity { get; init; } = "warning";
    public string Code { get; init; } = "";
    public string Message { get; init; } = "";
    public string? Path { get; init; }
}

internal sealed class RevitCrashEventLogEntry
{
    public DateTimeOffset? TimeCreatedUtc { get; init; }
    public string ProviderName { get; init; } = "";
    public int? Id { get; init; }
    public string Level { get; init; } = "";
    public string Message { get; init; } = "";
}

internal sealed class RevitCrashCollectedFile
{
    public string Kind { get; init; } = "";
    public string SourcePath { get; init; } = "";
    public string CollectedPath { get; init; } = "";
    public long Bytes { get; init; }
}

internal sealed class RevitCrashRedactionReport
{
    public string SchemaVersion { get; init; } = "revit-crash-redaction-report.v1";
    public DateTimeOffset GeneratedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public bool RedactionApplied { get; init; }
    public bool ReviewRequiredBeforeExternalSharing { get; init; } = true;
    public string Policy { get; init; } = "";
    public List<string> SensitiveFields { get; } = new();
    public List<RevitCrashRedactionReviewFile> Files { get; } = new();
}

internal sealed class RevitCrashRedactionReviewFile
{
    public string Kind { get; init; } = "";
    public string RelativePath { get; init; } = "";
    public long Bytes { get; init; }
    public string ReviewNote { get; init; } = "";
}

internal sealed class RevitEventLogResult
{
    public List<RevitCrashEventLogEntry> Entries { get; } = new();
    public List<RevitCrashIssue> Issues { get; } = new();
}

internal interface IRevitEventLogReader
{
    Task<RevitEventLogResult> ReadAsync(
        int revitYear,
        DateTimeOffset sinceUtc,
        CancellationToken cancellationToken = default);
}

internal sealed class NullRevitEventLogReader : IRevitEventLogReader
{
    public Task<RevitEventLogResult> ReadAsync(
        int revitYear,
        DateTimeOffset sinceUtc,
        CancellationToken cancellationToken = default)
    {
        var result = new RevitEventLogResult();
        result.Issues.Add(new RevitCrashIssue
        {
            Severity = "warning",
            Code = "event-log-unavailable",
            Message = "Windows Application Event Log is unavailable in this environment.",
        });
        return Task.FromResult(result);
    }
}
