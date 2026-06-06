using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace RevitCli.Shared;

public static class BimOpsFindingSchema
{
    public const string Version = "bimops-finding.v1";

    public const string SeverityInfo = "info";
    public const string SeverityWarning = "warning";
    public const string SeverityError = "error";
    public const string SeverityBlocker = "blocker";

    public const string FixabilityNone = "none";
    public const string FixabilityManual = "manual";
    public const string FixabilityPlan = "plan";
    public const string FixabilityApply = "apply";
    public const string FixabilityIrreversible = "irreversible";
}

public class BimOpsFindingEnvelope
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; set; } = BimOpsFindingSchema.Version;

    [JsonPropertyName("source")]
    public string Source { get; set; } = "";

    [JsonPropertyName("generatedAtUtc")]
    public string GeneratedAtUtc { get; set; } = "";

    [JsonPropertyName("requiresRevit")]
    public bool RequiresRevit { get; set; }

    [JsonPropertyName("policyPack")]
    public BimOpsPolicyPackReference? PolicyPack { get; set; }

    [JsonPropertyName("summary")]
    public BimOpsFindingSummary Summary { get; set; } = new();

    [JsonPropertyName("findings")]
    public List<BimOpsFinding> Findings { get; set; } = new();
}

public class BimOpsFindingSummary
{
    [JsonPropertyName("info")]
    public int Info { get; set; }

    [JsonPropertyName("warnings")]
    public int Warnings { get; set; }

    [JsonPropertyName("errors")]
    public int Errors { get; set; }

    [JsonPropertyName("blockers")]
    public int Blockers { get; set; }
}

public class BimOpsPolicyPackReference
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("hash")]
    public string? Hash { get; set; }
}

public class BimOpsFinding
{
    [JsonPropertyName("schemaVersion")]
    public string SchemaVersion { get; set; } = BimOpsFindingSchema.Version;

    [JsonPropertyName("findingId")]
    public string FindingId { get; set; } = "";

    [JsonPropertyName("ruleId")]
    public string RuleId { get; set; } = "";

    [JsonPropertyName("source")]
    public string Source { get; set; } = "";

    [JsonPropertyName("severity")]
    public string Severity { get; set; } = BimOpsFindingSchema.SeverityWarning;

    [JsonPropertyName("category")]
    public string Category { get; set; } = "";

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("confidence")]
    public double? Confidence { get; set; }

    [JsonPropertyName("requiresRevit")]
    public bool RequiresRevit { get; set; }

    [JsonPropertyName("fixability")]
    public string Fixability { get; set; } = BimOpsFindingSchema.FixabilityNone;

    [JsonPropertyName("target")]
    public BimOpsFindingTarget Target { get; set; } = new();

    [JsonPropertyName("evidence")]
    public List<BimOpsFindingEvidence> Evidence { get; set; } = new();

    [JsonPropertyName("remediation")]
    public BimOpsFindingRemediation? Remediation { get; set; }
}

public class BimOpsFindingTarget
{
    [JsonPropertyName("modelPath")]
    public string? ModelPath { get; set; }

    [JsonPropertyName("documentName")]
    public string? DocumentName { get; set; }

    [JsonPropertyName("elementIds")]
    public List<long> ElementIds { get; set; } = new();

    [JsonPropertyName("category")]
    public string? Category { get; set; }

    [JsonPropertyName("parameter")]
    public string? Parameter { get; set; }

    [JsonPropertyName("artifactPath")]
    public string? ArtifactPath { get; set; }
}

public class BimOpsFindingEvidence
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    [JsonPropertyName("source")]
    public string Source { get; set; } = "";

    [JsonPropertyName("locator")]
    public string? Locator { get; set; }

    [JsonPropertyName("value")]
    public string? Value { get; set; }
}

public class BimOpsFindingRemediation
{
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = BimOpsFindingSchema.FixabilityManual;

    [JsonPropertyName("planCommand")]
    public string? PlanCommand { get; set; }

    [JsonPropertyName("applyCommand")]
    public string? ApplyCommand { get; set; }

    [JsonPropertyName("verifyCommand")]
    public string? VerifyCommand { get; set; }

    [JsonPropertyName("rollbackCommand")]
    public string? RollbackCommand { get; set; }

    [JsonPropertyName("nextAction")]
    public string? NextAction { get; set; }

    [JsonPropertyName("irreversible")]
    public bool Irreversible { get; set; }
}
