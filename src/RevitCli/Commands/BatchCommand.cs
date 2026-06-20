using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using RevitCli.Client;
using RevitCli.Config;
using RevitCli.Output;
using RevitCli.Workflows;
using Spectre.Console;

namespace RevitCli.Commands;

public class BatchItem
{
    [JsonPropertyName("command")]
    public string Command { get; set; } = "";

    [JsonPropertyName("args")]
    public List<string> Args { get; set; } = new();
}

public static class BatchCommand
{
    public static Command Create(RevitClient client, CliConfig config)
    {
        var fileArg = new Argument<string>("file", "Path to JSON batch file");
        var command = new Command("batch", "Execute commands from a JSON batch file")
        {
            fileArg
        };
        command.AddCommand(CreateProjectsCommand(client, config));

        command.SetHandler(async (file) =>
        {
            if (!ConsoleHelper.IsInteractive)
            {
                var exitCode = await ExecuteAsync(client, config, file, Console.Out);
                Environment.ExitCode = exitCode;
                return;
            }

            await ExecuteSpectre(client, config, file);
        }, fileArg);

        return command;
    }

    private static Command CreateProjectsCommand(RevitClient client, CliConfig config)
    {
        var manifestOpt = new Option<string>("--manifest", "rvt-project-manifest.v1 JSON file") { IsRequired = true };
        var commandOpt = new Option<string>("--command", "Read-only revitcli command template to run per model") { IsRequired = true };
        var resultsOpt = new Option<string>("--results", () => Path.Combine(".revitcli", "batch", "project-results.jsonl"), "JSONL result path");
        var summaryOpt = new Option<string?>("--summary", "Write Markdown summary to this path");
        var timeoutOpt = new Option<long>("--timeout-ms", () => 0, "Per-model timeout in milliseconds; 0 disables timeout");
        var resumeOpt = new Option<bool>("--resume", "Skip manifest projects that already have successful JSONL result rows");
        var dryRunOpt = new Option<bool>("--dry-run", "Preview per-model commands without executing them");
        var outputOpt = new Option<string>("--output", () => "markdown", "Output format: table, json, markdown");
        var command = new Command("projects", "Run a read-only command for every model in an RVT project manifest")
        {
            manifestOpt,
            commandOpt,
            resultsOpt,
            summaryOpt,
            timeoutOpt,
            resumeOpt,
            dryRunOpt,
            outputOpt
        };

        command.SetHandler(async (
            string manifest,
            string commandTemplate,
            string results,
            string? summary,
            long timeoutMs,
            bool resume,
            bool dryRun,
            string outputFormat) =>
        {
            Environment.ExitCode = await ExecuteProjectsAsync(
                client,
                config,
                manifest,
                commandTemplate,
                results,
                summary,
                timeoutMs,
                resume,
                dryRun,
                outputFormat,
                Console.Out);
        }, manifestOpt, commandOpt, resultsOpt, summaryOpt, timeoutOpt, resumeOpt, dryRunOpt, outputOpt);

        return command;
    }

    public static async Task<int> ExecuteAsync(RevitClient client, CliConfig config, string file, TextWriter output)
    {
        if (!File.Exists(file))
        {
            await output.WriteLineAsync($"Error: file not found: {file}");
            return 1;
        }

        List<BatchItem> items;
        try
        {
            var json = await File.ReadAllTextAsync(file);
            items = JsonSerializer.Deserialize<List<BatchItem>>(json) ?? new();
        }
        catch (JsonException ex)
        {
            await output.WriteLineAsync($"Error: invalid JSON: {ex.Message}");
            return 1;
        }

        if (items.Count == 0)
        {
            await output.WriteLineAsync("No commands to execute.");
            return 0;
        }

        var hasError = false;
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            await output.WriteLineAsync($"[{i + 1}/{items.Count}] {item.Command} {string.Join(" ", item.Args)}");

            var exitCode = await DispatchViaParser(client, config, item);
            if (exitCode != 0) hasError = true;

            await output.WriteLineAsync();
        }

        return hasError ? 1 : 0;
    }

    public static async Task<int> ExecuteProjectsAsync(
        RevitClient client,
        CliConfig config,
        string manifestPath,
        string commandTemplate,
        string resultsPath,
        string? summaryPath,
        long timeoutMs,
        bool resume,
        bool dryRun,
        string outputFormat,
        TextWriter output)
    {
        if (!TerminalOutputFormat.TryNormalize(outputFormat, out var normalizedOutput, "table", "json", "markdown"))
        {
            await output.WriteLineAsync("Error: --output must be 'table', 'json', or 'markdown'.");
            return 1;
        }

        if (timeoutMs < 0)
        {
            await output.WriteLineAsync("Error: --timeout-ms must be 0 or greater.");
            return 1;
        }

        if (!TryBuildSafeCommand(commandTemplate, new BatchProjectManifestEntry(), out _, out var validationError))
        {
            await output.WriteLineAsync($"Error: project batch commands must be read-only or dry-run safe: {validationError}");
            return 1;
        }

        RvtCommand.RvtProjectManifest manifest;
        try
        {
            if (!File.Exists(manifestPath))
            {
                await output.WriteLineAsync($"Error: manifest not found: {manifestPath}");
                return 1;
            }

            manifest = JsonSerializer.Deserialize<RvtCommand.RvtProjectManifest>(
                await File.ReadAllTextAsync(manifestPath),
                TerminalJsonOptions.CompactContract) ?? new RvtCommand.RvtProjectManifest { Success = false };
        }
        catch (JsonException ex)
        {
            await output.WriteLineAsync($"Error: invalid project manifest JSON: {ex.Message}");
            return 1;
        }

        if (!string.Equals(manifest.SchemaVersion, "rvt-project-manifest.v1", StringComparison.OrdinalIgnoreCase))
        {
            await output.WriteLineAsync("Error: --manifest must be an rvt-project-manifest.v1 JSON file.");
            return 1;
        }

        var completedPathHashes = resume ? ReadSucceededPathHashes(resultsPath) : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resultRows = new List<BatchProjectResult>();
        var reportProjects = new List<BatchProjectRunProject>();
        var startedAt = DateTimeOffset.UtcNow;

        foreach (var project in manifest.Projects)
        {
            var entry = BatchProjectManifestEntry.FromProject(project);
            if (!TryBuildSafeCommand(commandTemplate, entry, out var renderedCommand, out validationError))
            {
                await output.WriteLineAsync($"Error: project batch commands must be read-only or dry-run safe: {validationError}");
                return 1;
            }

            if (resume && completedPathHashes.Contains(project.PathHash))
            {
                reportProjects.Add(BatchProjectRunProject.FromProject(project, renderedCommand, "skipped", exitCode: 0));
                continue;
            }

            if (dryRun)
            {
                reportProjects.Add(BatchProjectRunProject.FromProject(project, renderedCommand, "planned", exitCode: null));
                continue;
            }

            var result = await ExecuteProjectCommandAsync(client, config, project, renderedCommand, timeoutMs);
            resultRows.Add(result);
            reportProjects.Add(BatchProjectRunProject.FromProject(project, renderedCommand, result.Status, result.ExitCode));
            await AppendProjectResultAsync(resultsPath, result);
        }

        var report = BuildProjectRunReport(
            manifestPath,
            resultsPath,
            summaryPath,
            commandTemplate,
            timeoutMs,
            resume,
            dryRun,
            startedAt,
            reportProjects.ToArray());

        if (!dryRun && !string.IsNullOrWhiteSpace(summaryPath))
            await WriteProjectSummaryAsync(summaryPath, report);

        await WriteProjectRunOutputAsync(output, report, normalizedOutput);
        return report.Summary.FailedProjectCount > 0 || report.Summary.TimedOutProjectCount > 0 ? 1 : 0;
    }

    private static async Task ExecuteSpectre(RevitClient client, CliConfig config, string file)
    {
        if (!File.Exists(file))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] file not found: {Markup.Escape(file)}");
            Environment.ExitCode = 1;
            return;
        }

        List<BatchItem> items;
        try
        {
            var json = await File.ReadAllTextAsync(file);
            items = JsonSerializer.Deserialize<List<BatchItem>>(json) ?? new();
        }
        catch (JsonException ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] invalid JSON: {Markup.Escape(ex.Message)}");
            Environment.ExitCode = 1;
            return;
        }

        if (items.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No commands to execute.[/]");
            return;
        }

        var hasError = false;
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            AnsiConsole.MarkupLine($"[bold cyan][{i + 1}/{items.Count}][/] {Markup.Escape(item.Command)} {Markup.Escape(string.Join(" ", item.Args))}");

            var exitCode = await DispatchViaParser(client, config, item);
            if (exitCode != 0) hasError = true;

            AnsiConsole.WriteLine();
        }

        if (hasError)
            Environment.ExitCode = 1;
    }

    /// <summary>
    /// Dispatch batch items through the same System.CommandLine parser/handler as the real CLI.
    /// This guarantees batch and interactive CLI have identical validation, type binding, and error behavior.
    /// </summary>
    private static async Task<int> DispatchViaParser(RevitClient client, CliConfig config, BatchItem item)
    {
        var root = BuildRootCommand(client, config);

        // Prepend the command name to the args array, same as if typed on the CLI
        var fullArgs = new List<string> { item.Command };
        fullArgs.AddRange(item.Args);

        return await root.InvokeAsync(fullArgs.ToArray());
    }

    private static async Task<BatchProjectResult> ExecuteProjectCommandAsync(
        RevitClient client,
        CliConfig config,
        RvtCommand.RvtProjectEntry project,
        string command,
        long timeoutMs)
    {
        var tokens = NormalizeCommandTokens(WorkflowCommandLine.Tokenize(command));
        var item = new BatchItem
        {
            Command = tokens.FirstOrDefault() ?? "",
            Args = tokens.Skip(1).ToList()
        };
        var previousOut = Console.Out;
        var previousError = Console.Error;
        var previousExitCode = Environment.ExitCode;
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var startedAt = DateTimeOffset.UtcNow;
        int exitCode;
        var timedOut = false;

        try
        {
            Environment.ExitCode = 0;
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var task = DispatchViaParser(client, config, item);
            if (timeoutMs > 0)
            {
                var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromMilliseconds(timeoutMs)));
                if (!ReferenceEquals(completed, task))
                {
                    timedOut = true;
                    exitCode = 124;
                }
                else
                {
                    exitCode = await task;
                }
            }
            else
            {
                exitCode = await task;
            }
        }
        catch (Exception ex)
        {
            exitCode = 1;
            await stderr.WriteLineAsync(ex.Message);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
            Environment.ExitCode = previousExitCode;
        }

        var completedAt = DateTimeOffset.UtcNow;
        return new BatchProjectResult
        {
            GeneratedAtUtc = completedAt,
            ModelPath = project.Path,
            RelativePath = project.RelativePath,
            PathHash = project.PathHash,
            Command = command,
            Status = timedOut ? "timed-out" : exitCode == 0 ? "succeeded" : "failed",
            ExitCode = exitCode,
            TimeoutMs = timeoutMs > 0 ? timeoutMs : null,
            DurationMs = (long)Math.Max(0, (completedAt - startedAt).TotalMilliseconds),
            Stdout = TrimCapturedOutput(stdout.ToString()),
            Stderr = TrimCapturedOutput(stderr.ToString())
        };
    }

    private static bool TryBuildSafeCommand(
        string commandTemplate,
        BatchProjectManifestEntry project,
        out string command,
        out string error)
    {
        command = SubstituteProjectTokens(commandTemplate, project);
        error = "";
        IReadOnlyList<string> tokens;
        try
        {
            tokens = WorkflowCommandLine.Tokenize(command);
        }
        catch (FormatException ex)
        {
            error = ex.Message;
            return false;
        }

        if (tokens.Count == 0)
        {
            error = "command template is empty.";
            return false;
        }

        if (WorkflowCommandLine.ContainsShellOperator(tokens))
        {
            error = "shell operators are not allowed in project batch command templates.";
            return false;
        }

        var normalized = NormalizeCommandTokens(tokens);
        if (normalized.Count == 0)
        {
            error = "command template must include a revitcli command.";
            return false;
        }

        if (normalized.Any(token => string.Equals(token, "--yes", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(token, "--apply", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(token, "--force", StringComparison.OrdinalIgnoreCase)))
        {
            error = "approval or force flags are not allowed in read-only project batches.";
            return false;
        }

        if (IsMutatingCommandShape(normalized) &&
            !normalized.Any(token => string.Equals(token, "--dry-run", StringComparison.OrdinalIgnoreCase)))
        {
            error = $"command '{string.Join(" ", normalized.Take(3))}' can write; add --dry-run or choose a read-only command.";
            return false;
        }

        return true;
    }

    private static IReadOnlyList<string> NormalizeCommandTokens(IReadOnlyList<string> tokens) =>
        tokens.Count > 0 && string.Equals(tokens[0], "revitcli", StringComparison.OrdinalIgnoreCase)
            ? tokens.Skip(1).ToArray()
            : tokens.ToArray();

    private static bool IsMutatingCommandShape(IReadOnlyList<string> words)
    {
        var command = words[0];
        if (IsAny(command, "set", "import", "export", "publish", "fix", "rollback"))
            return true;
        if (IsAny(command, "plan") && words.Count >= 2 && IsAny(words[1], "apply"))
            return true;
        if (IsAny(command, "config") && words.Count >= 2 && IsAny(words[1], "set"))
            return true;
        if (IsAny(command, "journal") && words.Count >= 2 && IsAny(words[1], "sign"))
            return true;
        if (IsAny(command, "workflow") && words.Count >= 2 && IsAny(words[1], "init", "run"))
            return true;
        if (IsAny(command, "standards") && words.Count >= 2 && IsAny(words[1], "install"))
            return true;
        if (IsAny(command, "schedules") && words.Count >= 2 && IsAny(words[1], "ensure", "batch-export"))
            return true;
        if (IsAny(command, "views") && words.Count >= 2 && IsAny(words[1], "template-apply", "clone-set"))
            return true;
        if (IsAny(command, "links") && words.Count >= 2 && IsAny(words[1], "repair"))
            return true;
        if (IsAny(command, "model") && words.Count >= 2 && IsAny(words[1], "map-fix"))
            return true;
        if (IsAny(command, "rooms") && words.Count >= 2 && IsAny(words[1], "renumber"))
            return true;
        if (IsAny(command, "marks") && words.Count >= 2 && IsAny(words[1], "assign"))
            return true;
        if (IsAny(command, "rvt") && words.Count >= 2 && IsAny(words[1], "clean-backups"))
            return true;
        if (IsAny(command, "library") && words.Count >= 2 && IsAny(words[1], "install", "repair-apply", "repair-rollback"))
            return true;
        if (IsAny(command, "addins") && words.Count >= 2 && IsAny(words[1], "apply", "rollback"))
            return true;

        return false;
    }

    private static bool IsAny(string value, params string[] candidates) =>
        candidates.Any(candidate => string.Equals(value, candidate, StringComparison.OrdinalIgnoreCase));

    private static string SubstituteProjectTokens(string template, BatchProjectManifestEntry project) =>
        template
            .Replace("{modelPath}", QuoteCommandValue(project.ModelPath), StringComparison.Ordinal)
            .Replace("{modelDirectory}", QuoteCommandValue(project.ModelDirectory), StringComparison.Ordinal)
            .Replace("{relativePath}", QuoteCommandValue(project.RelativePath), StringComparison.Ordinal)
            .Replace("{fileName}", QuoteCommandValue(project.FileName), StringComparison.Ordinal)
            .Replace("{pathHash}", QuoteCommandValue(project.PathHash), StringComparison.Ordinal);

    private static string QuoteCommandValue(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "\"\"";

        if (!value.Any(char.IsWhiteSpace) && !value.Contains('"'))
            return value;

        return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private static HashSet<string> ReadSucceededPathHashes(string resultsPath)
    {
        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(resultsPath))
            return hashes;

        foreach (var line in File.ReadLines(resultsPath))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.TryGetProperty("status", out var status) &&
                    string.Equals(status.GetString(), "succeeded", StringComparison.OrdinalIgnoreCase) &&
                    root.TryGetProperty("pathHash", out var hash) &&
                    !string.IsNullOrWhiteSpace(hash.GetString()))
                {
                    hashes.Add(hash.GetString()!);
                }
            }
            catch (JsonException)
            {
            }
        }

        return hashes;
    }

    private static async Task AppendProjectResultAsync(string resultsPath, BatchProjectResult result)
    {
        var fullPath = Path.GetFullPath(resultsPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        await File.AppendAllTextAsync(
            fullPath,
            JsonSerializer.Serialize(result, TerminalJsonOptions.CompactContract) + Environment.NewLine);
    }

    private static BatchProjectRunReport BuildProjectRunReport(
        string manifestPath,
        string resultsPath,
        string? summaryPath,
        string commandTemplate,
        long timeoutMs,
        bool resume,
        bool dryRun,
        DateTimeOffset generatedAt,
        IReadOnlyList<BatchProjectRunProject> projects)
    {
        var summary = new BatchProjectRunSummary
        {
            TotalProjectCount = projects.Count,
            PlannedProjectCount = projects.Count(project => project.Status == "planned"),
            ExecutedProjectCount = projects.Count(project => project.Status is "succeeded" or "failed" or "timed-out"),
            SkippedProjectCount = projects.Count(project => project.Status == "skipped"),
            SucceededProjectCount = projects.Count(project => project.Status is "succeeded" or "skipped"),
            FailedProjectCount = projects.Count(project => project.Status == "failed"),
            TimedOutProjectCount = projects.Count(project => project.Status == "timed-out"),
        };

        return new BatchProjectRunReport
        {
            GeneratedAtUtc = generatedAt,
            ManifestPath = Path.GetFullPath(manifestPath),
            ResultsPath = string.IsNullOrWhiteSpace(resultsPath) ? null : Path.GetFullPath(resultsPath),
            SummaryPath = string.IsNullOrWhiteSpace(summaryPath) ? null : Path.GetFullPath(summaryPath),
            CommandTemplate = commandTemplate,
            TimeoutMs = timeoutMs > 0 ? timeoutMs : null,
            Resume = resume,
            DryRun = dryRun,
            Summary = summary,
            Projects = projects
        };
    }

    private static async Task WriteProjectSummaryAsync(string summaryPath, BatchProjectRunReport report)
    {
        var fullPath = Path.GetFullPath(summaryPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        await File.WriteAllTextAsync(fullPath, RenderProjectRunMarkdown(report));
    }

    private static async Task WriteProjectRunOutputAsync(TextWriter output, BatchProjectRunReport report, string outputFormat)
    {
        switch (outputFormat)
        {
            case "json":
                await output.WriteLineAsync(JsonSerializer.Serialize(report, TerminalJsonOptions.PrettyCamel));
                break;
            case "markdown":
                await output.WriteLineAsync(RenderProjectRunMarkdown(report));
                break;
            default:
                await output.WriteLineAsync(RenderProjectRunTable(report));
                break;
        }
    }

    private static string RenderProjectRunTable(BatchProjectRunReport report)
    {
        var lines = new List<string>
        {
            "Project batch",
            $"Manifest: {report.ManifestPath}",
            $"Mode: {(report.DryRun ? "dry-run" : "run")}",
            $"Projects: {report.Summary.TotalProjectCount}",
            $"Executed: {report.Summary.ExecutedProjectCount}",
            $"Skipped: {report.Summary.SkippedProjectCount}",
            $"Succeeded: {report.Summary.SucceededProjectCount}",
            $"Failed: {report.Summary.FailedProjectCount}",
            $"Timed out: {report.Summary.TimedOutProjectCount}",
        };

        foreach (var project in report.Projects.Take(50))
            lines.Add($"  {project.Status,-9} {project.RelativePath} {project.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"}");
        if (report.Projects.Count > 50)
            lines.Add($"  ... and {report.Projects.Count - 50} more.");

        return string.Join(Environment.NewLine, lines);
    }

    private static string RenderProjectRunMarkdown(BatchProjectRunReport report)
    {
        var lines = new List<string>
        {
            "# Project Batch Summary",
            "",
            $"- Manifest: `{EscapeMarkdown(report.ManifestPath)}`",
            $"- Results: `{EscapeMarkdown(report.ResultsPath ?? "")}`",
            $"- Mode: `{(report.DryRun ? "dry-run" : "run")}`",
            $"- Resume: `{report.Resume.ToString().ToLowerInvariant()}`",
            $"- Projects: `{report.Summary.TotalProjectCount}`",
            $"- Executed: `{report.Summary.ExecutedProjectCount}`",
            $"- Skipped: `{report.Summary.SkippedProjectCount}`",
            $"- Succeeded: `{report.Summary.SucceededProjectCount}`",
            $"- Failed: `{report.Summary.FailedProjectCount}`",
            $"- Timed out: `{report.Summary.TimedOutProjectCount}`",
            "",
            "| Status | Model | Exit code | Command |",
            "| --- | --- | ---: | --- |",
        };

        foreach (var project in report.Projects)
        {
            lines.Add(
                $"| `{EscapeMarkdown(project.Status)}` | `{EscapeMarkdown(project.RelativePath)}` | {project.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? ""} | `{EscapeMarkdown(project.Command)}` |");
        }

        lines.Add("");
        return string.Join(Environment.NewLine, lines);
    }

    private static string EscapeMarkdown(string value) =>
        value.Replace("|", "\\|", StringComparison.Ordinal);

    private static string TrimCapturedOutput(string value)
    {
        value = value.Trim();
        const int maxLength = 4000;
        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private static RootCommand BuildRootCommand(RevitClient client, CliConfig config)
    {
        return CliCommandCatalog.CreateRootCommand(
            client,
            config,
            includeInteractiveCommand: false,
            includeBatchCommand: false);
    }

    private sealed record BatchProjectManifestEntry(
        string ModelPath,
        string ModelDirectory,
        string RelativePath,
        string FileName,
        string PathHash)
    {
        public BatchProjectManifestEntry()
            : this("", "", "", "", "")
        {
        }

        public static BatchProjectManifestEntry FromProject(RvtCommand.RvtProjectEntry project) =>
            new(
                project.Path,
                Path.GetDirectoryName(project.Path) ?? "",
                project.RelativePath,
                project.FileName,
                project.PathHash);
    }

    public sealed class BatchProjectRunReport
    {
        public string SchemaVersion { get; init; } = "batch-project-run.v1";
        public DateTimeOffset GeneratedAtUtc { get; init; }
        public string ManifestPath { get; init; } = "";
        public string? ResultsPath { get; init; }
        public string? SummaryPath { get; init; }
        public string CommandTemplate { get; init; } = "";
        public long? TimeoutMs { get; init; }
        public bool Resume { get; init; }
        public bool DryRun { get; init; }
        public BatchProjectRunSummary Summary { get; init; } = new();
        public IReadOnlyList<BatchProjectRunProject> Projects { get; init; } = Array.Empty<BatchProjectRunProject>();
    }

    public sealed class BatchProjectRunSummary
    {
        public int TotalProjectCount { get; init; }
        public int PlannedProjectCount { get; init; }
        public int ExecutedProjectCount { get; init; }
        public int SkippedProjectCount { get; init; }
        public int SucceededProjectCount { get; init; }
        public int FailedProjectCount { get; init; }
        public int TimedOutProjectCount { get; init; }
    }

    public sealed class BatchProjectRunProject
    {
        public string ModelPath { get; init; } = "";
        public string RelativePath { get; init; } = "";
        public string PathHash { get; init; } = "";
        public string Command { get; init; } = "";
        public string Status { get; init; } = "";
        public int? ExitCode { get; init; }

        public static BatchProjectRunProject FromProject(
            RvtCommand.RvtProjectEntry project,
            string command,
            string status,
            int? exitCode) =>
            new()
            {
                ModelPath = project.Path,
                RelativePath = project.RelativePath,
                PathHash = project.PathHash,
                Command = command,
                Status = status,
                ExitCode = exitCode
            };
    }

    public sealed class BatchProjectResult
    {
        public string SchemaVersion { get; init; } = "batch-project-result.v1";
        public DateTimeOffset GeneratedAtUtc { get; init; }
        public string ModelPath { get; init; } = "";
        public string RelativePath { get; init; } = "";
        public string PathHash { get; init; } = "";
        public string Command { get; init; } = "";
        public string Status { get; init; } = "";
        public int ExitCode { get; init; }
        public long? TimeoutMs { get; init; }
        public long DurationMs { get; init; }
        public string Stdout { get; init; } = "";
        public string Stderr { get; init; } = "";
    }
}
