using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using RevitCli.Client;
using RevitCli.Profile;
using RevitCli.Shared;

namespace RevitCli.Checks;

internal static class CheckRunner
{
    public static async Task<CheckRunnerResponse> RunAsync(RevitClient client, string? name, string? profilePath)
    {
        ProjectProfile? profile;
        string? resolvedProfilePath = null;

        try
        {
            if (profilePath != null)
            {
                resolvedProfilePath = Path.GetFullPath(profilePath);
                profile = ProfileLoader.Load(profilePath);
            }
            else
            {
                var discoveredPath = ProfileLoader.Discover();
                if (discoveredPath != null)
                {
                    resolvedProfilePath = Path.GetFullPath(discoveredPath);
                    profile = ProfileLoader.Load(discoveredPath);
                }
                else
                {
                    profile = null;
                }
            }
        }
        catch (Exception ex)
        {
            return CheckRunnerResponse.Fail($"Error loading profile: {ex.Message}");
        }

        if (profile == null)
        {
            return CheckRunnerResponse.Fail(
                $"Error: no {ProfileLoader.FileName} found.{Environment.NewLine}" +
                "  Create one in your project root, or copy from .revitcli.example.yml");
        }

        var checkName = name ?? "default";
        if (!profile.Checks.TryGetValue(checkName, out var checkDef))
        {
            var message = $"Error: check set '{checkName}' not found in profile.";
            if (profile.Checks.Count > 0)
                message += $"{Environment.NewLine}  Available check sets: {string.Join(", ", profile.Checks.Keys)}";
            else
                message += $"{Environment.NewLine}  Your profile has no check sets defined. Add a 'checks:' section.";
            return CheckRunnerResponse.Fail(message);
        }

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
            return CheckRunnerResponse.Fail($"Error: {result.Error}");

        var allIssues = new List<AuditIssue>(result.Data!.Issues);
        var suppressedCount = 0;
        if (checkDef.Suppressions.Count > 0)
        {
            var activeSuppressions = checkDef.Suppressions
                .Where(s => !CheckSuppressionRules.IsExpired(s.Expires))
                .ToList();

            var filtered = new List<AuditIssue>();
            foreach (var issue in allIssues)
            {
                if (CheckSuppressionRules.IsSuppressed(issue, activeSuppressions))
                    suppressedCount++;
                else
                    filtered.Add(issue);
            }
            allIssues = filtered;
        }

        var displayFailed = allIssues.Count(i => i.Severity is "error" or "warning");
        var displayPassed = Math.Max(0, result.Data.Passed + result.Data.Failed - displayFailed);

        return CheckRunnerResponse.Ok(new CheckRunResult
        {
            Profile = profile,
            CheckDefinition = checkDef,
            CheckName = checkName,
            ProfilePath = resolvedProfilePath,
            Issues = allIssues,
            SuppressedCount = suppressedCount,
            DisplayPassed = displayPassed,
            DisplayFailed = displayFailed
        });
    }
}

internal sealed class CheckRunnerResponse
{
    public bool Success { get; init; }
    public CheckRunResult? Data { get; init; }
    public string Error { get; init; } = "";

    public static CheckRunnerResponse Ok(CheckRunResult data) => new() { Success = true, Data = data };
    public static CheckRunnerResponse Fail(string error) => new() { Success = false, Error = error };
}
