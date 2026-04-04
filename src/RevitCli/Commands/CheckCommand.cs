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

        var command = new Command("check", "Run project checks from .revitcli.yml profile")
        {
            nameArg, profileOpt
        };

        command.SetHandler(async (name, profilePath) =>
        {
            Environment.ExitCode = await ExecuteAsync(client, name, profilePath, Console.Out);
        }, nameArg, profileOpt);

        return command;
    }

    public static async Task<int> ExecuteAsync(RevitClient client, string? name, string? profilePath, TextWriter output)
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

        // Run built-in audit rules
        if (checkDef.AuditRules.Count > 0)
        {
            var ruleNames = checkDef.AuditRules.Select(r => r.Rule).ToList();
            var request = new AuditRequest { Rules = ruleNames };
            var result = await client.AuditAsync(request);

            if (!result.Success)
            {
                await output.WriteLineAsync($"Error: {result.Error}");
                return 1;
            }

            totalPassed += result.Data!.Passed;
            totalFailed += result.Data!.Failed;
            allIssues.AddRange(result.Data!.Issues);
        }

        // Run required parameter checks (client-side via query + inspect)
        foreach (var req in checkDef.RequiredParameters)
        {
            var queryResult = await client.QueryElementsAsync(req.Category, null);
            if (!queryResult.Success)
            {
                allIssues.Add(new AuditIssue
                {
                    Rule = "required-parameter",
                    Severity = "error",
                    Message = $"Failed to query {req.Category}: {queryResult.Error}"
                });
                totalFailed++;
                continue;
            }

            var elements = queryResult.Data!;
            var missing = elements.Where(e =>
                !e.Parameters.ContainsKey(req.Parameter) ||
                (req.RequireNonEmpty && string.IsNullOrWhiteSpace(e.Parameters.GetValueOrDefault(req.Parameter)))
            ).ToList();

            if (missing.Count == 0)
            {
                totalPassed++;
            }
            else
            {
                totalFailed++;
                foreach (var elem in missing.Take(20)) // cap per check
                {
                    allIssues.Add(new AuditIssue
                    {
                        Rule = "required-parameter",
                        Severity = req.Severity,
                        Message = $"{req.Category} '{elem.Name}' is missing required parameter '{req.Parameter}'.",
                        ElementId = elem.Id
                    });
                }
                if (missing.Count > 20)
                {
                    allIssues.Add(new AuditIssue
                    {
                        Rule = "required-parameter",
                        Severity = "info",
                        Message = $"... and {missing.Count - 20} more {req.Category} elements missing '{req.Parameter}'."
                    });
                }
            }
        }

        // Print results
        await output.WriteLineAsync($"Check '{checkName}': {totalPassed} passed, {totalFailed} failed");

        foreach (var issue in allIssues)
        {
            var prefix = issue.Severity == "error" ? "ERROR" : issue.Severity == "warning" ? "WARN" : "INFO";
            var elementRef = issue.ElementId.HasValue ? $" [Element {issue.ElementId}]" : "";
            await output.WriteLineAsync($"  [{prefix}] {issue.Rule}: {issue.Message}{elementRef}");
        }

        // Determine exit code based on failOn
        var failOn = checkDef.FailOn.ToLowerInvariant();
        var hasErrors = allIssues.Any(i => i.Severity == "error");
        var hasWarnings = allIssues.Any(i => i.Severity == "warning");

        if (failOn == "error" && hasErrors)
            return 1;
        if (failOn == "warning" && (hasErrors || hasWarnings))
            return 1;

        return 0;
    }
}
