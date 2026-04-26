using System.Text.Json;
using RevitCli.Shared;
using Xunit;

namespace RevitCli.Tests.Shared;

public class AuditIssueMetadataTests
{
    [Fact]
    public void AuditIssue_SerializesStructuredMetadata_WhenPresent()
    {
        var issue = new AuditIssue
        {
            Rule = "required-parameter",
            Severity = "warning",
            Message = "Door Mark is missing",
            ElementId = 123,
            Category = "doors",
            Parameter = "Mark",
            Target = "doors",
            CurrentValue = "",
            ExpectedValue = "D-123",
            Source = "structured"
        };

        var json = JsonSerializer.Serialize(issue);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("doors", root.GetProperty("category").GetString());
        Assert.Equal("Mark", root.GetProperty("parameter").GetString());
        Assert.Equal("doors", root.GetProperty("target").GetString());
        Assert.Equal("", root.GetProperty("currentValue").GetString());
        Assert.Equal("D-123", root.GetProperty("expectedValue").GetString());
        Assert.Equal("structured", root.GetProperty("source").GetString());
    }

    [Fact]
    public void AuditIssue_ExistingFieldsStillRoundTrip_WhenMetadataMissing()
    {
        var json = """
        {
          "rule": "naming",
          "severity": "warning",
          "message": "Bad name",
          "elementId": 42
        }
        """;

        var issue = JsonSerializer.Deserialize<AuditIssue>(json)!;

        Assert.Equal("naming", issue.Rule);
        Assert.Equal("warning", issue.Severity);
        Assert.Equal("Bad name", issue.Message);
        Assert.Equal(42, issue.ElementId);
        Assert.Null(issue.Category);
        Assert.Null(issue.Parameter);
        Assert.Null(issue.Target);
        Assert.Null(issue.CurrentValue);
        Assert.Null(issue.ExpectedValue);
        Assert.Null(issue.Source);
    }
}
