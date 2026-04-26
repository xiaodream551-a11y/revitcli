using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using RevitCli.Checks;
using RevitCli.Client;
using RevitCli.Shared;
using RevitCli.Tests.Client;
using Xunit;

namespace RevitCli.Tests.Checks;

public class CheckRunnerTests
{
    private static RevitClient ClientFor(AuditResult auditResult)
    {
        var response = ApiResponse<AuditResult>.Ok(auditResult);
        var handler = new FakeHttpHandler(JsonSerializer.Serialize(response));
        return new RevitClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:17839") });
    }

    private static string WriteProfile(string yaml)
    {
        var path = Path.Combine(Path.GetTempPath(), $"revitcli_check_runner_{Guid.NewGuid():N}.yml");
        File.WriteAllText(path, yaml);
        return path;
    }

    [Fact]
    public async Task RunAsync_AppliesSuppressionsBeforeReturningIssues()
    {
        var profilePath = WriteProfile("""
version: 1
checks:
  default:
    failOn: error
    auditRules:
      - rule: room-bounds
    suppressions:
      - rule: room-bounds
        elementIds: [100]
        reason: accepted
""");
        try
        {
            var client = ClientFor(new AuditResult
            {
                Passed = 0,
                Failed = 2,
                Issues = new List<AuditIssue>
                {
                    new() { Rule = "room-bounds", Severity = "error", Message = "A", ElementId = 100 },
                    new() { Rule = "room-bounds", Severity = "error", Message = "B", ElementId = 200 }
                }
            });

            var result = await CheckRunner.RunAsync(client, "default", profilePath);

            Assert.True(result.Success);
            Assert.Equal(1, result.Data!.SuppressedCount);
            var issue = Assert.Single(result.Data.Issues);
            Assert.Equal(200, issue.ElementId);
        }
        finally
        {
            File.Delete(profilePath);
        }
    }

    [Fact]
    public async Task RunAsync_UnknownCheckSet_ReturnsFailureWithAvailableNames()
    {
        var profilePath = WriteProfile("""
version: 1
checks:
  model-health:
    failOn: error
""");
        try
        {
            var result = await CheckRunner.RunAsync(ClientFor(new AuditResult()), "missing", profilePath);

            Assert.False(result.Success);
            Assert.Contains("model-health", result.Error);
        }
        finally
        {
            File.Delete(profilePath);
        }
    }
}
