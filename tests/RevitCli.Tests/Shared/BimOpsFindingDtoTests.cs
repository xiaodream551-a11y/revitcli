using System.Text.Json;
using RevitCli.Shared;

namespace RevitCli.Tests.Shared;

public class BimOpsFindingDtoTests
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public void FindingEnvelope_Roundtrips_PolicyAndEvidence()
    {
        var envelope = new BimOpsFindingEnvelope
        {
            Source = "issue.preflight",
            GeneratedAtUtc = "2026-05-31T00:00:00Z",
            RequiresRevit = true,
            PolicyPack = new BimOpsPolicyPackReference
            {
                Name = "office",
                Version = "7.0.0",
                Path = ".revitcli/policies/office.yml",
                Hash = "sha256:abc"
            },
            Summary = new BimOpsFindingSummary
            {
                Warnings = 1
            },
            Findings =
            {
                new BimOpsFinding
                {
                    FindingId = "F-LINK-001",
                    RuleId = "links.missing",
                    Source = "links.audit",
                    Severity = BimOpsFindingSchema.SeverityWarning,
                    Category = "links",
                    Message = "Linked model path is missing.",
                    Confidence = 0.95,
                    RequiresRevit = true,
                    Fixability = BimOpsFindingSchema.FixabilityPlan,
                    Target = new BimOpsFindingTarget
                    {
                        ModelPath = "D:\\Projects\\model.rvt",
                        DocumentName = "model.rvt",
                        Category = "RevitLinkType",
                        ElementIds = { 123 }
                    },
                    Evidence =
                    {
                        new BimOpsFindingEvidence
                        {
                            Kind = "api",
                            Source = "RevitLinkType",
                            Locator = "elementId:123",
                            Value = "missing"
                        }
                    },
                    Remediation = new BimOpsFindingRemediation
                    {
                        Mode = BimOpsFindingSchema.FixabilityPlan,
                        PlanCommand = "revitcli links repair --dry-run --output json",
                        VerifyCommand = "revitcli links audit --output json",
                        RollbackCommand = "revitcli rollback <receipt> --dry-run",
                        NextAction = "Review the link path map before applying."
                    }
                }
            }
        };

        var json = JsonSerializer.Serialize(envelope);

        Assert.Contains("\"schemaVersion\":\"bimops-finding.v1\"", json);
        Assert.Contains("\"policyPack\"", json);
        Assert.Contains("\"findingId\":\"F-LINK-001\"", json);
        Assert.DoesNotContain("\"FindingId\"", json);

        var restored = JsonSerializer.Deserialize<BimOpsFindingEnvelope>(json, JsonOpts)!;

        Assert.Equal(BimOpsFindingSchema.Version, restored.SchemaVersion);
        Assert.Equal("issue.preflight", restored.Source);
        Assert.True(restored.RequiresRevit);
        Assert.Equal("office", restored.PolicyPack!.Name);
        Assert.Equal(1, restored.Summary.Warnings);
        var finding = Assert.Single(restored.Findings);
        Assert.Equal("links.missing", finding.RuleId);
        Assert.Equal(BimOpsFindingSchema.FixabilityPlan, finding.Fixability);
        Assert.Equal(123, Assert.Single(finding.Target.ElementIds));
        Assert.Equal("missing", Assert.Single(finding.Evidence).Value);
        Assert.Equal("revitcli links audit --output json", finding.Remediation!.VerifyCommand);
        Assert.Equal("Review the link path map before applying.", finding.Remediation.NextAction);
    }

    [Fact]
    public void Finding_Defaults_AreStableForNewAdapters()
    {
        var finding = new BimOpsFinding();

        Assert.Equal(BimOpsFindingSchema.Version, finding.SchemaVersion);
        Assert.Equal(BimOpsFindingSchema.SeverityWarning, finding.Severity);
        Assert.Equal(BimOpsFindingSchema.FixabilityNone, finding.Fixability);
        Assert.False(finding.RequiresRevit);
        Assert.Empty(finding.Target.ElementIds);
        Assert.Empty(finding.Evidence);
        Assert.Null(finding.Remediation);
    }
}
