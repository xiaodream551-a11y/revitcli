using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using RevitCli.Shared;

namespace RevitCli.Output;

public class StoredCheckResult
{
    [JsonPropertyName("check")]
    public string Check { get; set; } = "";

    [JsonPropertyName("passed")]
    public int Passed { get; set; }

    [JsonPropertyName("failed")]
    public int Failed { get; set; }

    [JsonPropertyName("suppressed")]
    public int Suppressed { get; set; }

    [JsonPropertyName("timestamp")]
    public string Timestamp { get; set; } = "";

    [JsonPropertyName("issues")]
    public List<StoredIssue> Issues { get; set; } = new();
}

public class StoredIssue
{
    [JsonPropertyName("rule")]
    public string Rule { get; set; } = "";

    [JsonPropertyName("severity")]
    public string Severity { get; set; } = "";

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("elementId")]
    public long? ElementId { get; set; }

    /// <summary>Stable key for diffing: rule + elementId (or message hash if no elementId)</summary>
    [JsonIgnore]
    public string DiffKey => ElementId.HasValue
        ? $"{Rule}:{ElementId}"
        : $"{Rule}:{Message.GetHashCode()}";
}

public class CheckDiff
{
    public List<StoredIssue> New { get; set; } = new();
    public List<StoredIssue> Resolved { get; set; } = new();
    public int Unchanged { get; set; }
}

public static class CheckResultStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static string GetResultsDir(string? profileDir)
    {
        var baseDir = profileDir ?? Directory.GetCurrentDirectory();
        return Path.Combine(baseDir, ".revitcli", "results");
    }

    private static string GetLatestPath(string resultsDir, string checkName) =>
        Path.Combine(resultsDir, $"{checkName}-latest.json");

    private static string GetPreviousPath(string resultsDir, string checkName) =>
        Path.Combine(resultsDir, $"{checkName}-previous.json");

    public static void Save(string checkName, int passed, int failed, int suppressed,
        List<AuditIssue> issues, string? profileDir)
    {
        var resultsDir = GetResultsDir(profileDir);
        Directory.CreateDirectory(resultsDir);

        var latestPath = GetLatestPath(resultsDir, checkName);
        var previousPath = GetPreviousPath(resultsDir, checkName);

        // Rotate: latest → previous
        if (File.Exists(latestPath))
        {
            if (File.Exists(previousPath))
                File.Delete(previousPath);
            File.Move(latestPath, previousPath);
        }

        var result = new StoredCheckResult
        {
            Check = checkName,
            Passed = passed,
            Failed = failed,
            Suppressed = suppressed,
            Timestamp = DateTime.UtcNow.ToString("o"),
            Issues = issues.Select(i => new StoredIssue
            {
                Rule = i.Rule,
                Severity = i.Severity,
                Message = i.Message,
                ElementId = i.ElementId
            }).ToList()
        };

        File.WriteAllText(latestPath, JsonSerializer.Serialize(result, JsonOpts));
    }

    public static CheckDiff? ComputeDiff(string checkName, List<AuditIssue> currentIssues, string? profileDir)
    {
        var resultsDir = GetResultsDir(profileDir);
        // Compare against previous (which is the last-saved-before-current-run)
        var previousPath = GetPreviousPath(resultsDir, checkName);
        if (!File.Exists(previousPath))
            return null;

        StoredCheckResult? previous;
        try
        {
            var json = File.ReadAllText(previousPath);
            previous = JsonSerializer.Deserialize<StoredCheckResult>(json);
        }
        catch
        {
            return null;
        }

        if (previous == null)
            return null;

        var prevKeys = new HashSet<string>(previous.Issues.Select(i => i.DiffKey));
        var currIssues = currentIssues.Select(i => new StoredIssue
        {
            Rule = i.Rule, Severity = i.Severity, Message = i.Message, ElementId = i.ElementId
        }).ToList();
        var currKeys = new HashSet<string>(currIssues.Select(i => i.DiffKey));

        return new CheckDiff
        {
            New = currIssues.Where(i => !prevKeys.Contains(i.DiffKey)).ToList(),
            Resolved = previous.Issues.Where(i => !currKeys.Contains(i.DiffKey)).ToList(),
            Unchanged = currKeys.Intersect(prevKeys).Count()
        };
    }
}
