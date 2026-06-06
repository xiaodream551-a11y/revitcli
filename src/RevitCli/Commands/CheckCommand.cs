using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using RevitCli.Checks;
using RevitCli.Client;
using RevitCli.Output;
using RevitCli.Reports;
using RevitCli.Shared;
using Spectre.Console;

namespace RevitCli.Commands;

public static class CheckCommand
{
    private static readonly HashSet<string> ValidOutputFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "table",
        "json",
        "html",
        AuditReportFormats.Sarif,
        AuditReportFormats.PrComment,
    };

    public static Command Create(RevitClient client)
    {
        var nameArg = new Argument<string?>("name", () => null, "Check set name (default: 'default')");
        var profileOpt = new Option<string?>("--profile", "Path to .revitcli.yml profile");
        var outputOpt = new Option<string>("--output", () => "table", "Output format: table, json, html, sarif, pr-comment");
        var reportOpt = new Option<string?>("--report", "Save report to file (format inferred from extension, or uses --output)");
        var noSaveOpt = new Option<bool>("--no-save", "Don't save results for diff comparison");
        var findingsOpt = new Option<bool>("--findings", "Emit a bimops-finding.v1 JSON envelope instead of the legacy check report");

        var command = new Command("check", "Run project checks from .revitcli.yml profile")
        {
            nameArg, profileOpt, outputOpt, reportOpt, noSaveOpt, findingsOpt
        };

        command.SetHandler(async (name, profilePath, outputFormat, reportPath, noSave, findings) =>
        {
            Environment.ExitCode = await ExecuteAsync(client, name, profilePath, outputFormat, reportPath, noSave, Console.Out, findings);
        }, nameArg, profileOpt, outputOpt, reportOpt, noSaveOpt, findingsOpt);

        return command;
    }

    public static Task<int> ExecuteAsync(RevitClient client, string? name, string? profilePath,
        string outputFormat, string? reportPath, bool noSave, TextWriter output)
        => ExecuteAsync(client, name, profilePath, outputFormat, reportPath, noSave, true, output);

    public static Task<int> ExecuteAsync(RevitClient client, string? name, string? profilePath,
        string outputFormat, string? reportPath, bool noSave, TextWriter output, bool findings)
        => ExecuteAsync(client, name, profilePath, outputFormat, reportPath, noSave, true, output, findings);

    internal static async Task<int> ExecuteAsync(RevitClient client, string? name, string? profilePath,
        string outputFormat, string? reportPath, bool noSave, bool sendNotify, TextWriter output, bool findings = false)
    {
        if (!TryResolveFormat(outputFormat, reportPath, out var format))
        {
            await output.WriteLineAsync(
                "Error: --output must be one of: table, json, html, sarif, pr-comment.");
            return 1;
        }
        if (findings && format != "json")
        {
            await output.WriteLineAsync("Error: --findings currently requires '--output json'.");
            return 1;
        }

        var run = await CheckRunner.RunAsync(client, name, profilePath);
        if (!run.Success)
        {
            if (findings)
            {
                await output.WriteLineAsync(JsonSerializer.Serialize(
                    ToRunFailureFindingEnvelope(name ?? "default", profilePath, run.Error),
                    TerminalJsonOptions.PrettyIgnoreNull));
                return 1;
            }

            await WriteRunFailureAsync(name ?? "default", format, run.Error, output);
            return 1;
        }

        var checkName = run.Data!.CheckName;
        var profile = run.Data.Profile;
        var checkDef = run.Data.CheckDefinition;
        var allIssues = run.Data.Issues;
        var suppressedCount = run.Data.SuppressedCount;
        var displayPassed = run.Data.DisplayPassed;
        var displayFailed = run.Data.DisplayFailed;

        // Determine exit code based on failOn before rendering so JSON callers
        // get the same gate result the process returns.
        var failOn = checkDef.FailOn.ToLowerInvariant();
        var hasErrors = allIssues.Any(i => i.Severity == "error");
        var hasWarnings = allIssues.Any(i => i.Severity == "warning");

        var exitCode = 0;
        if (failOn == "error" && hasErrors)
            exitCode = 1;
        else if (failOn == "warning" && (hasErrors || hasWarnings))
            exitCode = 1;

        string rendered;
        if (AuditReportFormats.TryRender(format, allIssues, out var ciContent))
        {
            rendered = ciContent;
        }
        else
        {
            rendered = findings
                ? JsonSerializer.Serialize(
                    ToFindingEnvelope(checkName, run.Data.ProfilePath, allIssues),
                    TerminalJsonOptions.PrettyIgnoreNull)
                : format switch
            {
                "json" => CheckReportRenderer.RenderJson(
                    checkName,
                    displayPassed,
                    displayFailed,
                    allIssues,
                    suppressedCount,
                    exitCode,
                    failOn),
                "html" => CheckReportRenderer.RenderHtml(checkName, displayPassed, displayFailed, allIssues, suppressedCount),
                _ => CheckReportRenderer.RenderTable(checkName, displayPassed, displayFailed, allIssues, suppressedCount)
            };
        }

        // Write to file if --report specified
        if (reportPath != null)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(reportPath));
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            await File.WriteAllTextAsync(reportPath, rendered);
            await output.WriteLineAsync($"Report saved to {reportPath}");

            // Also print summary to console
            await output.WriteLineAsync(
                CheckReportRenderer.RenderTable(checkName, displayPassed, displayFailed, allIssues, suppressedCount));
        }
        else
        {
            await output.WriteLineAsync(rendered);
        }

        // Diff + save (only for table output — don't pollute JSON/HTML)
        var profileDir = run.Data.ProfilePath != null
            ? Path.GetDirectoryName(run.Data.ProfilePath)
            : null;

        if (!noSave)
        {
            try
            {
                // Diff against latest (before rotation)
                var diff = CheckResultStore.ComputeDiffAgainstLatest(checkName, allIssues, profileDir);
                CheckResultStore.Save(checkName, displayPassed, displayFailed, suppressedCount, allIssues, profileDir);

                // Only print diff to console for table format (not JSON/HTML)
                if (diff != null && format == "table")
                {
                    await output.WriteLineAsync("");
                    await output.WriteLineAsync(
                        $"vs previous: {diff.New.Count} new, {diff.Resolved.Count} resolved, {diff.Unchanged} unchanged");

                    foreach (var r in diff.Resolved.Take(5))
                        await output.WriteLineAsync($"  [RESOLVED] {r.Rule}: {r.Message}");
                    if (diff.Resolved.Count > 5)
                        await output.WriteLineAsync($"  ... and {diff.Resolved.Count - 5} more resolved");

                    foreach (var n in diff.New.Take(5))
                        await output.WriteLineAsync($"  [NEW] {n.Rule}: {n.Message}");
                    if (diff.New.Count > 5)
                        await output.WriteLineAsync($"  ... and {diff.New.Count - 5} more new issues");
                }
            }
            catch (Exception ex)
            {
                // Don't let history I/O failures break the audit result
                await Console.Error.WriteLineAsync($"Warning: failed to save check results: {ex.Message}");
            }
        }

        // Webhook notification (suppressed when called from publish precheck).
        // Mirrors PublishCommand's pattern: best-effort, never affects exit code.
        if (sendNotify
            && !string.IsNullOrWhiteSpace(profile.Defaults.Notify)
            && profile.Defaults.Notify.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            await WebhookNotifier.NotifyCheckAsync(
                profile.Defaults.Notify,
                checkName,
                displayPassed,
                displayFailed,
                suppressedCount,
                severityFailed: exitCode != 0,
                profilePath: run.Data.ProfilePath);
        }

        return exitCode;
    }

    private static bool TryResolveFormat(string? outputFormat, string? reportPath, out string format)
    {
        format = string.IsNullOrWhiteSpace(outputFormat)
            ? "table"
            : outputFormat.Trim().ToLowerInvariant();

        if (!string.IsNullOrWhiteSpace(reportPath))
        {
            var ext = Path.GetExtension(reportPath).ToLowerInvariant();
            if (ext == ".html" || ext == ".htm")
                format = "html";
            else if (ext == ".json")
                format = "json";
            else
            {
                // Let the v1.7 report formats (sarif/pr-comment) infer from .sarif / .md.
                var inferred = AuditReportFormats.InferFormatFromExtension(ext);
                if (inferred != null)
                    format = inferred;
            }
        }

        return ValidOutputFormats.Contains(format);
    }

    private static BimOpsFindingEnvelope ToFindingEnvelope(
        string checkName,
        string? profilePath,
        IReadOnlyList<AuditIssue> issues)
    {
        var envelope = new BimOpsFindingEnvelope
        {
            Source = "check",
            GeneratedAtUtc = DateTime.UtcNow.ToString("o"),
            RequiresRevit = true,
            PolicyPack = string.IsNullOrWhiteSpace(profilePath)
                ? null
                : new BimOpsPolicyPackReference
                {
                    Name = checkName,
                    Version = "",
                    Path = profilePath,
                },
            Summary = new BimOpsFindingSummary
            {
                Info = issues.Count(issue => string.Equals(issue.Severity, "info", StringComparison.OrdinalIgnoreCase)),
                Warnings = issues.Count(issue => string.Equals(issue.Severity, "warning", StringComparison.OrdinalIgnoreCase)),
                Errors = issues.Count(issue => string.Equals(issue.Severity, "error", StringComparison.OrdinalIgnoreCase)),
                Blockers = issues.Count(issue => string.Equals(issue.Severity, "blocker", StringComparison.OrdinalIgnoreCase)),
            },
        };

        var index = 1;
        foreach (var issue in issues)
        {
            var rule = string.IsNullOrWhiteSpace(issue.Rule) ? "unknown" : issue.Rule;
            envelope.Findings.Add(new BimOpsFinding
            {
                FindingId = $"CHK-{index++:D3}-{NormalizeFindingToken(rule)}",
                RuleId = $"check.{NormalizeRuleId(rule)}",
                Source = "check",
                Severity = NormalizeFindingSeverity(issue.Severity),
                Category = string.IsNullOrWhiteSpace(issue.Category) ? "model-health" : issue.Category!,
                Message = issue.Message,
                Confidence = 1.0,
                RequiresRevit = true,
                Fixability = BimOpsFindingSchema.FixabilityManual,
                Target = new BimOpsFindingTarget
                {
                    ElementIds = issue.ElementId.HasValue ? new List<long> { issue.ElementId.Value } : new List<long>(),
                    Category = issue.Category,
                    Parameter = issue.Parameter,
                    ArtifactPath = profilePath,
                },
                Evidence =
                {
                    new BimOpsFindingEvidence
                    {
                        Kind = "check-audit",
                        Source = issue.Source ?? checkName,
                        Locator = issue.ElementId.HasValue
                            ? $"element:{issue.ElementId.Value}"
                            : issue.Target ?? profilePath,
                        Value = FormatAuditValue(issue),
                    },
                },
                Remediation = new BimOpsFindingRemediation
                {
                    Mode = BimOpsFindingSchema.FixabilityManual,
                    VerifyCommand = BuildCheckVerifyCommand(checkName, profilePath),
                    NextAction = "Review the model issue, update the model or policy waiver, then rerun check.",
                },
            });
        }

        return envelope;
    }

    private static BimOpsFindingEnvelope ToRunFailureFindingEnvelope(string checkName, string? profilePath, string error)
    {
        var connectionFailure = IsConnectionFailure(error);
        var category = connectionFailure ? "connection" : "profile";
        var nextAction = connectionFailure
            ? "Run 'revitcli doctor' to diagnose connection issues, then rerun check."
            : "Fix the check profile or check-set name, then rerun check.";
        return new BimOpsFindingEnvelope
        {
            Source = "check",
            GeneratedAtUtc = DateTime.UtcNow.ToString("o"),
            RequiresRevit = connectionFailure,
            Summary = new BimOpsFindingSummary { Errors = 1 },
            Findings =
            {
                new BimOpsFinding
                {
                    FindingId = "CHK-001-RUN-FAILED",
                    RuleId = "check.run-failed",
                    Source = "check",
                    Severity = BimOpsFindingSchema.SeverityError,
                    Category = category,
                    Message = error,
                    Confidence = 1.0,
                    RequiresRevit = connectionFailure,
                    Fixability = BimOpsFindingSchema.FixabilityManual,
                    Target = new BimOpsFindingTarget
                    {
                        ArtifactPath = profilePath,
                        Category = category,
                        Parameter = "run-failed",
                    },
                    Evidence =
                    {
                        new BimOpsFindingEvidence
                        {
                            Kind = "check-run",
                            Source = "check.v1",
                            Locator = profilePath,
                            Value = "run-failed",
                        },
                    },
                    Remediation = new BimOpsFindingRemediation
                    {
                        Mode = BimOpsFindingSchema.FixabilityManual,
                        VerifyCommand = BuildCheckVerifyCommand(checkName, profilePath),
                        NextAction = nextAction,
                    },
                },
            },
        };
    }

    private static bool IsConnectionFailure(string error) =>
        error.Contains("not running", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("connection", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("refused", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("localhost", StringComparison.OrdinalIgnoreCase);

    private static string BuildCheckVerifyCommand(string checkName, string? profilePath)
    {
        var parts = new List<string> { "revitcli", "check", QuoteArgument(checkName) };
        if (!string.IsNullOrWhiteSpace(profilePath))
        {
            parts.Add("--profile");
            parts.Add(QuoteArgument(profilePath!));
        }

        parts.Add("--findings");
        parts.Add("--output");
        parts.Add("json");
        return string.Join(" ", parts);
    }

    private static string FormatAuditValue(AuditIssue issue)
    {
        if (!string.IsNullOrWhiteSpace(issue.CurrentValue) || !string.IsNullOrWhiteSpace(issue.ExpectedValue))
            return $"{issue.CurrentValue ?? ""} -> {issue.ExpectedValue ?? ""}";
        return issue.Rule;
    }

    private static string NormalizeFindingSeverity(string severity) =>
        severity.ToLowerInvariant() switch
        {
            "info" => BimOpsFindingSchema.SeverityInfo,
            "warning" => BimOpsFindingSchema.SeverityWarning,
            "error" => BimOpsFindingSchema.SeverityError,
            "blocker" => BimOpsFindingSchema.SeverityBlocker,
            _ => BimOpsFindingSchema.SeverityWarning,
        };

    private static string NormalizeRuleId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "unknown";

        var chars = value.Trim()
            .Select(ch => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '.')
            .ToArray();
        var ruleId = new string(chars).Trim('.');
        while (ruleId.Contains("..", StringComparison.Ordinal))
            ruleId = ruleId.Replace("..", ".", StringComparison.Ordinal);

        return string.IsNullOrWhiteSpace(ruleId) ? "unknown" : ruleId;
    }

    private static string NormalizeFindingToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "unknown";

        var chars = value
            .Trim()
            .Select(ch => char.IsLetterOrDigit(ch) ? char.ToUpperInvariant(ch) : '-')
            .ToArray();
        var token = new string(chars).Trim('-');
        while (token.Contains("--", StringComparison.Ordinal))
            token = token.Replace("--", "-", StringComparison.Ordinal);

        return string.IsNullOrWhiteSpace(token) ? "unknown" : token;
    }

    private static string QuoteArgument(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "\"\"";
        return value.Any(char.IsWhiteSpace)
            ? $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
            : value;
    }

    private static async Task WriteRunFailureAsync(
        string checkName,
        string format,
        string error,
        TextWriter output)
    {
        var hint = error.Contains("not running", StringComparison.OrdinalIgnoreCase)
            ? "Run 'revitcli doctor' to diagnose connection issues."
            : null;

        if (format == "json")
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(
                new CheckErrorOutput("check.v1", false, checkName, error, hint),
                TerminalJsonOptions.PrettyIgnoreNull));
            return;
        }

        await output.WriteLineAsync(error);
        if (hint != null)
            await output.WriteLineAsync($"  {hint}");
    }

    private sealed record CheckErrorOutput(
        [property: JsonPropertyName("schemaVersion")] string SchemaVersion,
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("check")] string Check,
        [property: JsonPropertyName("error")] string Error,
        [property: JsonPropertyName("hint")] string? Hint);
}
