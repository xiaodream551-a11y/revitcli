using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using RevitCli.Client;
using RevitCli.Commands;
using RevitCli.Shared;
using RevitCli.Tests.Client;
using Xunit;

namespace RevitCli.Tests.Commands;

public class AuditCommandTests
{
    [Fact]
    public async Task Execute_AllRulesPass_PrintsSummary()
    {
        var auditResult = new AuditResult { Passed = 5, Failed = 0, Issues = new List<AuditIssue>() };
        var response = ApiResponse<AuditResult>.Ok(auditResult);
        var handler = new FakeHttpHandler(JsonSerializer.Serialize(response));
        var client = new RevitClient(new HttpClient(handler) { BaseAddress = new System.Uri("http://localhost:17839") });
        var writer = new StringWriter();

        var exitCode = await AuditCommand.ExecuteAsync(client, null, writer);

        var output = writer.ToString();
        Assert.Contains("5 passed", output);
        Assert.Contains("0 failed", output);
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task Execute_WithIssues_PrintsDetails()
    {
        var auditResult = new AuditResult
        {
            Passed = 3,
            Failed = 2,
            Issues = new List<AuditIssue>
            {
                new() { Rule = "naming", Severity = "error", Message = "Wall has no mark", ElementId = 100 },
                new() { Rule = "clash", Severity = "warning", Message = "Overlap detected", ElementId = 200 }
            }
        };
        var response = ApiResponse<AuditResult>.Ok(auditResult);
        var handler = new FakeHttpHandler(JsonSerializer.Serialize(response));
        var client = new RevitClient(new HttpClient(handler) { BaseAddress = new System.Uri("http://localhost:17839") });
        var writer = new StringWriter();

        var exitCode = await AuditCommand.ExecuteAsync(client, "naming,clash", writer);

        var output = writer.ToString();
        Assert.Contains("3 passed", output);
        Assert.Contains("2 failed", output);
        Assert.Contains("[ERROR]", output);
        Assert.Contains("[WARN]", output);
        Assert.Contains("Element 100", output);
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task Execute_InvalidRule_PrintsError()
    {
        var handler = new FakeHttpHandler("{}");
        var client = new RevitClient(new HttpClient(handler) { BaseAddress = new System.Uri("http://localhost:17839") });
        var writer = new StringWriter();

        var exitCode = await AuditCommand.ExecuteAsync(client, "nonexistent", writer);

        Assert.Contains("unknown rule", writer.ToString().ToLower());
        Assert.Equal(1, exitCode);
    }
}
