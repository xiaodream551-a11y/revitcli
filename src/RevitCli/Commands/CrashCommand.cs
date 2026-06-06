using System.CommandLine;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using RevitCli.Crash;
using RevitCli.Output;

namespace RevitCli.Commands;

public static class CrashCommand
{
    public static Command Create()
    {
        var command = new Command("crash", "Analyze Revit crash journals and collect local diagnostic evidence");
        command.AddCommand(CreateAnalyzeCommand());
        command.AddCommand(CreateCollectCommand());
        command.AddCommand(CreateReproCommand());
        command.AddCommand(CreateVerifyCommand());
        return command;
    }

    private static Command CreateAnalyzeCommand()
    {
        var yearOpt = new Option<int>("--year", () => 2026, "Revit version year: 2024, 2025, or 2026");
        var journalOpt = new Option<string?>("--journal", "Analyze a specific Revit journal file");
        var sinceOpt = new Option<string>("--since", () => "24h", "Time window such as 24h, 7d, or 60m");
        var includeEventLogOpt = CreateIncludeEventLogOption();
        var contextLinesOpt = new Option<int>("--context-lines", () => 20, "Journal context lines around matched evidence");
        var outputOpt = new Option<string>("--output", () => "table", "Output format: table, json, markdown");

        var command = new Command("analyze", "Analyze recent Revit crash evidence and suggest next steps")
        {
            yearOpt,
            journalOpt,
            sinceOpt,
            includeEventLogOpt,
            contextLinesOpt,
            outputOpt,
        };

        command.SetHandler(async (year, journal, since, includeEventLog, contextLines, outputFormat) =>
        {
            Environment.ExitCode = await ExecuteAnalyzeAsync(
                year,
                journal,
                since,
                includeEventLog,
                contextLines,
                outputFormat,
                Console.Out);
        }, yearOpt, journalOpt, sinceOpt, includeEventLogOpt, contextLinesOpt, outputOpt);

        return command;
    }

    private static Command CreateCollectCommand()
    {
        var yearOpt = new Option<int>("--year", () => 2026, "Revit version year: 2024, 2025, or 2026");
        var journalOpt = new Option<string?>("--journal", "Collect and analyze a specific Revit journal file");
        var sinceOpt = new Option<string>("--since", () => "24h", "Time window such as 24h, 7d, or 60m");
        var includeEventLogOpt = CreateIncludeEventLogOption();
        var contextLinesOpt = new Option<int>("--context-lines", () => 20, "Journal context lines around matched evidence");
        var outputDirOpt = new Option<string>("--output-dir", () => ".revitcli/crash/latest", "Directory for the local crash evidence packet");
        var outputOpt = new Option<string>("--output", () => "table", "Output format: table, json, markdown");

        var command = new Command("collect", "Write a local crash diagnostics packet with copied journals, analysis JSON, and redaction report")
        {
            yearOpt,
            journalOpt,
            sinceOpt,
            includeEventLogOpt,
            contextLinesOpt,
            outputDirOpt,
            outputOpt,
        };

        command.SetHandler(async (year, journal, since, includeEventLog, contextLines, outputDir, outputFormat) =>
        {
            Environment.ExitCode = await ExecuteCollectAsync(
                year,
                journal,
                since,
                includeEventLog,
                contextLines,
                outputDir,
                outputFormat,
                Console.Out);
        }, yearOpt, journalOpt, sinceOpt, includeEventLogOpt, contextLinesOpt, outputDirOpt, outputOpt);

        return command;
    }

    private static Command CreateReproCommand()
    {
        var yearOpt = new Option<int>("--year", () => 2026, "Revit version year: 2024, 2025, or 2026");
        var caseOpt = new Option<string>("--case", () => "revit-crash", "Short support case or workflow label");
        var outputOpt = new Option<string>("--output", () => "markdown", "Output format: table, json, markdown");

        var command = new Command("repro", "Generate a clean Revit crash reproduction checklist")
        {
            yearOpt,
            caseOpt,
            outputOpt,
        };

        command.SetHandler(async (year, caseName, outputFormat) =>
        {
            Environment.ExitCode = await ExecuteReproAsync(
                year,
                caseName,
                outputFormat,
                Console.Out);
        }, yearOpt, caseOpt, outputOpt);

        return command;
    }

    private static Command CreateVerifyCommand()
    {
        var packetOpt = new Option<string>("--packet", () => ".revitcli/crash/latest", "Crash evidence packet directory");
        var outputOpt = new Option<string>("--output", () => "table", "Output format: table, json, markdown");

        var command = new Command("verify", "Verify a local crash diagnostics packet manifest")
        {
            packetOpt,
            outputOpt,
        };

        command.SetHandler(async (packet, outputFormat) =>
        {
            Environment.ExitCode = await ExecuteVerifyAsync(packet, outputFormat, Console.Out);
        }, packetOpt, outputOpt);

        return command;
    }

    internal static async Task<int> ExecuteReproAsync(
        int year,
        string caseName,
        string outputFormat,
        TextWriter output,
        DateTimeOffset? generatedAtUtc = null,
        Func<IReadOnlyList<RevitCrashReproProcess>>? processProvider = null)
    {
        if (!TerminalOutputFormat.TryNormalize(outputFormat, out var normalizedOutput, "table", "json", "markdown"))
        {
            await output.WriteLineAsync("Error: --output must be 'table', 'json', or 'markdown'.");
            return 1;
        }

        if (year is < 2024 or > 2026)
        {
            await output.WriteLineAsync($"Error: unsupported Revit year {year}. Supported years: 2024, 2025, 2026.");
            return 1;
        }

        var report = BuildReproReport(year, caseName, generatedAtUtc ?? DateTimeOffset.UtcNow, processProvider);
        await WriteOutputAsync(output, report, normalizedOutput);
        return 0;
    }

    internal static async Task<int> ExecuteVerifyAsync(
        string packetDirectory,
        string outputFormat,
        TextWriter output)
    {
        if (!TerminalOutputFormat.TryNormalize(outputFormat, out var normalizedOutput, "table", "json", "markdown"))
        {
            await output.WriteLineAsync("Error: --output must be 'table', 'json', or 'markdown'.");
            return 1;
        }

        var report = VerifyPacket(packetDirectory);
        await WriteOutputAsync(output, report, normalizedOutput);
        return report.Success ? 0 : 1;
    }

    internal static async Task<int> ExecuteAnalyzeAsync(
        int year,
        string? journal,
        string since,
        bool includeEventLog,
        int contextLines,
        string outputFormat,
        TextWriter output,
        IRevitEventLogReader? eventLogReader = null,
        DateTimeOffset? generatedAtUtc = null)
    {
        if (!TerminalOutputFormat.TryNormalize(outputFormat, out var normalizedOutput, "table", "json", "markdown"))
        {
            await output.WriteLineAsync("Error: --output must be 'table', 'json', or 'markdown'.");
            return 1;
        }

        if (!TryCreateOptions(year, journal, since, includeEventLog, contextLines, generatedAtUtc, out var options, out var error))
        {
            await output.WriteLineAsync($"Error: {error}");
            return 1;
        }

        var analyzer = new RevitCrashAnalyzer(eventLogReader);
        var report = await analyzer.AnalyzeAsync(options);
        await WriteOutputAsync(output, report, normalizedOutput);
        return report.Success ? 0 : 1;
    }

    internal static async Task<int> ExecuteCollectAsync(
        int year,
        string? journal,
        string since,
        bool includeEventLog,
        int contextLines,
        string outputDirectory,
        string outputFormat,
        TextWriter output,
        IRevitEventLogReader? eventLogReader = null,
        DateTimeOffset? generatedAtUtc = null)
    {
        if (!TerminalOutputFormat.TryNormalize(outputFormat, out var normalizedOutput, "table", "json", "markdown"))
        {
            await output.WriteLineAsync("Error: --output must be 'table', 'json', or 'markdown'.");
            return 1;
        }

        if (!TryCreateOptions(year, journal, since, includeEventLog, contextLines, generatedAtUtc, out var options, out var error))
        {
            await output.WriteLineAsync($"Error: {error}");
            return 1;
        }

        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            await output.WriteLineAsync("Error: --output-dir is required.");
            return 1;
        }

        var analyzer = new RevitCrashAnalyzer(eventLogReader);
        var analysis = await analyzer.AnalyzeAsync(options);
        var collection = CollectEvidence(analysis, outputDirectory);
        await WriteOutputAsync(output, collection, normalizedOutput);
        return collection.Success ? 0 : 1;
    }

    private static Option<bool> CreateIncludeEventLogOption() =>
        new("--include-event-log", () => true, "Include Windows Application Event Log when available")
        {
            Arity = ArgumentArity.ZeroOrOne,
        };

    private static bool TryCreateOptions(
        int year,
        string? journal,
        string since,
        bool includeEventLog,
        int contextLines,
        DateTimeOffset? generatedAtUtc,
        out RevitCrashAnalysisOptions options,
        out string error)
    {
        options = new RevitCrashAnalysisOptions();
        error = "";

        if (year is < 2024 or > 2026)
        {
            error = $"unsupported Revit year {year}. Supported years: 2024, 2025, 2026.";
            return false;
        }

        TimeSpan sinceWindow;
        try
        {
            sinceWindow = HistoryCommand.ParseWindow(since);
        }
        catch (FormatException ex)
        {
            error = ex.Message;
            return false;
        }

        if (contextLines < 0 || contextLines > 200)
        {
            error = "--context-lines must be between 0 and 200.";
            return false;
        }

        options = new RevitCrashAnalysisOptions
        {
            RevitYear = year,
            JournalPath = journal,
            Since = sinceWindow,
            SinceText = since,
            IncludeEventLog = includeEventLog,
            ContextLines = contextLines,
            GeneratedAtUtc = generatedAtUtc ?? DateTimeOffset.UtcNow,
        };
        return true;
    }

    private static RevitCrashCollectionReport CollectEvidence(
        RevitCrashAnalysisReport analysis,
        string outputDirectory)
    {
        var root = Path.GetFullPath(RevitJournalLocator.ExpandPath(outputDirectory));
        var report = new RevitCrashCollectionReport
        {
            OutputDirectory = root,
            Analysis = analysis,
        };

        try
        {
            Directory.CreateDirectory(root);
            var journalsDir = Path.Combine(root, "journals");
            Directory.CreateDirectory(journalsDir);

            for (var i = 0; i < analysis.Journals.Count; i++)
            {
                var journal = analysis.Journals[i];
                var destination = Path.Combine(journalsDir, $"{i + 1:00}-{SafeFileName(Path.GetFileName(journal.Path))}");
                File.Copy(journal.Path, destination, overwrite: true);
                report.Files.Add(new RevitCrashCollectedFile
                {
                    Kind = "journal",
                    SourcePath = journal.Path,
                    CollectedPath = destination,
                    Bytes = new FileInfo(destination).Length,
                });
            }

            report.AnalysisPath = Path.Combine(root, "analysis.json");
            File.WriteAllText(report.AnalysisPath, JsonSerializer.Serialize(analysis, TerminalJsonOptions.PrettyCamel));

            report.ReadmePath = Path.Combine(root, "README.md");
            File.WriteAllText(report.ReadmePath, RenderCollectionReadme(analysis));

            report.Files.Add(new RevitCrashCollectedFile
            {
                Kind = "analysis",
                SourcePath = "",
                CollectedPath = report.AnalysisPath,
                Bytes = new FileInfo(report.AnalysisPath).Length,
            });
            report.Files.Add(new RevitCrashCollectedFile
            {
                Kind = "readme",
                SourcePath = "",
                CollectedPath = report.ReadmePath,
                Bytes = new FileInfo(report.ReadmePath).Length,
            });

            report.RedactionReportPath = Path.Combine(root, "redaction.json");
            var redactionReport = BuildRedactionReport(report);
            File.WriteAllText(report.RedactionReportPath, JsonSerializer.Serialize(redactionReport, TerminalJsonOptions.PrettyCamel));
            report.Files.Add(new RevitCrashCollectedFile
            {
                Kind = "redaction",
                SourcePath = "",
                CollectedPath = report.RedactionReportPath,
                Bytes = new FileInfo(report.RedactionReportPath).Length,
            });

            report.ManifestPath = Path.Combine(root, "manifest.json");
            var manifest = BuildPacketManifest(report, analysis);
            File.WriteAllText(report.ManifestPath, JsonSerializer.Serialize(manifest, TerminalJsonOptions.PrettyCamel));
            report.Files.Add(new RevitCrashCollectedFile
            {
                Kind = "manifest",
                SourcePath = "",
                CollectedPath = report.ManifestPath,
                Bytes = new FileInfo(report.ManifestPath).Length,
            });
        }
        catch (IOException ex)
        {
            report.Issues.Add(new RevitCrashIssue
            {
                Severity = "error",
                Code = "collect-write-failed",
                Message = ex.Message,
                Path = root,
            });
        }
        catch (UnauthorizedAccessException ex)
        {
            report.Issues.Add(new RevitCrashIssue
            {
                Severity = "error",
                Code = "collect-write-failed",
                Message = ex.Message,
                Path = root,
            });
        }

        report.Issues.AddRange(analysis.Issues.Where(issue =>
            string.Equals(issue.Severity, "error", StringComparison.OrdinalIgnoreCase)));
        report.Success = analysis.Success && report.Issues.All(issue =>
            !string.Equals(issue.Severity, "error", StringComparison.OrdinalIgnoreCase));
        return report;
    }

    private static RevitCrashRedactionReport BuildRedactionReport(RevitCrashCollectionReport report)
    {
        var redaction = new RevitCrashRedactionReport
        {
            RedactionApplied = false,
            Policy = "Raw crash evidence is preserved for local integrity. Review this report before sharing the packet outside the firm.",
        };

        redaction.SensitiveFields.AddRange(new[]
        {
            "Windows user names in local paths",
            "Model, hub, project, and link paths",
            "Add-in assembly paths and vendor names",
            "Account names or cloud collaboration hints in journals",
        });

        foreach (var file in report.Files)
        {
            redaction.Files.Add(new RevitCrashRedactionReviewFile
            {
                Kind = file.Kind,
                RelativePath = Path.GetRelativePath(report.OutputDirectory, file.CollectedPath),
                Bytes = file.Bytes,
                ReviewNote = RedactionReviewNote(file.Kind),
            });
        }

        return redaction;
    }

    private static string RedactionReviewNote(string kind) =>
        kind switch
        {
            "journal" => "May contain user names, model paths, add-in paths, commands, and cloud/worksharing context.",
            "analysis" => "May contain extracted paths, commands, event-log messages, and source evidence snippets.",
            "readme" => "Contains packet summary and collection guidance.",
            _ => "Review before external sharing.",
        };

    private static RevitCrashReproReport BuildReproReport(
        int year,
        string caseName,
        DateTimeOffset generatedAtUtc,
        Func<IReadOnlyList<RevitCrashReproProcess>>? processProvider)
    {
        var normalizedCase = string.IsNullOrWhiteSpace(caseName) ? "revit-crash" : caseName.Trim();
        var journalDirectory = RevitJournalLocator.PreferredJournalDirectory(year);
        var report = new RevitCrashReproReport
        {
            GeneratedAtUtc = generatedAtUtc,
            RevitYear = year,
            CaseName = normalizedCase,
            JournalDirectory = journalDirectory,
        };

        report.RevitProcesses.AddRange(processProvider?.Invoke() ?? GetRunningRevitProcesses());
        report.RevitProcessCount = report.RevitProcesses.Count;
        report.Checklist.AddRange(new[]
        {
            new RevitCrashReproStep
            {
                StepId = "close-revit",
                Title = "Close all Revit sessions",
                Detail = "Save work, close every Revit instance, and wait until no Revit.exe process remains before starting the clean reproduction.",
            },
            new RevitCrashReproStep
            {
                StepId = "start-clean-session",
                Title = "Start one clean Revit session",
                Detail = "Start the target Revit version once, avoid opening unrelated models, then reproduce only the failing workflow.",
            },
            new RevitCrashReproStep
            {
                StepId = "record-workflow",
                Title = "Record exact reproduction steps",
                Detail = "Write the model path, command, add-in button, export target, link path, or family operation used immediately before the crash.",
            },
            new RevitCrashReproStep
            {
                StepId = "collect-latest-journal",
                Title = "Collect the newest journal after Revit exits",
                Detail = $"Use the newest file under {journalDirectory}; do not collect older journals from unrelated sessions.",
            },
            new RevitCrashReproStep
            {
                StepId = "analyze-and-bundle",
                Title = "Analyze and collect the support packet",
                Detail = "Run crash analyze first, then crash collect to preserve journals, analysis JSON, redaction report, README, and manifest evidence.",
            },
        });
        report.Commands.Add($"revitcli crash analyze --year {year} --since 2h --output markdown");
        report.Commands.Add($"revitcli crash collect --year {year} --since 2h --output-dir .revitcli/crash/{SafeFileName(normalizedCase)} --output markdown");
        report.Commands.Add($"revitcli crash verify --packet .revitcli/crash/{SafeFileName(normalizedCase)} --output json");
        return report;
    }

    private static IReadOnlyList<RevitCrashReproProcess> GetRunningRevitProcesses()
    {
        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName("Revit");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return Array.Empty<RevitCrashReproProcess>();
        }

        try
        {
            return processes
                .OrderBy(process => process.Id)
                .Select(process =>
                {
                    DateTimeOffset? started = null;
                    try
                    {
                        started = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                        started = null;
                    }

                    return new RevitCrashReproProcess
                    {
                        ProcessId = process.Id,
                        ProcessName = process.ProcessName,
                        StartTimeUtc = started,
                    };
                })
                .ToArray();
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }

    private static RevitCrashPacketManifest BuildPacketManifest(
        RevitCrashCollectionReport report,
        RevitCrashAnalysisReport analysis)
    {
        var manifest = new RevitCrashPacketManifest
        {
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            AnalysisSchemaVersion = analysis.SchemaVersion,
            RedactionSchemaVersion = "revit-crash-redaction-report.v1",
            RevitYear = analysis.RevitYear,
            FindingCount = analysis.Findings.Count,
        };

        foreach (var file in report.Files)
        {
            manifest.Files.Add(new RevitCrashPacketManifestFile
            {
                Kind = file.Kind,
                RelativePath = Path.GetRelativePath(report.OutputDirectory, file.CollectedPath),
                Bytes = file.Bytes,
                Sha256 = ComputeSha256Hex(file.CollectedPath),
            });
        }

        return manifest;
    }

    private static RevitCrashVerificationReport VerifyPacket(string packetDirectory)
    {
        var root = Path.GetFullPath(RevitJournalLocator.ExpandPath(packetDirectory));
        var report = new RevitCrashVerificationReport
        {
            PacketDirectory = root,
            ManifestPath = Path.Combine(root, "manifest.json"),
        };

        RevitCrashPacketManifest? manifest = null;
        try
        {
            manifest = JsonSerializer.Deserialize<RevitCrashPacketManifest>(
                File.ReadAllText(report.ManifestPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            report.Issues.Add(new RevitCrashIssue
            {
                Severity = "error",
                Code = "manifest-read-failed",
                Path = report.ManifestPath,
                Message = ex.Message,
            });
        }

        if (manifest == null ||
            !string.Equals(manifest.SchemaVersion, "revit-crash-packet-manifest.v1", StringComparison.OrdinalIgnoreCase))
        {
            report.Issues.Add(new RevitCrashIssue
            {
                Severity = "error",
                Code = "manifest-invalid",
                Path = report.ManifestPath,
                Message = "Crash packet manifest is missing or has an unsupported schema version.",
            });
            report.Success = false;
            return report;
        }

        report.ExpectedFileCount = manifest.Files.Count;
        report.HasRedactionReport = manifest.Files.Any(file =>
            string.Equals(file.Kind, "redaction", StringComparison.OrdinalIgnoreCase));
        if (!report.HasRedactionReport)
        {
            report.Issues.Add(new RevitCrashIssue
            {
                Severity = "warning",
                Code = "redaction-report-missing",
                Path = report.ManifestPath,
                Message = "Crash packet manifest does not list a redaction report; review older packets manually before external sharing.",
            });
        }

        foreach (var expected in manifest.Files)
        {
            var fullPath = Path.GetFullPath(Path.Combine(root, expected.RelativePath));
            var verified = new RevitCrashPacketVerificationFile
            {
                Kind = expected.Kind,
                RelativePath = expected.RelativePath,
                ExpectedBytes = expected.Bytes,
                ExpectedSha256 = expected.Sha256,
                Exists = File.Exists(fullPath),
            };

            if (verified.Exists)
            {
                var info = new FileInfo(fullPath);
                verified.ActualBytes = info.Length;
                verified.ActualSha256 = ComputeSha256Hex(fullPath);
                verified.HashMatches =
                    info.Length == expected.Bytes &&
                    string.Equals(verified.ActualSha256, expected.Sha256, StringComparison.OrdinalIgnoreCase);
                if (!verified.HashMatches)
                {
                    report.Issues.Add(new RevitCrashIssue
                    {
                        Severity = "error",
                        Code = "file-hash-mismatch",
                        Path = fullPath,
                        Message = $"Crash packet file does not match manifest: {expected.RelativePath}",
                    });
                }
            }
            else
            {
                report.Issues.Add(new RevitCrashIssue
                {
                    Severity = "error",
                    Code = "file-missing",
                    Path = fullPath,
                    Message = $"Crash packet file is missing: {expected.RelativePath}",
                });
            }

            report.Files.Add(verified);
        }

        report.VerifiedFileCount = report.Files.Count(file => file.Exists && file.HashMatches);
        report.Success = report.Issues.All(issue =>
            !string.Equals(issue.Severity, "error", StringComparison.OrdinalIgnoreCase));
        return report;
    }

    private static async Task WriteOutputAsync(TextWriter output, object report, string outputFormat)
    {
        switch (outputFormat)
        {
            case "json":
                await output.WriteLineAsync(JsonSerializer.Serialize(report, TerminalJsonOptions.PrettyCamel));
                break;
            case "markdown":
                await output.WriteLineAsync(report switch
                {
                    RevitCrashAnalysisReport analysis => RenderAnalysisMarkdown(analysis),
                    RevitCrashCollectionReport collection => RenderCollectionMarkdown(collection),
                    RevitCrashReproReport repro => RenderReproMarkdown(repro),
                    RevitCrashVerificationReport verify => RenderVerifyMarkdown(verify),
                    _ => report.ToString() ?? "",
                });
                break;
            default:
                await output.WriteLineAsync(report switch
                {
                    RevitCrashAnalysisReport analysis => RenderAnalysisTable(analysis),
                    RevitCrashCollectionReport collection => RenderCollectionTable(collection),
                    RevitCrashReproReport repro => RenderReproTable(repro),
                    RevitCrashVerificationReport verify => RenderVerifyTable(verify),
                    _ => report.ToString() ?? "",
                });
                break;
        }
    }

    private static string RenderAnalysisTable(RevitCrashAnalysisReport report)
    {
        var lines = new List<string>
        {
            $"Revit crash analysis ({report.SchemaVersion})",
            $"Status: {(report.Success ? "OK" : "INCOMPLETE")}",
            $"Year: {report.RevitYear}",
            $"Since: {report.Since} ({report.SinceUtc:O})",
            $"Journals analyzed: {report.Summary.JournalCount}",
            $"Event log entries: {report.Summary.EventLogEntryCount}",
            $"Findings: {report.Summary.FindingCount}",
        };

        if (!string.IsNullOrWhiteSpace(report.Summary.MostLikelyRuleId))
            lines.Add($"Most likely: {report.Summary.MostLikelyRuleId} - {report.Summary.MostLikelyTitle}");
        if (!string.IsNullOrWhiteSpace(report.Summary.LastModelPath))
            lines.Add($"Last model: {report.Summary.LastModelPath}");
        if (!string.IsNullOrWhiteSpace(report.Summary.LastCommand))
            lines.Add($"Last command: {report.Summary.LastCommand}");

        AppendFindings(lines, report.Findings);
        AppendIssues(lines, report.Issues);
        AppendNext(lines, report.NextCommands);
        return string.Join(Environment.NewLine, lines);
    }

    private static string RenderAnalysisMarkdown(RevitCrashAnalysisReport report)
    {
        var lines = new List<string>
        {
            "# Revit Crash Analysis",
            "",
            $"- Schema: `{report.SchemaVersion}`",
            $"- Status: `{(report.Success ? "ok" : "incomplete")}`",
            $"- Revit year: `{report.RevitYear}`",
            $"- Since: `{EscapeMarkdown(report.Since)}`",
            $"- Journals analyzed: `{report.Summary.JournalCount}`",
            $"- Event log entries: `{report.Summary.EventLogEntryCount}`",
            $"- Findings: `{report.Summary.FindingCount}`",
        };

        if (!string.IsNullOrWhiteSpace(report.Summary.MostLikelyRuleId))
            lines.Add($"- Most likely: `{EscapeMarkdown(report.Summary.MostLikelyRuleId)}` - {EscapeMarkdown(report.Summary.MostLikelyTitle ?? "")}");
        if (!string.IsNullOrWhiteSpace(report.Summary.LastModelPath))
            lines.Add($"- Last model: `{EscapeMarkdown(report.Summary.LastModelPath)}`");
        if (!string.IsNullOrWhiteSpace(report.Summary.LastCommand))
            lines.Add($"- Last command: `{EscapeMarkdown(report.Summary.LastCommand)}`");

        lines.Add("");
        lines.Add("## Findings");
        if (report.Findings.Count == 0)
        {
            lines.Add("");
            lines.Add("No known crash signature was found in the available evidence.");
        }
        else
        {
            lines.Add("");
            lines.Add("| Rule | Severity | Confidence | Evidence |");
            lines.Add("| --- | --- | --- | --- |");
            foreach (var finding in report.Findings)
            {
                lines.Add(
                    $"| `{EscapeMarkdown(finding.RuleId)}` | `{EscapeMarkdown(finding.Severity)}` | `{EscapeMarkdown(finding.Confidence)}` | {EscapeMarkdown(finding.Evidence)} |");
            }

            lines.Add("");
            lines.Add("## Suggestions");
            foreach (var finding in report.Findings)
            {
                lines.Add("");
                lines.Add($"### {EscapeMarkdown(finding.Title)}");
                foreach (var suggestion in finding.Suggestions)
                    lines.Add($"- {EscapeMarkdown(suggestion)}");
            }
        }

        AppendIssuesMarkdown(lines, report.Issues);
        AppendNextMarkdown(lines, report.NextCommands);
        return string.Join(Environment.NewLine, lines);
    }

    private static string RenderCollectionTable(RevitCrashCollectionReport report)
    {
        var lines = new List<string>
        {
            $"Revit crash collection ({report.SchemaVersion})",
            $"Status: {(report.Success ? "OK" : "INCOMPLETE")}",
            $"Output directory: {report.OutputDirectory}",
            $"Analysis: {report.AnalysisPath}",
            $"README: {report.ReadmePath}",
            $"Redaction: {report.RedactionReportPath}",
            $"Manifest: {report.ManifestPath}",
            $"Files: {report.Files.Count}",
        };

        foreach (var file in report.Files)
            lines.Add($"  - {file.Kind}: {file.CollectedPath} ({file.Bytes} bytes)");

        AppendIssues(lines, report.Issues);
        return string.Join(Environment.NewLine, lines);
    }

    private static string RenderCollectionMarkdown(RevitCrashCollectionReport report)
    {
        var lines = new List<string>
        {
            "# Revit Crash Collection",
            "",
            $"- Schema: `{report.SchemaVersion}`",
            $"- Status: `{(report.Success ? "ok" : "incomplete")}`",
            $"- Output directory: `{EscapeMarkdown(report.OutputDirectory)}`",
            $"- Analysis: `{EscapeMarkdown(report.AnalysisPath)}`",
            $"- README: `{EscapeMarkdown(report.ReadmePath)}`",
            $"- Redaction: `{EscapeMarkdown(report.RedactionReportPath)}`",
            $"- Manifest: `{EscapeMarkdown(report.ManifestPath)}`",
            "",
            "## Files",
            "",
            "| Kind | Bytes | Path |",
            "| --- | ---: | --- |",
        };

        foreach (var file in report.Files)
            lines.Add($"| `{EscapeMarkdown(file.Kind)}` | {file.Bytes} | `{EscapeMarkdown(file.CollectedPath)}` |");

        AppendIssuesMarkdown(lines, report.Issues);
        return string.Join(Environment.NewLine, lines);
    }

    private static string RenderReproTable(RevitCrashReproReport report)
    {
        var lines = new List<string>
        {
            $"Revit crash clean repro ({report.SchemaVersion})",
            $"Year: {report.RevitYear}",
            $"Case: {report.CaseName}",
            $"Journal directory: {report.JournalDirectory}",
            $"Running Revit processes: {report.RevitProcessCount}",
            "",
            "Checklist:",
        };

        foreach (var step in report.Checklist)
            lines.Add($"  - {step.StepId}: {step.Title}");
        AppendNext(lines, report.Commands);
        return string.Join(Environment.NewLine, lines);
    }

    private static string RenderReproMarkdown(RevitCrashReproReport report)
    {
        var lines = new List<string>
        {
            "# Revit Crash Clean Repro",
            "",
            $"- Schema: `{report.SchemaVersion}`",
            $"- Revit year: `{report.RevitYear}`",
            $"- Case: `{EscapeMarkdown(report.CaseName)}`",
            $"- Journal directory: `{EscapeMarkdown(report.JournalDirectory)}`",
            $"- Running Revit processes: `{report.RevitProcessCount}`",
            "",
            "## Checklist",
            "",
        };

        foreach (var step in report.Checklist)
        {
            lines.Add($"- [ ] **{EscapeMarkdown(step.Title)}**");
            lines.Add($"  {EscapeMarkdown(step.Detail)}");
        }

        AppendNextMarkdown(lines, report.Commands);
        return string.Join(Environment.NewLine, lines);
    }

    private static string RenderVerifyTable(RevitCrashVerificationReport report)
    {
        var lines = new List<string>
        {
            $"Revit crash packet verify ({report.SchemaVersion})",
            $"Status: {(report.Success ? "OK" : "FAILED")}",
            $"Packet: {report.PacketDirectory}",
            $"Manifest: {report.ManifestPath}",
            $"Redaction report: {(report.HasRedactionReport ? "present" : "missing")}",
            $"Files: {report.VerifiedFileCount}/{report.ExpectedFileCount}",
        };

        foreach (var file in report.Files)
            lines.Add($"  - {(file.HashMatches ? "ok" : "fail"),-4} {file.Kind}: {file.RelativePath}");
        AppendIssues(lines, report.Issues);
        return string.Join(Environment.NewLine, lines);
    }

    private static string RenderVerifyMarkdown(RevitCrashVerificationReport report)
    {
        var lines = new List<string>
        {
            "# Revit Crash Packet Verify",
            "",
            $"- Schema: `{report.SchemaVersion}`",
            $"- Success: `{report.Success.ToString().ToLowerInvariant()}`",
            $"- Packet: `{EscapeMarkdown(report.PacketDirectory)}`",
            $"- Manifest: `{EscapeMarkdown(report.ManifestPath)}`",
            $"- Redaction report: `{(report.HasRedactionReport ? "present" : "missing")}`",
            $"- Files: `{report.VerifiedFileCount}/{report.ExpectedFileCount}`",
            "",
            "## Files",
            "",
            "| Status | Kind | Relative path |",
            "| --- | --- | --- |",
        };

        foreach (var file in report.Files)
            lines.Add($"| `{(file.HashMatches ? "ok" : "fail")}` | `{EscapeMarkdown(file.Kind)}` | `{EscapeMarkdown(file.RelativePath)}` |");
        AppendIssuesMarkdown(lines, report.Issues);
        return string.Join(Environment.NewLine, lines);
    }

    private static string RenderCollectionReadme(RevitCrashAnalysisReport analysis)
    {
        var lines = new List<string>
        {
            "# Revit Crash Evidence Packet",
            "",
            "This packet was generated locally by `revitcli crash collect`.",
            "",
            $"- Analysis schema: `{analysis.SchemaVersion}`",
            $"- Revit year: `{analysis.RevitYear}`",
            $"- Generated at UTC: `{analysis.GeneratedAtUtc:O}`",
            $"- Findings: `{analysis.Findings.Count}`",
            "",
            "Start with `analysis.json`; copied journal files are under `journals/`.",
            "",
            "`redaction.json` documents the packet's sensitive-field review policy. Raw evidence is preserved for local integrity, so review it before sharing outside the firm.",
        };

        return string.Join(Environment.NewLine, lines);
    }

    private static void AppendFindings(List<string> lines, IReadOnlyList<RevitCrashFinding> findings)
    {
        lines.Add("");
        lines.Add("Findings:");
        if (findings.Count == 0)
        {
            lines.Add("  (none)");
            return;
        }

        foreach (var finding in findings)
        {
            lines.Add($"  - [{finding.Confidence}/{finding.Severity}] {finding.RuleId}: {finding.Title}");
            lines.Add($"    Evidence: {finding.Evidence}");
            foreach (var suggestion in finding.Suggestions.Take(3))
                lines.Add($"    Next: {suggestion}");
        }
    }

    private static void AppendIssues(List<string> lines, IReadOnlyList<RevitCrashIssue> issues)
    {
        if (issues.Count == 0)
            return;

        lines.Add("");
        lines.Add("Issues:");
        foreach (var issue in issues)
            lines.Add($"  - [{issue.Severity}] {issue.Code}: {issue.Message}");
    }

    private static void AppendNext(List<string> lines, IReadOnlyList<string> commands)
    {
        if (commands.Count == 0)
            return;

        lines.Add("");
        lines.Add("Next commands:");
        foreach (var command in commands)
            lines.Add($"  - {command}");
    }

    private static void AppendIssuesMarkdown(List<string> lines, IReadOnlyList<RevitCrashIssue> issues)
    {
        if (issues.Count == 0)
            return;

        lines.Add("");
        lines.Add("## Issues");
        foreach (var issue in issues)
            lines.Add($"- `{EscapeMarkdown(issue.Severity)}` `{EscapeMarkdown(issue.Code)}` {EscapeMarkdown(issue.Message)}");
    }

    private static void AppendNextMarkdown(List<string> lines, IReadOnlyList<string> commands)
    {
        if (commands.Count == 0)
            return;

        lines.Add("");
        lines.Add("## Next Commands");
        foreach (var command in commands)
            lines.Add($"- `{EscapeMarkdown(command)}`");
    }

    private static string SafeFileName(string name)
    {
        var safe = string.IsNullOrWhiteSpace(name) ? "journal.txt" : name;
        foreach (var invalid in Path.GetInvalidFileNameChars())
            safe = safe.Replace(invalid, '_');
        safe = safe.Replace(' ', '-');
        return safe;
    }

    private static string ComputeSha256Hex(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string EscapeMarkdown(string? value) =>
        (value ?? "")
            .Replace("|", "\\|", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
}
