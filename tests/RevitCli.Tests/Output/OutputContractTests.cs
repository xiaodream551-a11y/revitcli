using System;
using System.Collections.Generic;
using System.Text.Json;
using RevitCli.Output;
using RevitCli.Shared;
using Xunit;

namespace RevitCli.Tests.Output;

/// <summary>
/// Contract tests — these lock down the machine-readable output formats
/// that CI/scripts depend on. Do NOT change these without bumping a major version.
/// </summary>
public class OutputContractTests
{
    // ── Check JSON contract ─────────────────────────────────────

    [Fact]
    public void CheckJson_ContainsRequiredFields()
    {
        var issues = new List<AuditIssue>
        {
            new() { Rule = "naming", Severity = "warning", Message = "Default name", ElementId = 100 }
        };
        var json = CheckReportRenderer.RenderJson("default", 3, 1, issues, 2);
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Required top-level fields
        Assert.Equal("default", root.GetProperty("check").GetString());
        Assert.Equal(3, root.GetProperty("passed").GetInt32());
        Assert.Equal(1, root.GetProperty("failed").GetInt32());
        Assert.Equal(2, root.GetProperty("suppressed").GetInt32());
        Assert.True(root.TryGetProperty("timestamp", out _));
        Assert.True(root.TryGetProperty("issues", out var issuesArr));
        Assert.Equal(JsonValueKind.Array, issuesArr.ValueKind);
    }

    [Fact]
    public void CheckJson_IssueContainsRequiredFields()
    {
        var issues = new List<AuditIssue>
        {
            new() { Rule = "room-bounds", Severity = "error", Message = "Zero area", ElementId = 500 }
        };
        var json = CheckReportRenderer.RenderJson("test", 0, 1, issues);
        var doc = JsonDocument.Parse(json);
        var issue = doc.RootElement.GetProperty("issues").EnumerateArray().First();

        Assert.Equal("room-bounds", issue.GetProperty("rule").GetString());
        Assert.Equal("error", issue.GetProperty("severity").GetString());
        Assert.Equal("Zero area", issue.GetProperty("message").GetString());
        Assert.Equal(500, issue.GetProperty("elementId").GetInt64());
    }

    [Fact]
    public void CheckJson_NullElementId_OmittedFromOutput()
    {
        var issues = new List<AuditIssue>
        {
            new() { Rule = "naming", Severity = "info", Message = "Test" }
        };
        var json = CheckReportRenderer.RenderJson("test", 1, 0, issues);
        var doc = JsonDocument.Parse(json);
        var issue = doc.RootElement.GetProperty("issues").EnumerateArray().First();

        // elementId should be omitted (WhenWritingNull)
        Assert.False(issue.TryGetProperty("elementId", out _));
    }

    // ── Check table contract ────────────────────────────────────

    [Fact]
    public void CheckTable_FirstLineIsSummary()
    {
        var issues = new List<AuditIssue>();
        var table = CheckReportRenderer.RenderTable("default", 5, 0, issues, 3);
        var firstLine = table.Split(Environment.NewLine)[0];

        Assert.Contains("Check 'default'", firstLine);
        Assert.Contains("5 passed", firstLine);
        Assert.Contains("0 failed", firstLine);
        Assert.Contains("3 suppressed", firstLine);
    }

    [Fact]
    public void CheckTable_IssueFormat()
    {
        var issues = new List<AuditIssue>
        {
            new() { Rule = "naming", Severity = "error", Message = "Bad name", ElementId = 42 }
        };
        var table = CheckReportRenderer.RenderTable("test", 0, 1, issues);

        Assert.Contains("[ERROR]", table);
        Assert.Contains("naming:", table);
        Assert.Contains("[Element 42]", table);
    }

    // ── Check HTML contract ─────────────────────────────────────

    [Fact]
    public void CheckHtml_IsDarkMode()
    {
        var html = CheckReportRenderer.RenderHtml("test", 1, 0, new List<AuditIssue>());
        Assert.Contains("background: #1a1a2e", html);
        Assert.Contains("color: #e0e0e0", html);
    }

    [Fact]
    public void CheckHtml_ContainsStatusAndCounts()
    {
        var html = CheckReportRenderer.RenderHtml("mycheck", 3, 2, new List<AuditIssue>
        {
            new() { Rule = "r", Severity = "error", Message = "m" }
        }, 1);

        Assert.Contains("FAILED", html);
        Assert.Contains("mycheck", html);
        Assert.Contains(">3<", html); // passed count
        Assert.Contains(">2<", html); // failed count
        Assert.Contains(">1<", html); // suppressed count
    }

    // ── Severity values contract ────────────────────────────────

    [Fact]
    public void Severity_ProfileRejectsInvalidValues()
    {
        // Contract: severity must be error/warning/info — enforced at load time
        var yaml = @"
version: 1
checks:
  default:
    failOn: error
    requiredParameters:
      - category: doors
        parameter: Mark
        severity: critical
";
        var path = Path.Combine(Path.GetTempPath(), $"contract_{Guid.NewGuid():N}.yml");
        File.WriteAllText(path, yaml);
        Assert.Throws<InvalidOperationException>(() => RevitCli.Profile.ProfileLoader.Load(path));
        File.Delete(path);
    }

    // ── Stored result contract ──────────────────────────────────

    [Fact]
    public void StoredResult_JsonIsStable()
    {
        var issues = new List<AuditIssue>
        {
            new() { Rule = "naming", Severity = "info", Message = "Test", ElementId = 1 }
        };

        // Save and reload
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"revitcli_contract_{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(dir);

        CheckResultStore.Save("test", 1, 0, 0, issues, dir);
        var json = System.IO.File.ReadAllText(
            System.IO.Path.Combine(dir, ".revitcli", "results", "test-latest.json"));
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("test", root.GetProperty("check").GetString());
        Assert.Equal(1, root.GetProperty("passed").GetInt32());
        Assert.Equal(0, root.GetProperty("failed").GetInt32());
        Assert.Equal(0, root.GetProperty("suppressed").GetInt32());
        Assert.True(root.TryGetProperty("timestamp", out _));
        Assert.True(root.TryGetProperty("issues", out _));

        System.IO.Directory.Delete(dir, true);
    }

    // ── Query JSON contract ─────────────────────────────────────

    [Fact]
    public void QueryJson_ElementHasRequiredFields()
    {
        var elements = new[]
        {
            new ElementInfo
            {
                Id = 12345,
                Name = "Wall 1",
                Category = "Walls",
                TypeName = "Generic - 200mm",
                Parameters = new Dictionary<string, string> { ["Mark"] = "W-01" }
            }
        };

        var json = OutputFormatter.FormatElements(elements, "json");
        var doc = JsonDocument.Parse(json);
        var elem = doc.RootElement.EnumerateArray().First();

        Assert.Equal(12345, elem.GetProperty("id").GetInt64());
        Assert.Equal("Wall 1", elem.GetProperty("name").GetString());
        Assert.Equal("Walls", elem.GetProperty("category").GetString());
        Assert.Equal("Generic - 200mm", elem.GetProperty("typeName").GetString());
        Assert.Equal("W-01", elem.GetProperty("parameters").GetProperty("Mark").GetString());
    }
}
