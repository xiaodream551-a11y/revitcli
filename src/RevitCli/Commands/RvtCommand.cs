using System.CommandLine;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using RevitCli.Output;

namespace RevitCli.Commands;

public static class RvtCommand
{
    private static readonly Regex BackupFileNamePattern =
        new(@"^(?<stem>.+)\.(?<number>0[0-9]{3})\.rvt$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static Command Create()
    {
        var command = new Command("rvt", "Find Revit RVT files, create project manifests, and clean numbered backup files");
        command.AddCommand(CreateScanCommand());
        command.AddCommand(CreateManifestCommand());
        command.AddCommand(CreateCleanBackupsCommand());
        return command;
    }

    private static Command CreateScanCommand()
    {
        var rootArg = new Argument<string>("root", "Root directory to scan");
        var nonRecursiveOpt = new Option<bool>("--non-recursive", "Only scan the root directory");
        var outputOpt = new Option<string>("--output", () => "table", "Output format: table, json, markdown");

        var command = new Command("scan", "List RVT files and classify Revit backup files")
        {
            rootArg,
            nonRecursiveOpt,
            outputOpt,
        };

        command.SetHandler(async (string root, bool nonRecursive, string output) =>
        {
            Environment.ExitCode = await ExecuteScanAsync(root, nonRecursive, output, Console.Out);
        }, rootArg, nonRecursiveOpt, outputOpt);

        return command;
    }

    private static Command CreateManifestCommand()
    {
        var rootArg = new Argument<string>("root", "Root directory to scan");
        var nonRecursiveOpt = new Option<bool>("--non-recursive", "Only scan the root directory");
        var manifestOutputOpt = new Option<string?>("--manifest-output", "Write project manifest JSON to this path");
        var outputOpt = new Option<string>("--output", () => "table", "Output format: table, json, markdown");

        var command = new Command("manifest", "Create a read-only project manifest from local RVT files")
        {
            rootArg,
            nonRecursiveOpt,
            manifestOutputOpt,
            outputOpt,
        };

        command.SetHandler(async (string root, bool nonRecursive, string? manifestOutput, string output) =>
        {
            Environment.ExitCode = await ExecuteManifestAsync(root, nonRecursive, manifestOutput, output, Console.Out);
        }, rootArg, nonRecursiveOpt, manifestOutputOpt, outputOpt);

        return command;
    }

    private static Command CreateCleanBackupsCommand()
    {
        var rootArg = new Argument<string>("root", "Root directory to scan");
        var dryRunOpt = new Option<bool>("--dry-run", "Show backup files that would be deleted without deleting them");
        var applyOpt = new Option<bool>("--apply", "Delete matched backup files");
        var yesOpt = new Option<bool>("--yes", "Confirm deletion in non-interactive mode");
        var includeOrphansOpt = new Option<bool>("--include-orphans", "Also delete numbered backup files when the main RVT is missing");
        var nonRecursiveOpt = new Option<bool>("--non-recursive", "Only scan the root directory");
        var olderThanOpt = new Option<string?>("--older-than", "Only delete backups older than a duration such as 7d, 24h, or 60m");
        var reportOpt = new Option<string?>("--report", "Write a JSON cleanup report to this path");
        var outputOpt = new Option<string>("--output", () => "table", "Output format: table, json, markdown");

        var command = new Command("clean-backups", "Delete Revit numbered backup files after a dry-run review")
        {
            rootArg,
            dryRunOpt,
            applyOpt,
            yesOpt,
            includeOrphansOpt,
            nonRecursiveOpt,
            olderThanOpt,
            reportOpt,
            outputOpt,
        };

        command.SetHandler(async ctx =>
        {
            Environment.ExitCode = await ExecuteCleanBackupsAsync(
                ctx.ParseResult.GetValueForArgument(rootArg),
                ctx.ParseResult.GetValueForOption(dryRunOpt),
                ctx.ParseResult.GetValueForOption(applyOpt),
                ctx.ParseResult.GetValueForOption(yesOpt),
                ctx.ParseResult.GetValueForOption(includeOrphansOpt),
                ctx.ParseResult.GetValueForOption(nonRecursiveOpt),
                ctx.ParseResult.GetValueForOption(olderThanOpt),
                ctx.ParseResult.GetValueForOption(reportOpt),
                ctx.ParseResult.GetValueForOption(outputOpt)!,
                Console.Out);
        });

        return command;
    }

    public static async Task<int> ExecuteScanAsync(
        string root,
        bool nonRecursive,
        string outputFormat,
        TextWriter output)
    {
        if (!TerminalOutputFormat.TryNormalize(outputFormat, out var normalizedOutput, "table", "json", "markdown"))
        {
            await output.WriteLineAsync("Error: --output must be 'table', 'json', or 'markdown'.");
            return 1;
        }

        var report = BuildReport(
            root,
            recursive: !nonRecursive,
            command: "rvt scan",
            dryRun: true,
            apply: false,
            yes: false,
            includeOrphans: false,
            olderThan: null,
            markDeleteCandidates: false);

        await WriteOutputAsync(output, report, normalizedOutput);
        return report.Success ? 0 : 1;
    }

    public static async Task<int> ExecuteManifestAsync(
        string root,
        bool nonRecursive,
        string? manifestOutputPath,
        string outputFormat,
        TextWriter output)
    {
        if (!TerminalOutputFormat.TryNormalize(outputFormat, out var normalizedOutput, "table", "json", "markdown"))
        {
            await output.WriteLineAsync("Error: --output must be 'table', 'json', or 'markdown'.");
            return 1;
        }

        var scan = BuildReport(
            root,
            recursive: !nonRecursive,
            command: "rvt scan",
            dryRun: true,
            apply: false,
            yes: false,
            includeOrphans: false,
            olderThan: null,
            markDeleteCandidates: false);

        var manifest = BuildProjectManifest(scan);
        var exitCode = manifest.Success ? 0 : 1;

        if (!string.IsNullOrWhiteSpace(manifestOutputPath))
        {
            var manifestExit = await WriteProjectManifestAsync(manifestOutputPath, manifest);
            if (manifestExit != 0)
                exitCode = manifestExit;
        }

        await WriteManifestOutputAsync(output, manifest, normalizedOutput);
        return exitCode;
    }

    public static async Task<int> ExecuteCleanBackupsAsync(
        string root,
        bool dryRun,
        bool apply,
        bool yes,
        bool includeOrphans,
        bool nonRecursive,
        string? olderThan,
        string? reportPath,
        string outputFormat,
        TextWriter output)
    {
        if (!TerminalOutputFormat.TryNormalize(outputFormat, out var normalizedOutput, "table", "json", "markdown"))
        {
            await output.WriteLineAsync("Error: --output must be 'table', 'json', or 'markdown'.");
            return 1;
        }

        if (dryRun && apply)
        {
            await output.WriteLineAsync("Error: --dry-run and --apply cannot be combined.");
            return 1;
        }

        var effectiveDryRun = dryRun || !apply;
        var report = BuildReport(
            root,
            recursive: !nonRecursive,
            command: "rvt clean-backups",
            dryRun: effectiveDryRun,
            apply,
            yes,
            includeOrphans,
            olderThan,
            markDeleteCandidates: true);

        var exitCode = report.Success ? 0 : 1;
        if (report.Success && apply && !yes)
        {
            report.Success = false;
            report.Mode = "refused";
            report.Issues.Add(new RvtIssue
            {
                Severity = "error",
                Code = "confirmation-required",
                Message = "--yes is required with --apply before deleting RVT backup files.",
            });
            RecalculateSummary(report);
            exitCode = 1;
        }
        else if (report.Success && apply && yes)
        {
            ApplyDeletion(report);
            exitCode = report.Summary.DeleteFailureCount > 0 ? 2 : 0;
            report.Success = report.Summary.DeleteFailureCount == 0;
            report.Mode = report.Summary.DeleteFailureCount > 0 ? "partial" : "applied";
            RecalculateSummary(report);
        }
        else if (report.Success)
        {
            report.Mode = "dry-run";
        }

        if (!string.IsNullOrWhiteSpace(reportPath))
        {
            var reportExit = await WriteReportAsync(reportPath, report);
            if (reportExit != 0)
                exitCode = reportExit;
        }

        await WriteOutputAsync(output, report, normalizedOutput);
        return exitCode;
    }

    private static RvtReport BuildReport(
        string root,
        bool recursive,
        string command,
        bool dryRun,
        bool apply,
        bool yes,
        bool includeOrphans,
        string? olderThan,
        bool markDeleteCandidates)
    {
        var generatedAt = DateTimeOffset.UtcNow;
        var report = new RvtReport
        {
            SchemaVersion = command == "rvt scan" ? "rvt-file-scan.v1" : "rvt-backup-cleanup-report.v1",
            Command = command,
            GeneratedAtUtc = generatedAt,
            Root = string.IsNullOrWhiteSpace(root) ? root : Path.GetFullPath(root),
            Recursive = recursive,
            DryRun = dryRun,
            ApplyRequested = apply,
            Confirmed = yes,
            IncludeOrphans = includeOrphans,
            OlderThan = string.IsNullOrWhiteSpace(olderThan) ? null : olderThan.Trim(),
        };

        TimeSpan? olderThanSpan = null;
        if (!string.IsNullOrWhiteSpace(olderThan))
        {
            try
            {
                olderThanSpan = HistoryCommand.ParseWindow(olderThan);
                report.OlderThanCutoffUtc = generatedAt - olderThanSpan.Value;
            }
            catch (FormatException ex)
            {
                report.Success = false;
                report.Mode = "invalid";
                report.Issues.Add(new RvtIssue
                {
                    Severity = "error",
                    Code = "invalid-older-than",
                    Message = ex.Message,
                });
                RecalculateSummary(report);
                return report;
            }
        }

        if (!Directory.Exists(report.Root))
        {
            report.Success = false;
            report.Mode = "invalid";
            report.Issues.Add(new RvtIssue
            {
                Severity = "error",
                Code = "root-missing",
                Path = report.Root,
                Message = $"Root directory does not exist: {report.Root}",
            });
            RecalculateSummary(report);
            return report;
        }

        var files = EnumerateRvtFiles(report.Root, recursive, report.Issues)
            .Select(path => ToFileEntry(report.Root, path))
            .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var file in files)
        {
            if (!markDeleteCandidates || file.Kind == "main")
                continue;

            var canDelete = file.Kind == "backup" || (includeOrphans && file.Kind == "orphanBackup");
            if (canDelete && olderThanSpan.HasValue)
                canDelete = file.LastWriteUtc <= report.OlderThanCutoffUtc;

            file.DeleteCandidate = canDelete;
            if (!canDelete && file.Kind == "orphanBackup" && !includeOrphans)
                file.SkippedReason = "main-rvt-missing";
            else if (!canDelete && olderThanSpan.HasValue)
                file.SkippedReason = "newer-than-threshold";
        }

        report.Files = files;
        report.Success = !report.Issues.Any(issue => string.Equals(issue.Severity, "error", StringComparison.OrdinalIgnoreCase));
        RecalculateSummary(report);
        return report;
    }

    private static IEnumerable<string> EnumerateRvtFiles(
        string root,
        bool recursive,
        List<RvtIssue> issues)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly).ToArray();
            }
            catch (Exception ex) when (IsFileSystemException(ex))
            {
                issues.Add(new RvtIssue
                {
                    Severity = "warning",
                    Code = "directory-unreadable",
                    Path = directory,
                    Message = ex.Message,
                });
                continue;
            }

            foreach (var file in files)
            {
                if (string.Equals(Path.GetExtension(file), ".rvt", StringComparison.OrdinalIgnoreCase))
                    yield return file;
            }

            if (!recursive)
                continue;

            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly).ToArray();
            }
            catch (Exception ex) when (IsFileSystemException(ex))
            {
                issues.Add(new RvtIssue
                {
                    Severity = "warning",
                    Code = "directory-children-unreadable",
                    Path = directory,
                    Message = ex.Message,
                });
                continue;
            }

            foreach (var child in children.OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (File.GetAttributes(child).HasFlag(FileAttributes.ReparsePoint))
                    {
                        issues.Add(new RvtIssue
                        {
                            Severity = "info",
                            Code = "reparse-point-skipped",
                            Path = child,
                            Message = "Skipped reparse point while scanning RVT files.",
                        });
                        continue;
                    }
                }
                catch (Exception ex) when (IsFileSystemException(ex))
                {
                    issues.Add(new RvtIssue
                    {
                        Severity = "warning",
                        Code = "directory-attributes-unreadable",
                        Path = child,
                        Message = ex.Message,
                    });
                    continue;
                }

                pending.Push(child);
            }
        }
    }

    private static RvtFileEntry ToFileEntry(string root, string path)
    {
        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        var fileName = Path.GetFileName(fullPath);
        var relativePath = Path.GetRelativePath(root, fullPath).Replace(Path.DirectorySeparatorChar, '/');
        var match = BackupFileNamePattern.Match(fileName);
        var kind = "main";
        string? backupNumber = null;
        string? mainPath = null;
        string? mainRelativePath = null;

        if (match.Success && match.Groups["number"].Value != "0000")
        {
            backupNumber = match.Groups["number"].Value;
            mainPath = FindMainRvtPath(Path.GetDirectoryName(fullPath) ?? root, match.Groups["stem"].Value);
            mainRelativePath = Path.GetRelativePath(root, mainPath).Replace(Path.DirectorySeparatorChar, '/');
            kind = File.Exists(mainPath) ? "backup" : "orphanBackup";
        }

        return new RvtFileEntry
        {
            Path = fullPath,
            RelativePath = relativePath,
            FileName = fileName,
            Kind = kind,
            SizeBytes = info.Exists ? info.Length : 0,
            LastWriteUtc = info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue,
            BackupNumber = backupNumber,
            MainPath = mainPath,
            MainRelativePath = mainRelativePath,
        };
    }

    private static string FindMainRvtPath(string directory, string stem)
    {
        var expectedName = stem + ".rvt";
        var directPath = Path.Combine(directory, expectedName);

        try
        {
            var matchedPath = Directory
                .EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(path =>
                    string.Equals(Path.GetFileName(path), expectedName, StringComparison.OrdinalIgnoreCase));
            if (matchedPath is not null)
                return matchedPath;
        }
        catch (Exception ex) when (IsFileSystemException(ex))
        {
        }

        if (File.Exists(directPath))
            return directPath;

        return directPath;
    }

    private static void ApplyDeletion(RvtReport report)
    {
        foreach (var file in report.Files.Where(file => file.DeleteCandidate))
        {
            try
            {
                File.Delete(file.Path);
                file.Deleted = true;
                file.DeleteError = null;
            }
            catch (Exception ex) when (IsFileSystemException(ex))
            {
                file.Deleted = false;
                file.DeleteError = ex.Message;
                report.Issues.Add(new RvtIssue
                {
                    Severity = "error",
                    Code = "delete-failed",
                    Path = file.Path,
                    Message = ex.Message,
                });
            }
        }
    }

    private static void RecalculateSummary(RvtReport report)
    {
        var files = report.Files;
        report.Summary = new RvtSummary
        {
            TotalRvtFileCount = files.Length,
            MainFileCount = files.Count(file => file.Kind == "main"),
            BackupFileCount = files.Count(file => file.Kind == "backup"),
            OrphanBackupFileCount = files.Count(file => file.Kind == "orphanBackup"),
            DeleteCandidateCount = files.Count(file => file.DeleteCandidate),
            DeletedCount = files.Count(file => file.Deleted),
            DeleteFailureCount = files.Count(file => file.DeleteCandidate && file.DeleteError is not null),
            TotalSizeBytes = files.Sum(file => file.SizeBytes),
            BackupSizeBytes = files.Where(file => file.Kind is "backup" or "orphanBackup").Sum(file => file.SizeBytes),
            DeleteCandidateSizeBytes = files.Where(file => file.DeleteCandidate).Sum(file => file.SizeBytes),
            DeletedSizeBytes = files.Where(file => file.Deleted).Sum(file => file.SizeBytes),
            IssueCount = report.Issues.Count,
        };
    }

    private static RvtProjectManifest BuildProjectManifest(RvtReport scan)
    {
        var backupFiles = scan.Files
            .Where(file => file.Kind == "backup")
            .ToArray();
        var projectFiles = scan.Files
            .Where(file => file.Kind == "main")
            .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var projects = projectFiles
            .Select(file =>
            {
                var backups = backupFiles
                    .Where(backup => string.Equals(backup.MainRelativePath, file.RelativePath, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(backup => backup.BackupNumber, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                return new RvtProjectEntry
                {
                    Path = file.Path,
                    RelativePath = file.RelativePath,
                    FileName = file.FileName,
                    PathHash = ComputePathHash(file.Path),
                    Classification = "candidate-project-model",
                    Confidence = "high",
                    SizeBytes = file.SizeBytes,
                    LastWriteUtc = file.LastWriteUtc,
                    BackupHint = new RvtProjectBackupHint
                    {
                        HasNumberedBackups = backups.Length > 0,
                        NumberedBackupCount = backups.Length,
                        LatestBackupNumber = backups.LastOrDefault()?.BackupNumber,
                    },
                    LocalityHint = BuildLocalityHint(file.Path),
                };
            })
            .ToArray();

        return new RvtProjectManifest
        {
            GeneratedAtUtc = scan.GeneratedAtUtc,
            Root = scan.Root,
            Recursive = scan.Recursive,
            Success = scan.Success,
            Projects = projects,
            Issues = scan.Issues.ToList(),
            Summary = new RvtProjectManifestSummary
            {
                ProjectCount = projects.Length,
                BackupFileCount = scan.Summary.BackupFileCount,
                OrphanBackupFileCount = scan.Summary.OrphanBackupFileCount,
                ProjectSizeBytes = projects.Sum(project => project.SizeBytes),
                BackupSizeBytes = scan.Summary.BackupSizeBytes,
                TotalSizeBytes = scan.Summary.TotalSizeBytes,
                IssueCount = scan.Issues.Count,
            },
        };
    }

    private static string ComputePathHash(string path)
    {
        var normalized = Path.GetFullPath(path)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/')
            .TrimEnd('/');
        if (OperatingSystem.IsWindows())
            normalized = normalized.ToUpperInvariant();

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
    }

    private static RvtProjectLocalityHint BuildLocalityHint(string path)
    {
        var normalized = Path.GetFullPath(path).Replace('\\', '/');
        if (normalized.StartsWith("//", StringComparison.Ordinal))
        {
            return new RvtProjectLocalityHint
            {
                Kind = "network",
                Confidence = "high",
                Reason = "Path uses a UNC network prefix.",
            };
        }

        if (normalized.Contains("/ACCDocs/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("/Autodesk Docs/", StringComparison.OrdinalIgnoreCase))
        {
            return new RvtProjectLocalityHint
            {
                Kind = "desktop-connector",
                Confidence = "medium",
                Reason = "Path resembles an Autodesk Desktop Connector workspace.",
            };
        }

        return new RvtProjectLocalityHint
        {
            Kind = "local",
            Confidence = "high",
            Reason = "Path is on a local or mapped file system.",
        };
    }

    private static async Task<int> WriteProjectManifestAsync(string manifestOutputPath, RvtProjectManifest manifest)
    {
        try
        {
            var fullPath = Path.GetFullPath(manifestOutputPath);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            manifest.ManifestPath = fullPath;
            await File.WriteAllTextAsync(
                fullPath,
                JsonSerializer.Serialize(manifest, TerminalJsonOptions.PrettyCamel) + Environment.NewLine);
            return 0;
        }
        catch (Exception ex) when (IsFileSystemException(ex))
        {
            manifest.Success = false;
            manifest.Issues.Add(new RvtIssue
            {
                Severity = "error",
                Code = "manifest-write-failed",
                Path = manifestOutputPath,
                Message = ex.Message,
            });
            manifest.Summary = manifest.Summary with { IssueCount = manifest.Issues.Count };
            return 1;
        }
    }

    private static async Task WriteManifestOutputAsync(TextWriter output, RvtProjectManifest manifest, string outputFormat)
    {
        switch (outputFormat)
        {
            case "json":
                await output.WriteLineAsync(JsonSerializer.Serialize(manifest, TerminalJsonOptions.PrettyCamel));
                break;
            case "markdown":
                await output.WriteLineAsync(RenderManifestMarkdown(manifest));
                break;
            default:
                await output.WriteLineAsync(RenderManifestTable(manifest));
                break;
        }
    }

    private static string RenderManifestTable(RvtProjectManifest manifest)
    {
        var lines = new List<string>
        {
            "RVT project manifest",
            $"Root: {manifest.Root}",
            $"Projects: {manifest.Summary.ProjectCount}",
            $"Backups with main file: {manifest.Summary.BackupFileCount}",
            $"Orphan backups: {manifest.Summary.OrphanBackupFileCount}",
        };

        if (!string.IsNullOrWhiteSpace(manifest.ManifestPath))
            lines.Add($"Manifest: {manifest.ManifestPath}");

        if (manifest.Issues.Count > 0)
        {
            lines.Add("Issues:");
            foreach (var issue in manifest.Issues.Take(20))
                lines.Add($"  {issue.Severity}: {issue.Code}: {issue.Message}");
            if (manifest.Issues.Count > 20)
                lines.Add($"  ... and {manifest.Issues.Count - 20} more.");
        }

        foreach (var project in manifest.Projects.Take(50))
        {
            var backupText = project.BackupHint.NumberedBackupCount == 0
                ? "no backups"
                : $"{project.BackupHint.NumberedBackupCount} backups";
            lines.Add($"  {project.RelativePath}  {FormatBytes(project.SizeBytes)}  {project.LocalityHint.Kind}  {backupText}");
        }

        if (manifest.Projects.Length > 50)
            lines.Add($"  ... and {manifest.Projects.Length - 50} more.");

        return string.Join(Environment.NewLine, lines);
    }

    private static string RenderManifestMarkdown(RvtProjectManifest manifest)
    {
        var lines = new List<string>
        {
            "# RVT Project Manifest",
            "",
            $"- Root: `{EscapeMarkdown(manifest.Root)}`",
            $"- Recursive: `{manifest.Recursive.ToString().ToLowerInvariant()}`",
            $"- Projects: `{manifest.Summary.ProjectCount}`",
            $"- Backups with main file: `{manifest.Summary.BackupFileCount}`",
            $"- Orphan backups: `{manifest.Summary.OrphanBackupFileCount}`",
        };

        if (!string.IsNullOrWhiteSpace(manifest.ManifestPath))
            lines.Add($"- Manifest: `{EscapeMarkdown(manifest.ManifestPath)}`");

        lines.Add("");

        if (manifest.Issues.Count > 0)
        {
            lines.Add("## Issues");
            lines.Add("");
            lines.Add("| Severity | Code | Path | Message |");
            lines.Add("| --- | --- | --- | --- |");
            foreach (var issue in manifest.Issues)
            {
                lines.Add(
                    $"| {EscapeMarkdown(issue.Severity)} | `{EscapeMarkdown(issue.Code)}` | `{EscapeMarkdown(issue.Path ?? "")}` | {EscapeMarkdown(issue.Message)} |");
            }
            lines.Add("");
        }

        if (manifest.Projects.Length > 0)
        {
            lines.Add("## Projects");
            lines.Add("");
            lines.Add("| Path | Classification | Confidence | Locality | Backups | Path hash |");
            lines.Add("| --- | --- | --- | --- | ---: | --- |");
            foreach (var project in manifest.Projects)
            {
                lines.Add(
                    $"| `{EscapeMarkdown(project.RelativePath)}` | `{project.Classification}` | `{project.Confidence}` | `{project.LocalityHint.Kind}` | {project.BackupHint.NumberedBackupCount} | `{project.PathHash}` |");
            }
            lines.Add("");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static async Task<int> WriteReportAsync(string reportPath, RvtReport report)
    {
        try
        {
            var fullPath = Path.GetFullPath(reportPath);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            report.ReportPath = fullPath;
            await File.WriteAllTextAsync(
                fullPath,
                JsonSerializer.Serialize(report, TerminalJsonOptions.PrettyCamel) + Environment.NewLine);
            return 0;
        }
        catch (Exception ex) when (IsFileSystemException(ex))
        {
            report.Success = false;
            report.Issues.Add(new RvtIssue
            {
                Severity = "error",
                Code = "report-write-failed",
                Path = reportPath,
                Message = ex.Message,
            });
            RecalculateSummary(report);
            return 1;
        }
    }

    private static async Task WriteOutputAsync(TextWriter output, RvtReport report, string outputFormat)
    {
        switch (outputFormat)
        {
            case "json":
                await output.WriteLineAsync(JsonSerializer.Serialize(report, TerminalJsonOptions.PrettyCamel));
                break;
            case "markdown":
                await output.WriteLineAsync(RenderMarkdown(report));
                break;
            default:
                await output.WriteLineAsync(RenderTable(report));
                break;
        }
    }

    private static string RenderTable(RvtReport report)
    {
        var lines = new List<string>
        {
            report.Command == "rvt scan" ? "RVT file scan" : "RVT backup cleanup",
            $"Root: {report.Root}",
            $"Mode: {report.Mode}",
            $"RVT files: {report.Summary.TotalRvtFileCount}",
            $"Main files: {report.Summary.MainFileCount}",
            $"Backups with main file: {report.Summary.BackupFileCount}",
            $"Orphan backups: {report.Summary.OrphanBackupFileCount}",
            $"Delete candidates: {report.Summary.DeleteCandidateCount} ({FormatBytes(report.Summary.DeleteCandidateSizeBytes)})",
        };

        if (report.Summary.DeletedCount > 0 || report.ApplyRequested)
            lines.Add($"Deleted: {report.Summary.DeletedCount} ({FormatBytes(report.Summary.DeletedSizeBytes)})");

        if (report.Issues.Count > 0)
        {
            lines.Add("Issues:");
            foreach (var issue in report.Issues.Take(20))
                lines.Add($"  {issue.Severity}: {issue.Code}: {issue.Message}");
            if (report.Issues.Count > 20)
                lines.Add($"  ... and {report.Issues.Count - 20} more.");
        }

        var candidates = report.Files.Where(file => file.DeleteCandidate).Take(50).ToArray();
        if (candidates.Length > 0)
        {
            lines.Add(report.DryRun ? "Would delete:" : "Delete candidates:");
            foreach (var file in candidates)
                lines.Add($"  {file.RelativePath}  {FormatBytes(file.SizeBytes)}");
            if (report.Summary.DeleteCandidateCount > candidates.Length)
                lines.Add($"  ... and {report.Summary.DeleteCandidateCount - candidates.Length} more.");
        }

        if (report.Command == "rvt clean-backups" && report.DryRun && report.Summary.DeleteCandidateCount > 0)
            lines.Add("Re-run with --apply --yes to delete these backup files.");

        return string.Join(Environment.NewLine, lines);
    }

    private static string RenderMarkdown(RvtReport report)
    {
        var title = report.Command == "rvt scan" ? "RVT File Scan" : "RVT Backup Cleanup";
        var lines = new List<string>
        {
            $"# {title}",
            "",
            $"- Root: `{EscapeMarkdown(report.Root)}`",
            $"- Recursive: `{report.Recursive.ToString().ToLowerInvariant()}`",
            $"- Include orphan backups: `{report.IncludeOrphans.ToString().ToLowerInvariant()}`",
            $"- Mode: `{report.Mode}`",
            $"- RVT files: `{report.Summary.TotalRvtFileCount}`",
            $"- Main files: `{report.Summary.MainFileCount}`",
            $"- Backups with main file: `{report.Summary.BackupFileCount}`",
            $"- Orphan backups: `{report.Summary.OrphanBackupFileCount}`",
            $"- Delete candidates: `{report.Summary.DeleteCandidateCount}` (`{FormatBytes(report.Summary.DeleteCandidateSizeBytes)}`)",
            "",
        };

        if (report.Issues.Count > 0)
        {
            lines.Add("## Issues");
            lines.Add("");
            lines.Add("| Severity | Code | Path | Message |");
            lines.Add("| --- | --- | --- | --- |");
            foreach (var issue in report.Issues)
            {
                lines.Add(
                    $"| {EscapeMarkdown(issue.Severity)} | `{EscapeMarkdown(issue.Code)}` | `{EscapeMarkdown(issue.Path ?? "")}` | {EscapeMarkdown(issue.Message)} |");
            }
            lines.Add("");
        }

        var backups = report.Files.Where(file => file.Kind is "backup" or "orphanBackup").ToArray();
        if (backups.Length > 0)
        {
            lines.Add("## Backup Files");
            lines.Add("");
            lines.Add("| Path | Kind | Size | Last write UTC | Main file | Delete candidate | Deleted |");
            lines.Add("| --- | --- | ---: | --- | --- | --- | --- |");
            foreach (var file in backups)
            {
                lines.Add(
                    $"| `{EscapeMarkdown(file.RelativePath)}` | `{file.Kind}` | {FormatBytes(file.SizeBytes)} | `{file.LastWriteUtc:O}` | `{EscapeMarkdown(file.MainRelativePath ?? "")}` | `{file.DeleteCandidate.ToString().ToLowerInvariant()}` | `{file.Deleted.ToString().ToLowerInvariant()}` |");
            }
            lines.Add("");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} {units[unit]}" : $"{value:0.##} {units[unit]}";
    }

    private static string EscapeMarkdown(string value) =>
        value.Replace("|", "\\|", StringComparison.Ordinal);

    private static bool IsFileSystemException(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException;

    public sealed class RvtReport
    {
        public string SchemaVersion { get; init; } = "";
        public string Command { get; init; } = "";
        public bool Success { get; set; } = true;
        public string Mode { get; set; } = "scan";
        public DateTimeOffset GeneratedAtUtc { get; init; }
        public string Root { get; init; } = "";
        public bool Recursive { get; init; }
        public bool DryRun { get; init; }
        public bool ApplyRequested { get; init; }
        public bool Confirmed { get; init; }
        public bool IncludeOrphans { get; init; }
        public string? OlderThan { get; init; }
        public DateTimeOffset? OlderThanCutoffUtc { get; set; }
        public string? ReportPath { get; set; }
        public RvtSummary Summary { get; set; } = new();
        public RvtFileEntry[] Files { get; set; } = Array.Empty<RvtFileEntry>();
        public List<RvtIssue> Issues { get; } = new();
    }

    public sealed class RvtSummary
    {
        public int TotalRvtFileCount { get; init; }
        public int MainFileCount { get; init; }
        public int BackupFileCount { get; init; }
        public int OrphanBackupFileCount { get; init; }
        public int DeleteCandidateCount { get; init; }
        public int DeletedCount { get; init; }
        public int DeleteFailureCount { get; init; }
        public long TotalSizeBytes { get; init; }
        public long BackupSizeBytes { get; init; }
        public long DeleteCandidateSizeBytes { get; init; }
        public long DeletedSizeBytes { get; init; }
        public int IssueCount { get; init; }
    }

    public sealed class RvtFileEntry
    {
        public string Path { get; init; } = "";
        public string RelativePath { get; init; } = "";
        public string FileName { get; init; } = "";
        public string Kind { get; init; } = "";
        public long SizeBytes { get; init; }
        public DateTime LastWriteUtc { get; init; }
        public string? BackupNumber { get; init; }
        public string? MainPath { get; init; }
        public string? MainRelativePath { get; init; }
        public bool DeleteCandidate { get; set; }
        public bool Deleted { get; set; }
        public string? SkippedReason { get; set; }
        public string? DeleteError { get; set; }
    }

    public sealed class RvtIssue
    {
        public string Severity { get; init; } = "";
        public string Code { get; init; } = "";
        public string? Path { get; init; }
        public string Message { get; init; } = "";
    }

    public sealed class RvtProjectManifest
    {
        public string SchemaVersion { get; init; } = "rvt-project-manifest.v1";
        public string Command { get; init; } = "rvt manifest";
        public bool Success { get; set; } = true;
        public DateTimeOffset GeneratedAtUtc { get; init; }
        public string Root { get; init; } = "";
        public bool Recursive { get; init; }
        public string? ManifestPath { get; set; }
        public RvtProjectManifestSummary Summary { get; set; } = new();
        public RvtProjectEntry[] Projects { get; init; } = Array.Empty<RvtProjectEntry>();
        public List<RvtIssue> Issues { get; init; } = new();
    }

    public sealed record RvtProjectManifestSummary
    {
        public int ProjectCount { get; init; }
        public int BackupFileCount { get; init; }
        public int OrphanBackupFileCount { get; init; }
        public long ProjectSizeBytes { get; init; }
        public long BackupSizeBytes { get; init; }
        public long TotalSizeBytes { get; init; }
        public int IssueCount { get; init; }
    }

    public sealed class RvtProjectEntry
    {
        public string Path { get; init; } = "";
        public string RelativePath { get; init; } = "";
        public string FileName { get; init; } = "";
        public string PathHash { get; init; } = "";
        public string Classification { get; init; } = "";
        public string Confidence { get; init; } = "";
        public long SizeBytes { get; init; }
        public DateTime LastWriteUtc { get; init; }
        public RvtProjectBackupHint BackupHint { get; init; } = new();
        public RvtProjectLocalityHint LocalityHint { get; init; } = new();
    }

    public sealed class RvtProjectBackupHint
    {
        public bool HasNumberedBackups { get; init; }
        public int NumberedBackupCount { get; init; }
        public string? LatestBackupNumber { get; init; }
    }

    public sealed class RvtProjectLocalityHint
    {
        public string Kind { get; init; } = "";
        public string Confidence { get; init; } = "";
        public string Reason { get; init; } = "";
    }
}
