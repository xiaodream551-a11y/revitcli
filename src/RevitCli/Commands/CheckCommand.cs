using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using RevitCli.Client;
using RevitCli.Output;
using RevitCli.Profile;
using RevitCli.Shared;
using Spectre.Console;

namespace RevitCli.Commands;

public static class CheckCommand
{
    public static Command Create(RevitClient client)
    {
        var nameArg = new Argument<string?>("name", () => null, "Check set name (default: 'default')");
        var profileOpt = new Option<string?>("--profile", "Path to .revitcli.yml profile");
        var outputOpt = new Option<string>("--output", () => "table", "Output format: table, json, html");
        var reportOpt = new Option<string?>("--report", "Save report to file (format inferred from extension, or uses --output)");
        var noSaveOpt = new Option<bool>("--no-save", "Don't save results for diff comparison");

        var command = new Command("check", "Run project checks from .revitcli.yml profile")
        {
            nameArg, profileOpt, outputOpt, reportOpt, noSaveOpt
        };

        command.SetHandler(async (name, profilePath, outputFormat, reportPath, noSave) =>
        {
            Environment.ExitCode = await ExecuteAsync(client, name, profilePath, outputFormat, reportPath, noSave, Console.Out);
        }, nameArg, profileOpt, outputOpt, reportOpt, noSaveOpt);

        return command;
    }

    public static async Task<int> ExecuteAsync(RevitClient client, string? name, string? profilePath,
        string outputFormat, string? reportPath, bool noSave, TextWriter output)
    {
        // Load profile
        ProjectProfile? profile;
        try
        {
            if (profilePath != null)
                profile = ProfileLoader.Load(profilePath);
            else
                profile = ProfileLoader.DiscoverAndLoad();
        }
        catch (Exception ex)
        {
            await output.WriteLineAsync($"Error loading profile: {ex.Message}");
            return 1;
        }

        if (profile == null)
        {
            await output.WriteLineAsync($"Error: no {ProfileLoader.FileName} found. Create one in your project root.");
            return 1;
        }

        var checkName = name ?? "default";
        if (!profile.Checks.TryGetValue(checkName, out var checkDef))
        {
            await output.WriteLineAsync($"Error: check set '{checkName}' not found in profile.");
            if (profile.Checks.Count > 0)
                await output.WriteLineAsync($"Available: {string.Join(", ", profile.Checks.Keys)}");
            return 1;
        }

        var allIssues = new List<AuditIssue>();
        var totalPassed = 0;
        var totalFailed = 0;

        // Build single audit request with all checks
        var request = new AuditRequest
        {
            Rules = checkDef.AuditRules.Select(r => r.Rule).ToList(),
            RequiredParameters = checkDef.RequiredParameters.Select(r => new RequiredParameterSpec
            {
                Category = r.Category,
                Parameter = r.Parameter,
                RequireNonEmpty = r.RequireNonEmpty,
                Severity = r.Severity
            }).ToList(),
            NamingPatterns = checkDef.Naming.Select(n => new NamingPatternSpec
            {
                Target = n.Target,
                Pattern = n.Pattern,
                Severity = n.Severity
            }).ToList()
        };

        var result = await client.AuditAsync(request);
        if (!result.Success)
        {
            await output.WriteLineAsync($"Error: {result.Error}");
            return 1;
        }

        totalPassed = result.Data!.Passed;
        totalFailed = result.Data!.Failed;
        allIssues.AddRange(result.Data!.Issues);

        // Apply suppressions
        var suppressedCount = 0;
        if (checkDef.Suppressions.Count > 0)
        {
            var activeSuppressions = checkDef.Suppressions
                .Where(s => !IsExpired(s.Expires))
                .ToList();

            var filtered = new List<AuditIssue>();
            foreach (var issue in allIssues)
            {
                if (IsSuppressed(issue, activeSuppressions))
                    suppressedCount++;
                else
                    filtered.Add(issue);
            }
            allIssues = filtered;

            // Recount: failed = has any error/warning issues remaining
            var hasRemainingProblems = allIssues.Any(i => i.Severity is "error" or "warning");
            if (!hasRemainingProblems)
            {
                totalPassed += totalFailed;
                totalFailed = 0;
            }
        }

        // Render output
        var format = outputFormat.ToLowerInvariant();

        // Infer format from report file extension if provided
        if (reportPath != null)
        {
            var ext = Path.GetExtension(reportPath).ToLowerInvariant();
            if (ext == ".html" || ext == ".htm")
                format = "html";
            else if (ext == ".json")
                format = "json";
        }

        var rendered = format switch
        {
            "json" => CheckReportRenderer.RenderJson(checkName, totalPassed, totalFailed, allIssues, suppressedCount),
            "html" => CheckReportRenderer.RenderHtml(checkName, totalPassed, totalFailed, allIssues, suppressedCount),
            _ => CheckReportRenderer.RenderTable(checkName, totalPassed, totalFailed, allIssues, suppressedCount)
        };

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
                CheckReportRenderer.RenderTable(checkName, totalPassed, totalFailed, allIssues, suppressedCount));
        }
        else
        {
            await output.WriteLineAsync(rendered);
        }

        // Diff + save (only for table output — don't pollute JSON/HTML)
        var profileDir = profilePath != null
            ? Path.GetDirectoryName(Path.GetFullPath(profilePath))
            : (ProfileLoader.Discover() is { } discovered
                ? Path.GetDirectoryName(Path.GetFullPath(discovered))
                : null);

        if (!noSave)
        {
            try
            {
                // Diff against latest (before rotation)
                var diff = CheckResultStore.ComputeDiffAgainstLatest(checkName, allIssues, profileDir);
                CheckResultStore.Save(checkName, totalPassed, totalFailed, suppressedCount, allIssues, profileDir);

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

        // Determine exit code based on failOn
        var failOn = checkDef.FailOn.ToLowerInvariant();
        var hasErrors = allIssues.Any(i => i.Severity == "error");
        var hasWarnings = allIssues.Any(i => i.Severity == "warning");

        var exitCode = 0;
        if (failOn == "error" && hasErrors)
            exitCode = 1;
        else if (failOn == "warning" && (hasErrors || hasWarnings))
            exitCode = 1;

        // Webhook notification
        if (!string.IsNullOrWhiteSpace(profile.Defaults.Notify))
        {
            await WebhookNotifier.NotifyAsync(profile.Defaults.Notify, new
            {
                type = "check",
                check = checkName,
                passed = totalPassed,
                failed = totalFailed,
                suppressed = suppressedCount,
                issueCount = allIssues.Count,
                status = exitCode == 0 ? "passed" : "failed",
                timestamp = DateTime.UtcNow.ToString("o")
            });
        }

        return exitCode;
    }

    private static bool IsSuppressed(AuditIssue issue, List<Profile.Suppression> suppressions)
    {
        foreach (var s in suppressions)
        {
            if (!string.Equals(s.Rule, issue.Rule, StringComparison.OrdinalIgnoreCase))
                continue;

            // Fine-grained matching: category and parameter narrow the scope
            if (!string.IsNullOrEmpty(s.Category) &&
                !issue.Message.Contains(s.Category, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!string.IsNullOrEmpty(s.Parameter) &&
                !issue.Message.Contains(s.Parameter, StringComparison.OrdinalIgnoreCase))
                continue;

            // If elementIds specified, only suppress those specific elements
            if (s.ElementIds != null && s.ElementIds.Count > 0)
            {
                if (issue.ElementId.HasValue && s.ElementIds.Contains(issue.ElementId.Value))
                    return true;
            }
            else
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsExpired(string? expires)
    {
        if (string.IsNullOrWhiteSpace(expires))
            return false;

        if (DateTime.TryParse(expires, out var expiryDate))
            return DateTime.Now > expiryDate;

        return false;
    }
}
