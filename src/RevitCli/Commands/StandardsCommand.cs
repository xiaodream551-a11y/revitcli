using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using RevitCli.Shared;
using RevitCli.Standards;

namespace RevitCli.Commands;

public static class StandardsCommand
{
    private const string PolicyInitSchemaVersion = "standards-policy-init.v1";
    private const string PolicyDiffSchemaVersion = "standards-policy-diff.v1";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static Command Create()
    {
        var command = new Command("standards", "Install and validate local office standards requirements");
        command.AddCommand(CreateValidateCommand());
        command.AddCommand(CreateInstallCommand());
        command.AddCommand(CreatePolicyCommand());
        return command;
    }

    private static Command CreatePolicyCommand()
    {
        var command = new Command("policy", "Initialize, validate, and diff versioned local policy packs");
        command.AddCommand(CreatePolicyInitCommand());
        command.AddCommand(CreatePolicyValidateCommand());
        command.AddCommand(CreatePolicyDiffCommand());
        return command;
    }

    private static Command CreatePolicyInitCommand()
    {
        var dirOpt = new Option<string?>("--dir", "Project directory; defaults to current directory");
        var nameOpt = new Option<string>("--name", () => "office", "Policy pack name");
        var packVersionOpt = new Option<string>("--pack-version", () => "0.1.0", "Policy pack version");
        var dryRunOpt = new Option<bool>("--dry-run", "Preview the manifest write without creating files");
        var forceOpt = new Option<bool>("--force", "Overwrite an existing policy manifest");
        var outputOpt = new Option<string>("--output", () => "table", "Output format: table | json | markdown");

        var command = new Command("init", "Create a starter local policy pack manifest")
        {
            dirOpt,
            nameOpt,
            packVersionOpt,
            dryRunOpt,
            forceOpt,
            outputOpt,
        };

        command.SetHandler(async (
            string? dir,
            string name,
            string packVersion,
            bool dryRun,
            bool force,
            string outputFormat) =>
        {
            Environment.ExitCode = await ExecutePolicyInitAsync(
                dir,
                name,
                packVersion,
                dryRun,
                force,
                outputFormat,
                Console.Out);
        }, dirOpt, nameOpt, packVersionOpt, dryRunOpt, forceOpt, outputOpt);

        return command;
    }

    private static Command CreatePolicyValidateCommand()
    {
        var manifestOpt = new Option<string?>(
            "--manifest",
            $"Policy manifest path (default: {StandardsValidator.DefaultManifestPath})");
        var dirOpt = new Option<string?>("--dir", "Project directory; defaults to current directory");
        var outputOpt = new Option<string>("--output", () => "table", "Output format: table | json | markdown");
        var findingsOpt = new Option<bool>("--findings", "Emit a bimops-finding.v1 JSON envelope");

        var command = new Command("validate", "Validate a local policy pack manifest")
        {
            manifestOpt,
            dirOpt,
            outputOpt,
            findingsOpt,
        };

        command.SetHandler(async (string? manifest, string? dir, string outputFormat, bool findings) =>
        {
            Environment.ExitCode = await ExecutePolicyValidateAsync(manifest, dir, outputFormat, findings, Console.Out);
        }, manifestOpt, dirOpt, outputOpt, findingsOpt);

        return command;
    }

    private static Command CreatePolicyDiffCommand()
    {
        var fromArg = new Argument<string>("from", "Older policy manifest path");
        var toArg = new Argument<string>("to", "Newer policy manifest path");
        var outputOpt = new Option<string>("--output", () => "table", "Output format: table | json | markdown");

        var command = new Command("diff", "Compare two local policy pack manifests")
        {
            fromArg,
            toArg,
            outputOpt,
        };

        command.SetHandler(async (string from, string to, string outputFormat) =>
        {
            Environment.ExitCode = await ExecutePolicyDiffAsync(from, to, outputFormat, Console.Out);
        }, fromArg, toArg, outputOpt);

        return command;
    }

    private static Command CreateInstallCommand()
    {
        var sourceArg = new Argument<string>(
            "path-or-git-url",
            "Local standards pack path or git URL");
        var dirOpt = new Option<string?>("--dir", "Project directory to install into; defaults to current directory");
        var refOpt = new Option<string?>("--ref", "Branch, tag, or commit SHA for git sources");
        var subPathOpt = new Option<string?>("--subpath", "Path inside the source pack or git repository");
        var forceOpt = new Option<bool>("--force", "Overwrite existing standards/profile/workflow files");
        var dryRunOpt = new Option<bool>("--dry-run", "Show files and directories that would be installed");
        var outputOpt = new Option<string>("--output", () => "table", "Output format: table | json | markdown");

        var command = new Command("install", "Install an approved standards pack into the local project")
        {
            sourceArg,
            dirOpt,
            refOpt,
            subPathOpt,
            forceOpt,
            dryRunOpt,
            outputOpt,
        };

        command.SetHandler(async (
            string source,
            string? dir,
            string? refSpec,
            string? subPath,
            bool force,
            bool dryRun,
            string outputFormat) =>
        {
            Environment.ExitCode = await ExecuteInstallAsync(
                source,
                dir,
                refSpec,
                subPath,
                force,
                dryRun,
                outputFormat,
                Console.Out);
        }, sourceArg, dirOpt, refOpt, subPathOpt, forceOpt, dryRunOpt, outputOpt);

        return command;
    }

    private static Command CreateValidateCommand()
    {
        var manifestOpt = new Option<string?>(
            "--manifest",
            $"Standards manifest path (default: {StandardsValidator.DefaultManifestPath})");
        var dirOpt = new Option<string?>("--dir", "Project directory; defaults to current directory");
        var outputOpt = new Option<string>("--output", () => "table", "Output format: table | json | markdown");
        var findingsOpt = new Option<bool>("--findings", "Emit a bimops-finding.v1 JSON envelope instead of the legacy standards report");

        var command = new Command("validate", "Validate local profile, workflow, output, schedule, and family-rule standards")
        {
            manifestOpt,
            dirOpt,
            outputOpt,
            findingsOpt,
        };

        command.SetHandler(async (string? manifest, string? dir, string outputFormat, bool findings) =>
        {
            Environment.ExitCode = await ExecuteValidateAsync(manifest, dir, outputFormat, Console.Out, findings);
        }, manifestOpt, dirOpt, outputOpt, findingsOpt);

        return command;
    }

    public static async Task<int> ExecuteInstallAsync(
        string source,
        string? projectDirectory,
        string? refSpec,
        string? subPath,
        bool force,
        bool dryRun,
        string outputFormat,
        TextWriter output)
    {
        var projectRoot = string.IsNullOrWhiteSpace(projectDirectory)
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(projectDirectory!);

        StandardsInstallResult result;
        try
        {
            result = await StandardsInstaller.InstallAsync(
                source,
                projectRoot,
                refSpec,
                subPath,
                force,
                dryRun);
        }
        catch (Exception ex) when (ex is ArgumentException
                                       or InvalidOperationException
                                       or IOException
                                       or UnauthorizedAccessException
                                       or LibGit2Sharp.LibGit2SharpException
                                       or YamlDotNet.Core.YamlException)
        {
            await output.WriteLineAsync($"Error: {ex.Message}");
            return 1;
        }

        if (string.Equals(outputFormat, "json", StringComparison.OrdinalIgnoreCase))
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(result, JsonOpts));
        }
        else if (string.Equals(outputFormat, "markdown", StringComparison.OrdinalIgnoreCase))
        {
            await output.WriteLineAsync(RenderInstallMarkdown(result));
        }
        else
        {
            await output.WriteLineAsync(RenderInstallTable(result));
        }

        return result.Validation?.Valid == false ? 1 : 0;
    }

    public static async Task<int> ExecuteValidateAsync(
        string? manifestPath,
        string? projectDirectory,
        string outputFormat,
        TextWriter output,
        bool findings = false)
    {
        if (findings && !string.Equals(outputFormat, "json", StringComparison.OrdinalIgnoreCase))
        {
            await output.WriteLineAsync("Error: --findings currently requires '--output json'.");
            return 1;
        }

        var projectRoot = string.IsNullOrWhiteSpace(projectDirectory)
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(projectDirectory!);
        var resolvedManifest = string.IsNullOrWhiteSpace(manifestPath)
            ? Path.Combine(projectRoot, StandardsValidator.DefaultManifestPath)
            : ResolvePath(projectRoot, manifestPath!);

        var report = StandardsValidator.Validate(resolvedManifest, projectRoot);
        if (findings)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(ToFindingEnvelope(report), JsonOpts));
        }
        else if (string.Equals(outputFormat, "json", StringComparison.OrdinalIgnoreCase))
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(report, JsonOpts));
        }
        else if (string.Equals(outputFormat, "markdown", StringComparison.OrdinalIgnoreCase))
        {
            await output.WriteLineAsync(RenderMarkdown(report));
        }
        else
        {
            await output.WriteLineAsync(RenderTable(report));
        }

        return report.Valid ? 0 : 1;
    }

    public static async Task<int> ExecutePolicyInitAsync(
        string? projectDirectory,
        string name,
        string packVersion,
        bool dryRun,
        bool force,
        string outputFormat,
        TextWriter output)
    {
        if (!TryNormalizeStandardsOutput(outputFormat, out var normalizedOutput))
        {
            await output.WriteLineAsync("Error: --output must be 'table', 'json', or 'markdown'.");
            return 1;
        }

        var projectRoot = string.IsNullOrWhiteSpace(projectDirectory)
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(projectDirectory!);
        var manifestPath = Path.Combine(projectRoot, StandardsValidator.DefaultManifestPath);
        var exists = File.Exists(manifestPath);
        var requiresForce = exists && !force;
        var success = !requiresForce || dryRun;
        var result = new StandardsPolicyInitResult(
            PolicyInitSchemaVersion,
            DateTimeOffset.UtcNow,
            success,
            projectRoot,
            manifestPath,
            dryRun,
            exists,
            requiresForce,
            !dryRun && success,
            name,
            packVersion,
            success ? null : $"policy manifest already exists: {manifestPath}. Use --force to overwrite.",
            success
                ? new[]
                {
                    $"Review {StandardsValidator.DefaultManifestPath}.",
                    "Run revitcli standards policy validate --output markdown.",
                    "Commit the policy pack with the profiles and workflows it references.",
                }
                : new[] { "Rerun with --force after reviewing the existing manifest." });

        if (success && !dryRun)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
            await File.WriteAllTextAsync(manifestPath, BuildPolicyManifestYaml(name, packVersion));
        }

        await WritePolicyInitResultAsync(output, normalizedOutput, result);
        return success ? 0 : 1;
    }

    public static Task<int> ExecutePolicyValidateAsync(
        string? manifestPath,
        string? projectDirectory,
        string outputFormat,
        bool findings,
        TextWriter output)
    {
        return ExecuteValidateAsync(manifestPath, projectDirectory, outputFormat, output, findings);
    }

    public static async Task<int> ExecutePolicyDiffAsync(
        string fromPath,
        string toPath,
        string outputFormat,
        TextWriter output)
    {
        if (!TryNormalizeStandardsOutput(outputFormat, out var normalizedOutput))
        {
            await output.WriteLineAsync("Error: --output must be 'table', 'json', or 'markdown'.");
            return 1;
        }

        var fromFullPath = Path.GetFullPath(fromPath);
        var toFullPath = Path.GetFullPath(toPath);
        StandardsPolicyDiffReport report;
        try
        {
            var from = StandardsValidator.LoadManifest(fromFullPath);
            var to = StandardsValidator.LoadManifest(toFullPath);
            var changes = ComparePolicyManifests(from, to);
            report = new StandardsPolicyDiffReport(
                PolicyDiffSchemaVersion,
                DateTimeOffset.UtcNow,
                true,
                fromFullPath,
                toFullPath,
                changes.Count,
                changes,
                null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or YamlDotNet.Core.YamlException)
        {
            report = new StandardsPolicyDiffReport(
                PolicyDiffSchemaVersion,
                DateTimeOffset.UtcNow,
                false,
                fromFullPath,
                toFullPath,
                0,
                Array.Empty<StandardsPolicyDiffChange>(),
                ex.Message);
            await WritePolicyDiffReportAsync(output, normalizedOutput, report);
            return 1;
        }

        await WritePolicyDiffReportAsync(output, normalizedOutput, report);
        return 0;
    }

    private static bool TryNormalizeStandardsOutput(string? outputFormat, out string normalized)
    {
        normalized = string.IsNullOrWhiteSpace(outputFormat) ? "table" : outputFormat.Trim().ToLowerInvariant();
        return normalized is "table" or "json" or "markdown";
    }

    private static string BuildPolicyManifestYaml(string name, string packVersion)
    {
        var sb = new StringBuilder();
        sb.AppendLine("version: 1");
        sb.AppendLine($"name: {YamlDoubleQuoted(name)}");
        sb.AppendLine("description: \"Local BIMOps policy pack. Fill required sections before rollout.\"");
        sb.AppendLine($"packVersion: {YamlDoubleQuoted(packVersion)}");
        sb.AppendLine("compatibility:");
        sb.AppendLine("  revitCli: \">=0.1.0\"");
        sb.AppendLine("  revitYears: [2024, 2025, 2026]");
        sb.AppendLine("  notes:");
        sb.AppendLine("    - \"Local-first policy pack; validate before pilot rollout.\"");
        sb.AppendLine("required:");
        sb.AppendLine("  profiles: []");
        sb.AppendLine("  workflows: []");
        sb.AppendLine("  outputPaths: []");
        sb.AppendLine("  scheduleTemplates: []");
        sb.AppendLine("  sheetMaps: []");
        sb.AppendLine("  numberingRules: []");
        sb.AppendLine("  familyRules: []");
        return sb.ToString();
    }

    private static string YamlDoubleQuoted(string value) =>
        "\"" + (value ?? string.Empty)
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal) + "\"";

    private static IReadOnlyList<StandardsPolicyDiffChange> ComparePolicyManifests(
        StandardsManifest from,
        StandardsManifest to)
    {
        var changes = new List<StandardsPolicyDiffChange>();
        AddScalarChange(changes, "version", from.Version.ToString(CultureInfo.InvariantCulture), to.Version.ToString(CultureInfo.InvariantCulture));
        AddScalarChange(changes, "name", from.Name, to.Name);
        AddScalarChange(changes, "description", from.Description, to.Description);
        AddScalarChange(changes, "packVersion", from.PackVersion, to.PackVersion);
        AddScalarChange(changes, "compatibility.revitCli", from.Compatibility?.RevitCli, to.Compatibility?.RevitCli);
        AddStringListChanges(
            changes,
            "compatibility.revitYears",
            (from.Compatibility?.RevitYears ?? Enumerable.Empty<int>()).Select(year => year.ToString(CultureInfo.InvariantCulture)),
            (to.Compatibility?.RevitYears ?? Enumerable.Empty<int>()).Select(year => year.ToString(CultureInfo.InvariantCulture)));
        AddStringListChanges(changes, "compatibility.notes", from.Compatibility?.Notes ?? Enumerable.Empty<string>(), to.Compatibility?.Notes ?? Enumerable.Empty<string>());
        AddStringListChanges(changes, "required.profiles", from.Required.Profiles, to.Required.Profiles);
        AddStringListChanges(changes, "required.workflows", from.Required.Workflows, to.Required.Workflows);
        AddStringListChanges(changes, "required.outputPaths", from.Required.OutputPaths, to.Required.OutputPaths);
        AddStringListChanges(changes, "required.scheduleTemplates", from.Required.ScheduleTemplates, to.Required.ScheduleTemplates);
        AddStringListChanges(changes, "required.sheetMaps", from.Required.SheetMaps, to.Required.SheetMaps);
        AddStringListChanges(changes, "required.numberingRules", from.Required.NumberingRules, to.Required.NumberingRules);
        AddStringListChanges(changes, "required.familyRules", from.Required.FamilyRules, to.Required.FamilyRules);
        return changes
            .OrderBy(change => change.Path, StringComparer.Ordinal)
            .ThenBy(change => change.ChangeType, StringComparer.Ordinal)
            .ThenBy(change => change.To ?? change.From, StringComparer.Ordinal)
            .ToList();
    }

    private static void AddScalarChange(
        List<StandardsPolicyDiffChange> changes,
        string path,
        string? from,
        string? to)
    {
        var normalizedFrom = from ?? "";
        var normalizedTo = to ?? "";
        if (string.Equals(normalizedFrom, normalizedTo, StringComparison.Ordinal))
            return;

        changes.Add(new StandardsPolicyDiffChange(path, "changed", normalizedFrom, normalizedTo));
    }

    private static void AddStringListChanges(
        List<StandardsPolicyDiffChange> changes,
        string path,
        IEnumerable<string> from,
        IEnumerable<string> to)
    {
        var fromSet = new HashSet<string>(from.Where(value => !string.IsNullOrWhiteSpace(value)), StringComparer.OrdinalIgnoreCase);
        var toSet = new HashSet<string>(to.Where(value => !string.IsNullOrWhiteSpace(value)), StringComparer.OrdinalIgnoreCase);
        foreach (var removed in fromSet.Except(toSet).OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            changes.Add(new StandardsPolicyDiffChange(path + "[]", "removed", removed, ""));
        foreach (var added in toSet.Except(fromSet).OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            changes.Add(new StandardsPolicyDiffChange(path + "[]", "added", "", added));
    }

    private static BimOpsFindingEnvelope ToFindingEnvelope(StandardsValidationReport report)
    {
        var envelope = new BimOpsFindingEnvelope
        {
            Source = "standards.validate",
            GeneratedAtUtc = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            RequiresRevit = false,
            PolicyPack = string.IsNullOrWhiteSpace(report.Name) && string.IsNullOrWhiteSpace(report.PackVersion)
                ? null
                : new BimOpsPolicyPackReference
                {
                    Name = report.Name,
                    Version = report.PackVersion ?? "",
                    Path = report.ManifestPath,
                },
            Summary = new BimOpsFindingSummary
            {
                Info = report.Issues.Count(issue => issue.Severity == StandardsValidationSeverity.Info),
                Warnings = report.Issues.Count(issue => issue.Severity == StandardsValidationSeverity.Warning),
                Errors = report.Issues.Count(issue => issue.Severity == StandardsValidationSeverity.Error),
            },
        };

        var index = 1;
        foreach (var issue in report.Issues)
        {
            envelope.Findings.Add(new BimOpsFinding
            {
                FindingId = $"STD-{index++:D3}-{NormalizeFindingToken(issue.Path)}",
                RuleId = $"standards.{NormalizeRuleId(issue.Path)}",
                Source = "standards.validate",
                Severity = ToFindingSeverity(issue.Severity),
                Category = "standards",
                Message = issue.Message,
                RequiresRevit = false,
                Fixability = BimOpsFindingSchema.FixabilityManual,
                Target = new BimOpsFindingTarget
                {
                    ArtifactPath = report.ManifestPath,
                    Category = "policy-pack",
                    Parameter = issue.Path,
                },
                Evidence =
                {
                    new BimOpsFindingEvidence
                    {
                        Kind = "standards-validation",
                        Source = report.ManifestPath,
                        Locator = issue.Path,
                        Value = issue.Severity.ToString(),
                    },
                },
                Remediation = new BimOpsFindingRemediation
                {
                    Mode = BimOpsFindingSchema.FixabilityManual,
                    VerifyCommand = $"revitcli standards validate --manifest {QuoteArgument(report.ManifestPath)} --dir {QuoteArgument(report.ProjectDirectory)} --findings --output json",
                    NextAction = "Update the standards pack or required project artifact, then rerun standards validate.",
                },
            });
        }

        return envelope;
    }

    private static string ToFindingSeverity(StandardsValidationSeverity severity) =>
        severity switch
        {
            StandardsValidationSeverity.Info => BimOpsFindingSchema.SeverityInfo,
            StandardsValidationSeverity.Warning => BimOpsFindingSchema.SeverityWarning,
            StandardsValidationSeverity.Error => BimOpsFindingSchema.SeverityError,
            _ => BimOpsFindingSchema.SeverityWarning,
        };

    private static string NormalizeRuleId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "unknown";

        var chars = value.Trim()
            .Select(ch => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '.')
            .ToArray();
        var ruleId = new string(chars).Trim('.');
        while (ruleId.Contains("..", StringComparison.Ordinal))
            ruleId = ruleId.Replace("..", ".", StringComparison.Ordinal);

        return string.IsNullOrWhiteSpace(ruleId) ? "unknown" : ruleId;
    }

    private static string NormalizeFindingToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "unknown";

        var chars = value.Trim()
            .Select(ch => char.IsLetterOrDigit(ch) ? char.ToUpperInvariant(ch) : '-')
            .ToArray();
        var token = new string(chars).Trim('-');
        while (token.Contains("--", StringComparison.Ordinal))
            token = token.Replace("--", "-", StringComparison.Ordinal);

        return string.IsNullOrWhiteSpace(token) ? "unknown" : token;
    }

    private sealed record StandardsPolicyInitResult(
        [property: JsonPropertyName("schemaVersion")]
        string SchemaVersion,
        [property: JsonPropertyName("generatedAt")]
        DateTimeOffset GeneratedAt,
        [property: JsonPropertyName("success")]
        bool Success,
        [property: JsonPropertyName("projectDirectory")]
        string ProjectDirectory,
        [property: JsonPropertyName("manifestPath")]
        string ManifestPath,
        [property: JsonPropertyName("dryRun")]
        bool DryRun,
        [property: JsonPropertyName("manifestAlreadyExists")]
        bool ManifestAlreadyExists,
        [property: JsonPropertyName("requiresForce")]
        bool RequiresForce,
        [property: JsonPropertyName("wroteManifest")]
        bool WroteManifest,
        [property: JsonPropertyName("name")]
        string Name,
        [property: JsonPropertyName("packVersion")]
        string PackVersion,
        [property: JsonPropertyName("error")]
        string? Error,
        [property: JsonPropertyName("nextActions")]
        IReadOnlyList<string> NextActions);

    private sealed record StandardsPolicyDiffReport(
        [property: JsonPropertyName("schemaVersion")]
        string SchemaVersion,
        [property: JsonPropertyName("generatedAt")]
        DateTimeOffset GeneratedAt,
        [property: JsonPropertyName("success")]
        bool Success,
        [property: JsonPropertyName("fromPath")]
        string FromPath,
        [property: JsonPropertyName("toPath")]
        string ToPath,
        [property: JsonPropertyName("changeCount")]
        int ChangeCount,
        [property: JsonPropertyName("changes")]
        IReadOnlyList<StandardsPolicyDiffChange> Changes,
        [property: JsonPropertyName("error")]
        string? Error);

    private sealed record StandardsPolicyDiffChange(
        [property: JsonPropertyName("path")]
        string Path,
        [property: JsonPropertyName("changeType")]
        string ChangeType,
        [property: JsonPropertyName("from")]
        string? From,
        [property: JsonPropertyName("to")]
        string? To);

    private static async Task WritePolicyInitResultAsync(
        TextWriter output,
        string outputFormat,
        StandardsPolicyInitResult result)
    {
        switch (outputFormat)
        {
            case "json":
                await output.WriteLineAsync(JsonSerializer.Serialize(result, JsonOpts));
                break;
            case "markdown":
                await output.WriteLineAsync(RenderPolicyInitMarkdown(result));
                break;
            default:
                await output.WriteLineAsync(RenderPolicyInitTable(result));
                break;
        }
    }

    private static async Task WritePolicyDiffReportAsync(
        TextWriter output,
        string outputFormat,
        StandardsPolicyDiffReport report)
    {
        switch (outputFormat)
        {
            case "json":
                await output.WriteLineAsync(JsonSerializer.Serialize(report, JsonOpts));
                break;
            case "markdown":
                await output.WriteLineAsync(RenderPolicyDiffMarkdown(report));
                break;
            default:
                await output.WriteLineAsync(RenderPolicyDiffTable(report));
                break;
        }
    }

    private static string RenderPolicyInitTable(StandardsPolicyInitResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine(result.DryRun ? "Policy pack init plan" : "Policy pack init");
        sb.AppendLine($"Manifest: {result.ManifestPath}");
        sb.AppendLine($"Name: {result.Name}");
        sb.AppendLine($"Pack version: {result.PackVersion}");
        sb.AppendLine($"Status: {(result.Success ? "OK" : "FAIL")}");
        sb.AppendLine($"Dry run: {(result.DryRun ? "yes" : "no")}");
        sb.AppendLine($"Existing manifest: {(result.ManifestAlreadyExists ? "yes" : "no")}");
        if (result.RequiresForce)
            sb.AppendLine("Requires force: yes");
        if (!string.IsNullOrWhiteSpace(result.Error))
            sb.AppendLine($"Error: {result.Error}");
        foreach (var action in result.NextActions)
            sb.AppendLine($"Next: {action}");
        return sb.ToString().TrimEnd();
    }

    private static string RenderPolicyInitMarkdown(StandardsPolicyInitResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine(result.DryRun ? "# Policy Pack Init Plan" : "# Policy Pack Init");
        sb.AppendLine();
        sb.AppendLine($"- Manifest: `{EscapeInlineCode(result.ManifestPath)}`");
        sb.AppendLine($"- Name: `{EscapeInlineCode(result.Name)}`");
        sb.AppendLine($"- Pack version: `{EscapeInlineCode(result.PackVersion)}`");
        sb.AppendLine($"- Status: {(result.Success ? "`OK`" : "`FAIL`")}");
        sb.AppendLine($"- Dry run: `{(result.DryRun ? "yes" : "no")}`");
        sb.AppendLine($"- Existing manifest: `{(result.ManifestAlreadyExists ? "yes" : "no")}`");
        if (result.RequiresForce)
            sb.AppendLine("- Requires force: `yes`");
        if (!string.IsNullOrWhiteSpace(result.Error))
            sb.AppendLine($"- Error: {EscapeMarkdownText(result.Error)}");
        sb.AppendLine();
        sb.AppendLine("## Next Actions");
        foreach (var action in result.NextActions)
            sb.AppendLine($"- {EscapeMarkdownText(action)}");
        return sb.ToString().TrimEnd();
    }

    private static string RenderPolicyDiffTable(StandardsPolicyDiffReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Policy pack diff");
        sb.AppendLine($"From: {report.FromPath}");
        sb.AppendLine($"To: {report.ToPath}");
        sb.AppendLine($"Status: {(report.Success ? "OK" : "FAIL")}");
        if (!string.IsNullOrWhiteSpace(report.Error))
        {
            sb.AppendLine($"Error: {report.Error}");
            return sb.ToString().TrimEnd();
        }

        sb.AppendLine($"Changes: {report.ChangeCount}");
        foreach (var change in report.Changes)
            sb.AppendLine($"  {change.ChangeType.ToUpperInvariant(),-7} {change.Path}: {change.From} -> {change.To}");
        if (report.Changes.Count == 0)
            sb.AppendLine("No changes.");
        return sb.ToString().TrimEnd();
    }

    private static string RenderPolicyDiffMarkdown(StandardsPolicyDiffReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Policy Pack Diff");
        sb.AppendLine();
        sb.AppendLine($"- From: `{EscapeInlineCode(report.FromPath)}`");
        sb.AppendLine($"- To: `{EscapeInlineCode(report.ToPath)}`");
        sb.AppendLine($"- Status: {(report.Success ? "`OK`" : "`FAIL`")}");
        if (!string.IsNullOrWhiteSpace(report.Error))
        {
            sb.AppendLine($"- Error: {EscapeMarkdownText(report.Error)}");
            return sb.ToString().TrimEnd();
        }

        sb.AppendLine($"- Changes: `{report.ChangeCount}`");
        sb.AppendLine();
        sb.AppendLine("| Change | Path | From | To |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var change in report.Changes)
        {
            sb.AppendLine(
                $"| {EscapeMarkdownText(change.ChangeType)} | `{EscapeInlineCode(change.Path)}` | `{EscapeInlineCode(change.From)}` | `{EscapeInlineCode(change.To)}` |");
        }
        if (report.Changes.Count == 0)
            sb.AppendLine("| none | - | - | - |");
        return sb.ToString().TrimEnd();
    }

    private static string RenderTable(StandardsValidationReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Standards validation");
        sb.AppendLine($"Manifest: {report.ManifestPath}");
        sb.AppendLine($"Project: {report.ProjectDirectory}");
        if (!string.IsNullOrWhiteSpace(report.Name))
        {
            sb.AppendLine($"Name: {report.Name}");
        }
        if (!string.IsNullOrWhiteSpace(report.PackVersion))
        {
            sb.AppendLine($"Pack version: {report.PackVersion}");
        }
        if (!string.IsNullOrWhiteSpace(report.Compatibility.RevitCli)
            || report.Compatibility.RevitYears.Count > 0)
        {
            var revitCli = string.IsNullOrWhiteSpace(report.Compatibility.RevitCli)
                ? "(unspecified)"
                : report.Compatibility.RevitCli;
            var years = report.Compatibility.RevitYears.Count == 0
                ? "(unspecified)"
                : string.Join(", ", report.Compatibility.RevitYears);
            sb.AppendLine($"Compatibility: RevitCli {revitCli}; Revit {years}");
        }
        foreach (var note in report.Compatibility.Notes.Where(note => !string.IsNullOrWhiteSpace(note)).Take(3))
        {
            sb.AppendLine($"Compatibility note: {note}");
        }

        sb.AppendLine($"Status: {(report.Valid ? "OK" : "FAIL")}");

        if (report.Issues.Count == 0)
        {
            sb.AppendLine("No issues.");
            return sb.ToString().TrimEnd();
        }

        foreach (var issue in report.Issues
                     .OrderByDescending(issue => issue.Severity)
                     .ThenBy(issue => issue.Path, StringComparer.Ordinal))
        {
            sb.AppendLine($"  {issue.Severity.ToString().ToUpperInvariant()} {issue.Path}: {issue.Message}");
        }

        return sb.ToString().TrimEnd();
    }

    private static string RenderMarkdown(StandardsValidationReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Standards Validation");
        sb.AppendLine();
        sb.AppendLine($"- Manifest: `{EscapeInlineCode(report.ManifestPath)}`");
        sb.AppendLine($"- Project: `{EscapeInlineCode(report.ProjectDirectory)}`");
        if (!string.IsNullOrWhiteSpace(report.Name))
            sb.AppendLine($"- Name: `{EscapeInlineCode(report.Name)}`");
        if (!string.IsNullOrWhiteSpace(report.PackVersion))
            sb.AppendLine($"- Pack version: `{EscapeInlineCode(report.PackVersion)}`");
        sb.AppendLine($"- Status: {(report.Valid ? "`OK`" : "`FAIL`")}");
        sb.AppendLine($"- RevitCli version: `{EscapeInlineCode(report.CliVersion)}`");
        AppendCompatibilityMarkdown(sb, report.Compatibility);

        sb.AppendLine();
        sb.AppendLine("## Issues");
        if (report.Issues.Count == 0)
        {
            sb.AppendLine("- None.");
        }
        else
        {
            foreach (var issue in report.Issues
                         .OrderByDescending(issue => issue.Severity)
                         .ThenBy(issue => issue.Path, StringComparer.Ordinal))
            {
                sb.AppendLine(
                    $"- `{issue.Severity.ToString().ToUpperInvariant()}` `{EscapeInlineCode(issue.Path)}`: {EscapeMarkdownText(issue.Message)}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static string RenderInstallTable(StandardsInstallResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine(result.DryRun ? "Standards install plan" : "Standards install");
        sb.AppendLine($"Source: {result.Source}");
        sb.AppendLine($"Project: {result.ProjectDirectory}");

        if (result.Changes.Count == 0)
        {
            sb.AppendLine("No files or directories found to install.");
        }
        else
        {
            foreach (var change in result.Changes
                         .OrderBy(change => change.Kind, StringComparer.Ordinal)
                         .ThenBy(change => change.Target, StringComparer.Ordinal))
            {
                sb.AppendLine($"  {change.Action.ToUpperInvariant(),-9} {change.Kind,-9} {change.Target}");
            }
        }

        if (result.Validation != null)
        {
            sb.AppendLine($"Validation: {(result.Validation.Valid ? "OK" : "FAIL")}");
            foreach (var issue in result.Validation.Issues
                         .OrderByDescending(issue => issue.Severity)
                         .ThenBy(issue => issue.Path, StringComparer.Ordinal))
            {
                sb.AppendLine($"  {issue.Severity.ToString().ToUpperInvariant()} {issue.Path}: {issue.Message}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static string RenderInstallMarkdown(StandardsInstallResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine(result.DryRun ? "# Standards Install Plan" : "# Standards Install");
        sb.AppendLine();
        sb.AppendLine($"- Source: `{EscapeInlineCode(result.Source)}`");
        sb.AppendLine($"- Project: `{EscapeInlineCode(result.ProjectDirectory)}`");
        sb.AppendLine($"- Dry run: {(result.DryRun ? "yes" : "no")}");

        sb.AppendLine();
        sb.AppendLine("## Changes");
        if (result.Changes.Count == 0)
        {
            sb.AppendLine("- No files or directories found to install.");
        }
        else
        {
            foreach (var change in result.Changes
                         .OrderBy(change => change.Kind, StringComparer.Ordinal)
                         .ThenBy(change => change.Target, StringComparer.Ordinal))
            {
                sb.AppendLine(
                    $"- `{EscapeInlineCode(change.Action.ToUpperInvariant())}` `{EscapeInlineCode(change.Kind)}` `{EscapeInlineCode(change.Target)}`");
            }
        }

        if (result.Validation != null)
        {
            sb.AppendLine();
            sb.AppendLine("## Validation");
            sb.AppendLine($"- Status: {(result.Validation.Valid ? "`OK`" : "`FAIL`")}");
            if (result.Validation.Issues.Count == 0)
            {
                sb.AppendLine("- Issues: none.");
            }
            else
            {
                foreach (var issue in result.Validation.Issues
                             .OrderByDescending(issue => issue.Severity)
                             .ThenBy(issue => issue.Path, StringComparer.Ordinal))
                {
                    sb.AppendLine(
                        $"- `{issue.Severity.ToString().ToUpperInvariant()}` `{EscapeInlineCode(issue.Path)}`: {EscapeMarkdownText(issue.Message)}");
                }
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static void AppendCompatibilityMarkdown(StringBuilder sb, StandardsCompatibility compatibility)
    {
        if (string.IsNullOrWhiteSpace(compatibility.RevitCli)
            && compatibility.RevitYears.Count == 0
            && compatibility.Notes.Count == 0)
        {
            return;
        }

        sb.AppendLine("- Compatibility:");
        if (!string.IsNullOrWhiteSpace(compatibility.RevitCli))
            sb.AppendLine($"  - RevitCli: `{EscapeInlineCode(compatibility.RevitCli)}`");
        if (compatibility.RevitYears.Count > 0)
            sb.AppendLine($"  - Revit years: `{string.Join(", ", compatibility.RevitYears)}`");
        foreach (var note in compatibility.Notes.Where(note => !string.IsNullOrWhiteSpace(note)).Take(3))
            sb.AppendLine($"  - Note: {EscapeMarkdownText(note)}");
    }

    private static string EscapeInlineCode(string? value)
    {
        return (value ?? string.Empty)
            .Replace("`", "'", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
    }

    private static string EscapeMarkdownText(string? value)
    {
        return (value ?? string.Empty)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
    }

    private static string QuoteArgument(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "\"\"";

        return value.Any(char.IsWhiteSpace) || value.Contains('"', StringComparison.Ordinal)
            ? "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\""
            : value;
    }

    private static string ResolvePath(string projectRoot, string path) =>
        Path.IsPathFullyQualified(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(projectRoot, path));
}
