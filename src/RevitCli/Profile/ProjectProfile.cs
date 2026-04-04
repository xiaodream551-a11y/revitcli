using System.Collections.Generic;
using YamlDotNet.Serialization;

namespace RevitCli.Profile;

public class ProjectProfile
{
    [YamlMember(Alias = "version")]
    public int Version { get; set; } = 1;

    [YamlMember(Alias = "extends")]
    public string? Extends { get; set; }

    [YamlMember(Alias = "defaults")]
    public ProfileDefaults Defaults { get; set; } = new();

    [YamlMember(Alias = "checks")]
    public Dictionary<string, CheckDefinition> Checks { get; set; } = new();

    [YamlMember(Alias = "exports")]
    public Dictionary<string, ExportPreset> Exports { get; set; } = new();

    [YamlMember(Alias = "publish")]
    public Dictionary<string, PublishPipeline> Publish { get; set; } = new();
}

public class ProfileDefaults
{
    [YamlMember(Alias = "outputDir")]
    public string? OutputDir { get; set; }
}

public class CheckDefinition
{
    [YamlMember(Alias = "failOn")]
    public string FailOn { get; set; } = "error";

    [YamlMember(Alias = "auditRules")]
    public List<AuditRuleRef> AuditRules { get; set; } = new();

    [YamlMember(Alias = "requiredParameters")]
    public List<RequiredParameterCheck> RequiredParameters { get; set; } = new();

    [YamlMember(Alias = "naming")]
    public List<NamingCheck> Naming { get; set; } = new();

    [YamlMember(Alias = "suppressions")]
    public List<Suppression> Suppressions { get; set; } = new();
}

public class Suppression
{
    [YamlMember(Alias = "rule")]
    public string Rule { get; set; } = "";

    [YamlMember(Alias = "category")]
    public string? Category { get; set; }

    [YamlMember(Alias = "parameter")]
    public string? Parameter { get; set; }

    [YamlMember(Alias = "elementIds")]
    public List<long>? ElementIds { get; set; }

    [YamlMember(Alias = "reason")]
    public string? Reason { get; set; }

    [YamlMember(Alias = "expires")]
    public string? Expires { get; set; }
}

public class AuditRuleRef
{
    [YamlMember(Alias = "rule")]
    public string Rule { get; set; } = "";
}

public class RequiredParameterCheck
{
    [YamlMember(Alias = "category")]
    public string Category { get; set; } = "";

    [YamlMember(Alias = "parameter")]
    public string Parameter { get; set; } = "";

    [YamlMember(Alias = "requireNonEmpty")]
    public bool RequireNonEmpty { get; set; } = true;

    [YamlMember(Alias = "severity")]
    public string Severity { get; set; } = "error";
}

public class NamingCheck
{
    [YamlMember(Alias = "target")]
    public string Target { get; set; } = "";

    [YamlMember(Alias = "pattern")]
    public string Pattern { get; set; } = "";

    [YamlMember(Alias = "severity")]
    public string Severity { get; set; } = "warning";
}

public class ExportPreset
{
    [YamlMember(Alias = "format")]
    public string Format { get; set; } = "";

    [YamlMember(Alias = "sheets")]
    public List<string>? Sheets { get; set; }

    [YamlMember(Alias = "views")]
    public List<string>? Views { get; set; }

    [YamlMember(Alias = "outputDir")]
    public string? OutputDir { get; set; }
}

public class PublishPipeline
{
    [YamlMember(Alias = "presets")]
    public List<string> Presets { get; set; } = new();

    [YamlMember(Alias = "precheck")]
    public string? Precheck { get; set; }
}
