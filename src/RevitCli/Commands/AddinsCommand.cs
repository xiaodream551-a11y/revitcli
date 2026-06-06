using System;
using System.CommandLine;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using RevitCli.Addins;
using RevitCli.Output;
using RevitCli.Shared;

namespace RevitCli.Commands;

public static class AddinsCommand
{
    private const string DisablePlanSchemaVersion = "revit-addins-disable-plan.v1";
    private const string DisableReceiptSchemaVersion = "revit-addins-disable-receipt.v1";
    private const string DisableRollbackSchemaVersion = "revit-addins-disable-rollback.v1";
    private static readonly JsonSerializerOptions DisableReadOptions = new() { PropertyNameCaseInsensitive = true };

    public static Command Create()
    {
        var command = new Command("addins", "Audit and safely disable local Revit add-in manifests");
        command.AddCommand(CreateAuditCommand());
        command.AddCommand(CreatePlanDisableCommand());
        command.AddCommand(CreateApplyCommand());
        command.AddCommand(CreateRollbackCommand());
        return command;
    }

    private static Command CreateAuditCommand()
    {
        var versionsOpt = new Option<string>("--versions", () => "2024,2025,2026", "Comma-separated Revit years to audit");
        var allUsersRootOpt = new Option<string?>("--all-users-root", "Override all-users add-in root; expected child folders are Revit years");
        var perUserRootOpt = new Option<string?>("--per-user-root", "Override per-user add-in root; expected child folders are Revit years");
        var outputOpt = new Option<string>("--output", () => "table", "Output format: table, json, markdown");
        var findingsOpt = new Option<bool>("--findings", "Emit a bimops-finding.v1 JSON envelope instead of the add-ins report");

        var command = new Command("audit", "Audit .addin manifests for load-order and assembly risks")
        {
            versionsOpt,
            allUsersRootOpt,
            perUserRootOpt,
            outputOpt,
            findingsOpt,
        };

        command.SetHandler(async (versions, allUsersRoot, perUserRoot, outputFormat, findings) =>
        {
            Environment.ExitCode = await ExecuteAuditAsync(
                versions,
                allUsersRoot,
                perUserRoot,
                outputFormat,
                Console.Out,
                findings);
        }, versionsOpt, allUsersRootOpt, perUserRootOpt, outputOpt, findingsOpt);

        return command;
    }

    private static Command CreatePlanDisableCommand()
    {
        var versionsOpt = new Option<string>("--versions", () => "2024,2025,2026", "Comma-separated Revit years to audit");
        var allUsersRootOpt = new Option<string?>("--all-users-root", "Override all-users add-in root; expected child folders are Revit years");
        var perUserRootOpt = new Option<string?>("--per-user-root", "Override per-user add-in root; expected child folders are Revit years");
        var profileOpt = new Option<string>("--profile", () => "office-standard", "Disable profile: office-standard, cloud-safe, or clean");
        var planOutputOpt = new Option<string?>("--plan-output", "Optional path to write the disable plan JSON");
        var outputOpt = new Option<string>("--output", () => "table", "Output format: table, json, markdown");

        var command = new Command("plan-disable", "Create a reviewable add-in manifest disable plan without applying it")
        {
            versionsOpt,
            allUsersRootOpt,
            perUserRootOpt,
            profileOpt,
            planOutputOpt,
            outputOpt,
        };

        command.SetHandler(async (versions, allUsersRoot, perUserRoot, profile, planOutput, outputFormat) =>
        {
            Environment.ExitCode = await ExecutePlanDisableAsync(
                versions,
                allUsersRoot,
                perUserRoot,
                profile,
                planOutput,
                outputFormat,
                Console.Out);
        }, versionsOpt, allUsersRootOpt, perUserRootOpt, profileOpt, planOutputOpt, outputOpt);

        return command;
    }

    private static Command CreateApplyCommand()
    {
        var planOpt = new Option<string>("--plan", "Disable plan JSON produced by addins plan-disable") { IsRequired = true };
        var dryRunOpt = new Option<bool>("--dry-run", "Preview add-in manifest disable without moving files");
        var yesOpt = new Option<bool>("--yes", "Confirm add-in manifest disable in non-interactive mode");
        var receiptOutputOpt = new Option<string?>("--receipt-output", "Optional path to write the disable receipt JSON");
        var envBaselineOpt = new Option<string?>("--env-baseline", $"Environment baseline JSON to link into the receipt; defaults to {RevitEnvBaselineReceiptReferenceBuilder.DefaultBaselinePath}");
        var outputOpt = new Option<string>("--output", () => "table", "Output format: table, json, markdown");

        var command = new Command("apply", "Apply a reviewed add-in manifest disable plan")
        {
            planOpt,
            dryRunOpt,
            yesOpt,
            receiptOutputOpt,
            envBaselineOpt,
            outputOpt,
        };

        command.SetHandler(async (plan, dryRun, yes, receiptOutput, envBaseline, outputFormat) =>
        {
            Environment.ExitCode = await ExecuteApplyAsync(
                plan,
                dryRun,
                yes,
                receiptOutput,
                outputFormat,
                Console.Out,
                envBaselinePath: envBaseline);
        }, planOpt, dryRunOpt, yesOpt, receiptOutputOpt, envBaselineOpt, outputOpt);

        return command;
    }

    private static Command CreateRollbackCommand()
    {
        var receiptOpt = new Option<string>("--receipt", "Disable receipt JSON produced by addins apply") { IsRequired = true };
        var dryRunOpt = new Option<bool>("--dry-run", "Preview add-in manifest rollback without moving files");
        var yesOpt = new Option<bool>("--yes", "Confirm add-in manifest rollback in non-interactive mode");
        var outputOpt = new Option<string>("--output", () => "table", "Output format: table, json, markdown");

        var command = new Command("rollback", "Rollback an add-in manifest disable receipt")
        {
            receiptOpt,
            dryRunOpt,
            yesOpt,
            outputOpt,
        };

        command.SetHandler(async (receipt, dryRun, yes, outputFormat) =>
        {
            Environment.ExitCode = await ExecuteRollbackAsync(
                receipt,
                dryRun,
                yes,
                outputFormat,
                Console.Out);
        }, receiptOpt, dryRunOpt, yesOpt, outputOpt);

        return command;
    }

    public static async Task<int> ExecuteAuditAsync(
        string versions,
        string? allUsersRoot,
        string? perUserRoot,
        string outputFormat,
        TextWriter output,
        bool findings = false)
    {
        if (!TerminalOutputFormat.TryNormalize(outputFormat, out var normalizedOutput, "table", "json", "markdown"))
        {
            await output.WriteLineAsync("Error: --output must be 'table', 'json', or 'markdown'.");
            return 1;
        }

        if (findings && normalizedOutput != "json")
        {
            await output.WriteLineAsync("Error: --findings currently requires '--output json'.");
            return 1;
        }

        RevitAddinAuditReport report;
        try
        {
            report = RevitAddinAuditor.Audit(RevitAddinAuditor.ParseYears(versions), allUsersRoot, perUserRoot);
        }
        catch (ArgumentException ex)
        {
            await output.WriteLineAsync($"Error: {ex.Message}");
            return 1;
        }

        if (findings)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(ToFindingEnvelope(report), TerminalJsonOptions.PrettyCamel));
            return report.Success ? 0 : 1;
        }

        await WriteAuditReportAsync(output, normalizedOutput, report);
        return report.Success ? 0 : 1;
    }

    public static async Task<int> ExecutePlanDisableAsync(
        string versions,
        string? allUsersRoot,
        string? perUserRoot,
        string profile,
        string? planOutput,
        string outputFormat,
        TextWriter output)
    {
        if (!TerminalOutputFormat.TryNormalize(outputFormat, out var normalizedOutput, "table", "json", "markdown"))
        {
            await output.WriteLineAsync("Error: --output must be 'table', 'json', or 'markdown'.");
            return 1;
        }

        if (!TryNormalizeProfile(profile, out var normalizedProfile))
        {
            await output.WriteLineAsync("Error: --profile must be 'office-standard', 'cloud-safe', or 'clean'.");
            return 1;
        }

        RevitAddinAuditReport audit;
        try
        {
            audit = RevitAddinAuditor.Audit(RevitAddinAuditor.ParseYears(versions), allUsersRoot, perUserRoot);
        }
        catch (ArgumentException ex)
        {
            await output.WriteLineAsync($"Error: {ex.Message}");
            return 1;
        }

        var report = BuildDisablePlan(audit, normalizedProfile);
        if (!string.IsNullOrWhiteSpace(planOutput))
        {
            var planPath = Path.GetFullPath(planOutput);
            Directory.CreateDirectory(Path.GetDirectoryName(planPath)!);
            report.PlanOutputPath = planPath;
            await File.WriteAllTextAsync(planPath, JsonSerializer.Serialize(report, TerminalJsonOptions.PrettyCamel));
        }

        await WriteAddinsReportAsync(output, normalizedOutput, report);
        return report.Success ? 0 : 1;
    }

    public static async Task<int> ExecuteApplyAsync(
        string planPath,
        bool dryRun,
        bool yes,
        string? receiptOutput,
        string outputFormat,
        TextWriter output,
        string? envBaselinePath = null)
    {
        if (!TerminalOutputFormat.TryNormalize(outputFormat, out var normalizedOutput, "table", "json", "markdown"))
        {
            await output.WriteLineAsync("Error: --output must be 'table', 'json', or 'markdown'.");
            return 1;
        }

        var fullPlanPath = Path.GetFullPath(planPath);
        var report = new RevitAddinDisableReceipt
        {
            PlanPath = fullPlanPath,
            DryRun = dryRun,
            Confirmed = yes,
        };

        if (!dryRun && !yes)
        {
            AddDisableIssue(report.Issues, "error", "confirmation-required", fullPlanPath, "--yes is required before applying an add-in disable plan.");
            FinalizeDisableReceipt(report);
            await WriteAddinsReportAsync(output, normalizedOutput, report);
            return 1;
        }

        if (dryRun && !string.IsNullOrWhiteSpace(receiptOutput))
        {
            AddDisableIssue(report.Issues, "error", "receipt-output-refused", receiptOutput, "--receipt-output is only written for an approved apply; dry-run must not create a receipt.");
            FinalizeDisableReceipt(report);
            await WriteAddinsReportAsync(output, normalizedOutput, report);
            return 1;
        }

        RevitAddinDisablePlanReport? plan;
        try
        {
            plan = JsonSerializer.Deserialize<RevitAddinDisablePlanReport>(
                await File.ReadAllTextAsync(fullPlanPath),
                DisableReadOptions);
            report.PlanHash = ComputeSha256Hex(fullPlanPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            AddDisableIssue(report.Issues, "error", "plan-read-failed", fullPlanPath, ex.Message);
            FinalizeDisableReceipt(report);
            await WriteAddinsReportAsync(output, normalizedOutput, report);
            return 1;
        }

        if (!ValidateDisablePlan(plan, report.Issues, fullPlanPath))
        {
            FinalizeDisableReceipt(report);
            await WriteAddinsReportAsync(output, normalizedOutput, report);
            return 1;
        }

        foreach (var action in plan!.Actions)
        {
            var sourcePath = Path.GetFullPath(action.ManifestPath);
            var disabledPath = Path.GetFullPath(action.DisabledPath);
            if (!File.Exists(sourcePath))
            {
                AddDisableIssue(report.Issues, "error", "manifest-missing", sourcePath, "The source .addin manifest no longer exists.");
                continue;
            }

            if (File.Exists(disabledPath))
            {
                AddDisableIssue(report.Issues, "error", "disabled-target-exists", disabledPath, "The disabled manifest target already exists.");
                continue;
            }

            var sourceHash = ComputeSha256Hex(sourcePath);
            if (!string.Equals(sourceHash, action.ManifestHash, StringComparison.OrdinalIgnoreCase))
            {
                AddDisableIssue(report.Issues, "error", "manifest-hash-conflict", sourcePath, "The source .addin manifest changed after plan generation.");
                continue;
            }

            var receiptAction = new RevitAddinDisableReceiptAction
            {
                ActionId = action.ActionId,
                Kind = action.Kind,
                Year = action.Year,
                Scope = action.Scope,
                ManifestPath = sourcePath,
                DisabledPath = disabledPath,
                ManifestFileName = action.ManifestFileName,
                ManifestHash = sourceHash,
                Reason = action.Reason,
                Applied = false,
            };

            if (!dryRun)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(disabledPath)!);
                    File.Move(sourcePath, disabledPath);
                    receiptAction.Applied = true;
                    receiptAction.DisabledHash = ComputeSha256Hex(disabledPath);
                    receiptAction.RollbackCommand = "";
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    AddDisableIssue(report.Issues, "error", "apply-failed", sourcePath, ex.Message);
                    continue;
                }
            }

            report.Actions.Add(receiptAction);
        }

        FinalizeDisableReceipt(report);
        if (!dryRun && report.Actions.Any(action => action.Applied))
        {
            var receiptPath = Path.GetFullPath(string.IsNullOrWhiteSpace(receiptOutput)
                ? fullPlanPath + ".receipt.json"
                : receiptOutput);
            foreach (var action in report.Actions)
                action.RollbackCommand = $"revitcli addins rollback --receipt {QuoteArgument(receiptPath)} --dry-run";

            report.ReceiptPath = receiptPath;
            report.ReceiptSaved = true;
            report.EnvBaseline = RevitEnvBaselineReceiptReferenceBuilder.Capture(envBaselinePath);
            Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
            await File.WriteAllTextAsync(receiptPath, JsonSerializer.Serialize(report, TerminalJsonOptions.PrettyCamel));
        }

        await WriteAddinsReportAsync(output, normalizedOutput, report);
        return report.Success ? 0 : 1;
    }

    public static async Task<int> ExecuteRollbackAsync(
        string receiptPath,
        bool dryRun,
        bool yes,
        string outputFormat,
        TextWriter output)
    {
        if (!TerminalOutputFormat.TryNormalize(outputFormat, out var normalizedOutput, "table", "json", "markdown"))
        {
            await output.WriteLineAsync("Error: --output must be 'table', 'json', or 'markdown'.");
            return 1;
        }

        var fullReceiptPath = Path.GetFullPath(receiptPath);
        var report = new RevitAddinDisableRollbackReport
        {
            ReceiptPath = fullReceiptPath,
            DryRun = dryRun,
            Confirmed = yes,
        };

        if (!dryRun && !yes)
        {
            AddDisableIssue(report.Issues, "error", "confirmation-required", fullReceiptPath, "--yes is required before applying add-in disable rollback.");
            FinalizeDisableRollback(report);
            await WriteAddinsReportAsync(output, normalizedOutput, report);
            return 1;
        }

        RevitAddinDisableReceipt? receipt;
        try
        {
            receipt = JsonSerializer.Deserialize<RevitAddinDisableReceipt>(
                await File.ReadAllTextAsync(fullReceiptPath),
                DisableReadOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            AddDisableIssue(report.Issues, "error", "receipt-read-failed", fullReceiptPath, ex.Message);
            FinalizeDisableRollback(report);
            await WriteAddinsReportAsync(output, normalizedOutput, report);
            return 1;
        }

        if (receipt == null ||
            !string.Equals(receipt.SchemaVersion, DisableReceiptSchemaVersion, StringComparison.OrdinalIgnoreCase) ||
            !receipt.Actions.Any(action => action.Applied))
        {
            AddDisableIssue(report.Issues, "error", "invalid-receipt", fullReceiptPath, "The add-in disable receipt is missing, has no applied actions, or has an unsupported schema version.");
            FinalizeDisableRollback(report);
            await WriteAddinsReportAsync(output, normalizedOutput, report);
            return 1;
        }

        foreach (var action in receipt.Actions)
        {
            if (!File.Exists(action.DisabledPath))
            {
                AddDisableIssue(report.Issues, "error", "disabled-manifest-missing", action.DisabledPath, "The disabled manifest file no longer exists.");
                continue;
            }

            if (File.Exists(action.ManifestPath))
            {
                AddDisableIssue(report.Issues, "error", "manifest-target-exists", action.ManifestPath, "The original manifest path already exists.");
                continue;
            }

            var disabledHash = ComputeSha256Hex(action.DisabledPath);
            if (!string.Equals(disabledHash, action.ManifestHash, StringComparison.OrdinalIgnoreCase))
            {
                AddDisableIssue(report.Issues, "error", "disabled-manifest-hash-conflict", action.DisabledPath, "The disabled manifest changed after apply.");
                continue;
            }

            var rollbackAction = new RevitAddinDisableRollbackAction
            {
                ActionId = action.ActionId,
                Year = action.Year,
                Scope = action.Scope,
                ManifestPath = action.ManifestPath,
                DisabledPath = action.DisabledPath,
                ManifestFileName = action.ManifestFileName,
                ManifestHash = disabledHash,
                Reason = action.Reason,
                RolledBack = false,
            };

            if (!dryRun)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(action.ManifestPath)!);
                    File.Move(action.DisabledPath, action.ManifestPath);
                    rollbackAction.RolledBack = true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    AddDisableIssue(report.Issues, "error", "rollback-failed", action.DisabledPath, ex.Message);
                    continue;
                }
            }

            report.Actions.Add(rollbackAction);
        }

        FinalizeDisableRollback(report);
        await WriteAddinsReportAsync(output, normalizedOutput, report);
        return report.Success ? 0 : 1;
    }

    private static RevitAddinDisablePlanReport BuildDisablePlan(
        RevitAddinAuditReport audit,
        string profile)
    {
        var report = new RevitAddinDisablePlanReport
        {
            Profile = profile,
            Years = audit.Years.ToList(),
            Roots = audit.Roots.ToList(),
            ManifestCount = audit.ManifestCount,
            AuditIssueCount = audit.IssueCount,
        };

        var issuesByManifest = audit.Issues
            .GroupBy(issue => issue.ManifestPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var addinsByManifest = audit.AddIns
            .GroupBy(addin => addin.ManifestPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

        foreach (var manifest in audit.Manifests
                     .OrderBy(manifest => manifest.Year)
                     .ThenBy(manifest => manifest.Scope, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(manifest => manifest.Path, StringComparer.OrdinalIgnoreCase))
        {
            issuesByManifest.TryGetValue(manifest.Path, out var manifestIssues);
            addinsByManifest.TryGetValue(manifest.Path, out var manifestAddins);
            if (!ShouldDisableManifest(profile, manifest, manifestIssues ?? Array.Empty<RevitAddinAuditIssue>(), manifestAddins ?? Array.Empty<RevitAddinRecord>(), out var reason))
            {
                report.Skipped.Add(new RevitAddinDisableSkippedManifest
                {
                    Year = manifest.Year,
                    Scope = manifest.Scope,
                    ManifestPath = manifest.Path,
                    ManifestFileName = manifest.FileName,
                    Reason = reason,
                });
                continue;
            }

            if (!File.Exists(manifest.Path))
            {
                AddDisableIssue(report.Issues, "error", "manifest-missing", manifest.Path, "The source .addin manifest no longer exists.");
                continue;
            }

            var disabledPath = manifest.Path + ".disabled";
            if (File.Exists(disabledPath))
            {
                AddDisableIssue(report.Issues, "error", "disabled-target-exists", disabledPath, "The disabled manifest target already exists.");
                continue;
            }

            report.Actions.Add(new RevitAddinDisablePlanAction
            {
                ActionId = $"addins.disable.{report.Actions.Count + 1:D3}",
                Kind = "rename-manifest",
                Year = manifest.Year,
                Scope = manifest.Scope,
                ManifestPath = manifest.Path,
                DisabledPath = disabledPath,
                ManifestFileName = manifest.FileName,
                ManifestHash = ComputeSha256Hex(manifest.Path),
                Reason = reason,
                Reversible = true,
                VerifyCommand = $"revitcli addins audit --versions {manifest.Year} --output json",
                Rollback = new RevitAddinDisableRollbackPointer
                {
                    Kind = "rename-disabled-manifest",
                    ManifestPath = manifest.Path,
                    DisabledPath = disabledPath,
                },
            });
        }

        FinalizeDisablePlan(report);
        return report;
    }

    private static bool ShouldDisableManifest(
        string profile,
        RevitAddinManifestRecord manifest,
        IReadOnlyCollection<RevitAddinAuditIssue> issues,
        IReadOnlyCollection<RevitAddinRecord> addins,
        out string reason)
    {
        if (string.Equals(profile, "clean", StringComparison.OrdinalIgnoreCase))
        {
            reason = "clean profile disables every discovered .addin manifest.";
            return true;
        }

        if (string.Equals(profile, "cloud-safe", StringComparison.OrdinalIgnoreCase))
        {
            if (IsCloudProtectedManifest(manifest, addins))
            {
                reason = "cloud-safe profile keeps Autodesk/cloud/collaboration manifests enabled.";
                return false;
            }

            reason = "cloud-safe profile disables non-Autodesk/non-cloud manifests.";
            return true;
        }

        var errorCodes = issues
            .Where(issue => string.Equals(issue.Severity, "error", StringComparison.OrdinalIgnoreCase))
            .Select(issue => issue.Code)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (errorCodes.Length == 0)
        {
            reason = "office-standard profile keeps manifests without error-level audit findings enabled.";
            return false;
        }

        reason = $"office-standard profile disables manifests with error-level findings: {string.Join(", ", errorCodes)}.";
        return true;
    }

    private static bool IsCloudProtectedManifest(
        RevitAddinManifestRecord manifest,
        IReadOnlyCollection<RevitAddinRecord> addins)
    {
        // Autodesk's own vendor id is the most reliable first-party signal.
        if (addins.Any(addin => string.Equals(addin.VendorId, "ADSK", StringComparison.OrdinalIgnoreCase)))
            return true;

        // Match only on add-in identity. The manifest's full path and the resolved
        // assembly path always live under ...\Autodesk\Revit\Addins, so including
        // them would mark every discovered manifest as Autodesk-owned and disable nothing.
        var text = string.Join(
            " ",
            new[]
            {
                manifest.FileName,
            }.Concat(addins.SelectMany(addin => new[]
            {
                addin.Name,
                addin.VendorId,
                addin.AssemblyRaw,
            })));
        return ContainsAny(
            text,
            "autodesk",
            "collaboration",
            "collaborate",
            "cloud",
            "bim 360",
            "bim360",
            "construction cloud",
            "desktop connector",
            "autodesk docs");
    }

    private static bool ContainsAny(string value, params string[] tokens) =>
        tokens.Any(token => value.Contains(token, StringComparison.OrdinalIgnoreCase));

    private static int AddinSeverityRank(string severity) =>
        severity.ToLowerInvariant() switch
        {
            "error" => 3,
            "warning" => 2,
            "info" => 1,
            _ => 0,
        };

    private static bool TryNormalizeProfile(string value, out string normalized)
    {
        normalized = value.Trim().ToLowerInvariant();
        return normalized is "office-standard" or "cloud-safe" or "clean";
    }

    private static bool ValidateDisablePlan(
        RevitAddinDisablePlanReport? plan,
        List<RevitAddinDisableIssue> issues,
        string planPath)
    {
        if (plan == null)
        {
            AddDisableIssue(issues, "error", "invalid-plan", planPath, "Disable plan JSON could not be read.");
            return false;
        }

        if (!string.Equals(plan.SchemaVersion, DisablePlanSchemaVersion, StringComparison.OrdinalIgnoreCase))
        {
            AddDisableIssue(issues, "error", "unsupported-plan-schema", planPath, $"Expected {DisablePlanSchemaVersion}.");
            return false;
        }

        if (!plan.Success)
        {
            AddDisableIssue(issues, "error", "failed-plan", planPath, "Only successful add-in disable plans can be applied.");
            return false;
        }

        if (plan.Actions.Count == 0)
            AddDisableIssue(issues, "warning", "empty-plan", planPath, "Disable plan has no actions.");

        return true;
    }

    private static void FinalizeDisablePlan(RevitAddinDisablePlanReport report)
    {
        report.ActionCount = report.Actions.Count;
        report.SkippedCount = report.Skipped.Count;
        report.IssueCount = report.Issues.Count;
        report.Success = report.Issues.All(issue => !string.Equals(issue.Severity, "error", StringComparison.OrdinalIgnoreCase));
    }

    private static void FinalizeDisableReceipt(RevitAddinDisableReceipt report)
    {
        report.ActionCount = report.Actions.Count;
        report.AppliedCount = report.Actions.Count(action => action.Applied);
        report.IssueCount = report.Issues.Count;
        report.Success = report.Issues.All(issue => !string.Equals(issue.Severity, "error", StringComparison.OrdinalIgnoreCase));
    }

    private static void FinalizeDisableRollback(RevitAddinDisableRollbackReport report)
    {
        report.ActionCount = report.Actions.Count;
        report.RolledBackCount = report.Actions.Count(action => action.RolledBack);
        report.IssueCount = report.Issues.Count;
        report.Success = report.Issues.All(issue => !string.Equals(issue.Severity, "error", StringComparison.OrdinalIgnoreCase));
    }

    private static void AddDisableIssue(
        List<RevitAddinDisableIssue> issues,
        string severity,
        string code,
        string? path,
        string message)
    {
        issues.Add(new RevitAddinDisableIssue
        {
            Severity = severity,
            Code = code,
            Path = path,
            Message = message,
        });
    }

    private static string ComputeSha256Hex(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static BimOpsFindingEnvelope ToFindingEnvelope(RevitAddinAuditReport report)
    {
        var envelope = new BimOpsFindingEnvelope
        {
            Source = "addins.audit",
            GeneratedAtUtc = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            RequiresRevit = false,
            Summary = new BimOpsFindingSummary
            {
                Warnings = report.Issues.Count(issue => string.Equals(issue.Severity, "warning", StringComparison.OrdinalIgnoreCase)),
                Errors = report.Issues.Count(issue => string.Equals(issue.Severity, "error", StringComparison.OrdinalIgnoreCase)),
            },
        };

        var index = 1;
        foreach (var issue in report.Issues)
        {
            envelope.Findings.Add(new BimOpsFinding
            {
                FindingId = $"ADDIN-{index++:D3}-{NormalizeToken(issue.Code)}",
                RuleId = $"addins.{NormalizeRuleId(issue.Code)}",
                Source = "addins.audit",
                Severity = string.Equals(issue.Severity, "error", StringComparison.OrdinalIgnoreCase)
                    ? BimOpsFindingSchema.SeverityError
                    : BimOpsFindingSchema.SeverityWarning,
                Category = "addins",
                Message = issue.Message,
                RequiresRevit = false,
                Fixability = BimOpsFindingSchema.FixabilityManual,
                Target = new BimOpsFindingTarget
                {
                    ArtifactPath = issue.ManifestPath,
                    Category = "addin-manifest",
                    Parameter = issue.Code,
                },
                Evidence =
                {
                    new BimOpsFindingEvidence
                    {
                        Kind = "addin-manifest",
                        Source = issue.ManifestPath,
                        Locator = issue.Code,
                        Value = issue.AssemblyPath,
                    },
                },
                Remediation = new BimOpsFindingRemediation
                {
                    Mode = BimOpsFindingSchema.FixabilityManual,
                    VerifyCommand = "revitcli addins audit --findings --output json",
                    NextAction = "Review add-in manifest ownership, assembly paths, and duplicate ids before disabling or moving any files.",
                },
            });
        }

        return envelope;
    }

    private static async Task WriteAuditReportAsync(
        TextWriter output,
        string outputFormat,
        RevitAddinAuditReport report)
        => await WriteAddinsReportAsync(output, outputFormat, report);

    private static async Task WriteAddinsReportAsync(
        TextWriter output,
        string outputFormat,
        object report)
    {
        switch (outputFormat)
        {
            case "json":
                await output.WriteLineAsync(JsonSerializer.Serialize(report, TerminalJsonOptions.PrettyCamel));
                break;
            case "markdown":
                await output.WriteLineAsync(RenderMarkdown(report));
                break;
            default:
                await output.WriteLineAsync(RenderTable(report));
                break;
        }
    }

    private static string RenderTable(object report) =>
        report switch
        {
            RevitAddinAuditReport audit => RenderAuditTable(audit),
            RevitAddinDisablePlanReport plan => RenderDisablePlanTable(plan),
            RevitAddinDisableReceipt receipt => RenderDisableReceiptTable(receipt),
            RevitAddinDisableRollbackReport rollback => RenderDisableRollbackTable(rollback),
            _ => report.ToString() ?? "",
        };

    private static string RenderMarkdown(object report) =>
        report switch
        {
            RevitAddinAuditReport audit => RenderAuditMarkdown(audit),
            RevitAddinDisablePlanReport plan => RenderDisablePlanMarkdown(plan),
            RevitAddinDisableReceipt receipt => RenderDisableReceiptMarkdown(receipt),
            RevitAddinDisableRollbackReport rollback => RenderDisableRollbackMarkdown(rollback),
            _ => report.ToString() ?? "",
        };

    private static string RenderAuditTable(RevitAddinAuditReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Revit add-ins audit");
        sb.AppendLine($"Years: {string.Join(", ", report.Years)}");
        sb.AppendLine($"Manifests: {report.ManifestCount}");
        sb.AppendLine($"Add-ins: {report.AddInCount}");
        sb.AppendLine($"Status: {(report.Success ? "OK" : "FAIL")}");
        if (report.Issues.Count == 0)
        {
            sb.AppendLine("No issues.");
            return sb.ToString().TrimEnd();
        }

        foreach (var issue in report.Issues
                     .OrderByDescending(issue => AddinSeverityRank(issue.Severity))
                     .ThenBy(issue => issue.Year)
                     .ThenBy(issue => issue.Code, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(issue => issue.ManifestPath, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine($"  {issue.Severity.ToUpperInvariant(),-7} {issue.Code,-24} {issue.Year} {issue.Scope} {issue.Message}");
        }

        return sb.ToString().TrimEnd();
    }

    private static string RenderAuditMarkdown(RevitAddinAuditReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Revit Add-ins Audit");
        sb.AppendLine();
        sb.AppendLine($"- Schema: `{RevitAddinAuditor.SchemaVersion}`");
        sb.AppendLine($"- Years: `{string.Join(", ", report.Years)}`");
        sb.AppendLine($"- Status: {(report.Success ? "`OK`" : "`FAIL`")}");
        sb.AppendLine($"- Manifests: `{report.ManifestCount}`");
        sb.AppendLine($"- Add-ins: `{report.AddInCount}`");
        sb.AppendLine();
        sb.AppendLine("## Issues");
        if (report.Issues.Count == 0)
        {
            sb.AppendLine("- None.");
            return sb.ToString().TrimEnd();
        }

        sb.AppendLine("| Severity | Code | Year | Scope | Manifest | Message |");
        sb.AppendLine("|---|---|---:|---|---|---|");
        foreach (var issue in report.Issues
                     .OrderByDescending(issue => AddinSeverityRank(issue.Severity))
                     .ThenBy(issue => issue.Year)
                     .ThenBy(issue => issue.Code, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(issue => issue.ManifestPath, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine(
                $"| `{Escape(issue.Severity)}` | `{Escape(issue.Code)}` | {issue.Year} | `{Escape(issue.Scope)}` | `{Escape(Path.GetFileName(issue.ManifestPath))}` | {Escape(issue.Message)} |");
        }

        return sb.ToString().TrimEnd();
    }

    private static string RenderDisablePlanTable(RevitAddinDisablePlanReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Revit add-ins disable plan");
        sb.AppendLine($"Profile: {report.Profile}");
        sb.AppendLine($"Status: {(report.Success ? "OK" : "FAIL")}");
        sb.AppendLine($"Actions: {report.ActionCount}");
        sb.AppendLine($"Skipped: {report.SkippedCount}");
        if (!string.IsNullOrWhiteSpace(report.PlanOutputPath))
            sb.AppendLine($"Plan output: {report.PlanOutputPath}");
        foreach (var action in report.Actions.Take(20))
            sb.AppendLine($"  {action.ActionId}: {action.Year} {action.Scope} {action.ManifestFileName} -> {Path.GetFileName(action.DisabledPath)}");
        if (report.Actions.Count > 20)
            sb.AppendLine($"  ... and {report.Actions.Count - 20} more.");
        AppendDisableIssues(sb, report.Issues);
        return sb.ToString().TrimEnd();
    }

    private static string RenderDisableReceiptTable(RevitAddinDisableReceipt report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Revit add-ins disable receipt");
        sb.AppendLine($"Status: {(report.Success ? "OK" : "FAIL")}");
        sb.AppendLine($"Mode: {(report.DryRun ? "dry-run" : "apply")}");
        sb.AppendLine($"Actions: {report.ActionCount}");
        sb.AppendLine($"Applied: {report.AppliedCount}");
        if (!string.IsNullOrWhiteSpace(report.ReceiptPath))
            sb.AppendLine($"Receipt: {report.ReceiptPath}");
        AppendEnvBaseline(sb, report.EnvBaseline, markdown: false);
        foreach (var action in report.Actions.Take(20))
            sb.AppendLine($"  {action.ActionId}: {action.ManifestFileName} -> {Path.GetFileName(action.DisabledPath)}");
        if (report.Actions.Count > 20)
            sb.AppendLine($"  ... and {report.Actions.Count - 20} more.");
        AppendDisableIssues(sb, report.Issues);
        return sb.ToString().TrimEnd();
    }

    private static string RenderDisableRollbackTable(RevitAddinDisableRollbackReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Revit add-ins disable rollback");
        sb.AppendLine($"Status: {(report.Success ? "OK" : "FAIL")}");
        sb.AppendLine($"Mode: {(report.DryRun ? "dry-run" : "apply")}");
        sb.AppendLine($"Actions: {report.ActionCount}");
        sb.AppendLine($"Rolled back: {report.RolledBackCount}");
        foreach (var action in report.Actions.Take(20))
            sb.AppendLine($"  {action.ActionId}: {Path.GetFileName(action.DisabledPath)} -> {action.ManifestFileName}");
        if (report.Actions.Count > 20)
            sb.AppendLine($"  ... and {report.Actions.Count - 20} more.");
        AppendDisableIssues(sb, report.Issues);
        return sb.ToString().TrimEnd();
    }

    private static string RenderDisablePlanMarkdown(RevitAddinDisablePlanReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Revit Add-ins Disable Plan");
        sb.AppendLine();
        sb.AppendLine($"- Schema: `{report.SchemaVersion}`");
        sb.AppendLine($"- Profile: `{Escape(report.Profile)}`");
        sb.AppendLine($"- Success: `{report.Success.ToString().ToLowerInvariant()}`");
        sb.AppendLine($"- Actions: `{report.ActionCount}`");
        sb.AppendLine($"- Skipped: `{report.SkippedCount}`");
        if (!string.IsNullOrWhiteSpace(report.PlanOutputPath))
            sb.AppendLine($"- Plan output: `{Escape(report.PlanOutputPath)}`");
        AppendPlanActionTable(sb, report.Actions);
        AppendDisableIssuesMarkdown(sb, report.Issues);
        return sb.ToString().TrimEnd();
    }

    private static string RenderDisableReceiptMarkdown(RevitAddinDisableReceipt report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Revit Add-ins Disable Receipt");
        sb.AppendLine();
        sb.AppendLine($"- Schema: `{report.SchemaVersion}`");
        sb.AppendLine($"- Success: `{report.Success.ToString().ToLowerInvariant()}`");
        sb.AppendLine($"- Mode: `{(report.DryRun ? "dry-run" : "apply")}`");
        sb.AppendLine($"- Actions: `{report.ActionCount}`");
        sb.AppendLine($"- Applied: `{report.AppliedCount}`");
        if (!string.IsNullOrWhiteSpace(report.ReceiptPath))
            sb.AppendLine($"- Receipt: `{Escape(report.ReceiptPath)}`");
        AppendEnvBaseline(sb, report.EnvBaseline, markdown: true);
        AppendReceiptActionTable(sb, report.Actions);
        AppendDisableIssuesMarkdown(sb, report.Issues);
        return sb.ToString().TrimEnd();
    }

    private static void AppendEnvBaseline(StringBuilder sb, RevitEnvBaselineReceiptReference? reference, bool markdown)
    {
        if (reference == null)
            return;

        if (markdown)
        {
            sb.AppendLine($"- Env baseline: `{Escape(reference.Status)}` `{Escape(reference.Path)}`");
            if (!string.IsNullOrWhiteSpace(reference.Sha256))
                sb.AppendLine($"- Env baseline SHA256: `{Escape(reference.Sha256)}`");
            if (!string.IsNullOrWhiteSpace(reference.Issue))
                sb.AppendLine($"- Env baseline issue: {Escape(reference.Issue)}");
            if (!string.IsNullOrWhiteSpace(reference.CaptureCommand) &&
                !string.Equals(reference.Status, "linked", StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine($"- Env baseline capture: `{Escape(reference.CaptureCommand)}`");
            }

            return;
        }

        sb.AppendLine($"Env baseline: {reference.Status} ({reference.Path})");
        if (!string.IsNullOrWhiteSpace(reference.Sha256))
            sb.AppendLine($"Env baseline SHA256: {reference.Sha256}");
        if (!string.IsNullOrWhiteSpace(reference.Issue))
            sb.AppendLine($"Env baseline issue: {reference.Issue}");
        if (!string.IsNullOrWhiteSpace(reference.CaptureCommand) &&
            !string.Equals(reference.Status, "linked", StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine($"Env baseline capture: {reference.CaptureCommand}");
        }
    }

    private static string RenderDisableRollbackMarkdown(RevitAddinDisableRollbackReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Revit Add-ins Disable Rollback");
        sb.AppendLine();
        sb.AppendLine($"- Schema: `{report.SchemaVersion}`");
        sb.AppendLine($"- Success: `{report.Success.ToString().ToLowerInvariant()}`");
        sb.AppendLine($"- Mode: `{(report.DryRun ? "dry-run" : "apply")}`");
        sb.AppendLine($"- Actions: `{report.ActionCount}`");
        sb.AppendLine($"- Rolled back: `{report.RolledBackCount}`");
        sb.AppendLine();
        sb.AppendLine("| Action | Year | Scope | Disabled | Restored |");
        sb.AppendLine("|---|---:|---|---|---|");
        foreach (var action in report.Actions)
            sb.AppendLine($"| `{Escape(action.ActionId)}` | {action.Year} | `{Escape(action.Scope)}` | `{Escape(action.DisabledPath)}` | `{Escape(action.ManifestPath)}` |");
        AppendDisableIssuesMarkdown(sb, report.Issues);
        return sb.ToString().TrimEnd();
    }

    private static void AppendPlanActionTable(StringBuilder sb, IReadOnlyCollection<RevitAddinDisablePlanAction> actions)
    {
        sb.AppendLine();
        sb.AppendLine("## Actions");
        if (actions.Count == 0)
        {
            sb.AppendLine("- None.");
            return;
        }

        sb.AppendLine("| Action | Year | Scope | Manifest | Disabled path | Reason |");
        sb.AppendLine("|---|---:|---|---|---|---|");
        foreach (var action in actions)
            sb.AppendLine($"| `{Escape(action.ActionId)}` | {action.Year} | `{Escape(action.Scope)}` | `{Escape(action.ManifestPath)}` | `{Escape(action.DisabledPath)}` | {Escape(action.Reason)} |");
    }

    private static void AppendReceiptActionTable(StringBuilder sb, IReadOnlyCollection<RevitAddinDisableReceiptAction> actions)
    {
        sb.AppendLine();
        sb.AppendLine("## Actions");
        if (actions.Count == 0)
        {
            sb.AppendLine("- None.");
            return;
        }

        sb.AppendLine("| Action | Year | Scope | Manifest | Disabled path | Applied |");
        sb.AppendLine("|---|---:|---|---|---|---|");
        foreach (var action in actions)
            sb.AppendLine($"| `{Escape(action.ActionId)}` | {action.Year} | `{Escape(action.Scope)}` | `{Escape(action.ManifestPath)}` | `{Escape(action.DisabledPath)}` | `{action.Applied.ToString().ToLowerInvariant()}` |");
    }

    private static void AppendDisableIssues(StringBuilder sb, IReadOnlyCollection<RevitAddinDisableIssue> issues)
    {
        if (issues.Count == 0)
            return;

        sb.AppendLine("Issues:");
        foreach (var issue in issues)
            sb.AppendLine($"  {issue.Severity.ToUpperInvariant(),-7} {issue.Code,-24} {issue.Message}");
    }

    private static void AppendDisableIssuesMarkdown(StringBuilder sb, IReadOnlyCollection<RevitAddinDisableIssue> issues)
    {
        if (issues.Count == 0)
            return;

        sb.AppendLine();
        sb.AppendLine("## Issues");
        sb.AppendLine();
        sb.AppendLine("| Severity | Code | Path | Message |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var issue in issues)
            sb.AppendLine($"| `{Escape(issue.Severity)}` | `{Escape(issue.Code)}` | `{Escape(issue.Path ?? "")}` | {Escape(issue.Message)} |");
    }

    private static string NormalizeRuleId(string value) =>
        NormalizeToken(value).ToLowerInvariant().Replace("-", ".", StringComparison.Ordinal);

    private static string NormalizeToken(string value)
    {
        var chars = (value ?? "")
            .Select(ch => char.IsLetterOrDigit(ch) ? char.ToUpperInvariant(ch) : '-')
            .ToArray();
        var token = new string(chars).Trim('-');
        while (token.Contains("--", StringComparison.Ordinal))
            token = token.Replace("--", "-", StringComparison.Ordinal);
        return string.IsNullOrWhiteSpace(token) ? "UNKNOWN" : token;
    }

    private static string Escape(string value) =>
        (value ?? "")
        .Replace("|", "\\|", StringComparison.Ordinal)
        .Replace("\r", " ", StringComparison.Ordinal)
        .Replace("\n", " ", StringComparison.Ordinal);

    private static string QuoteArgument(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "\"\"";

        return value.Any(char.IsWhiteSpace) || value.Contains('"', StringComparison.Ordinal)
            ? "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\""
            : value;
    }
}

public sealed class RevitAddinDisablePlanReport
{
    public string SchemaVersion { get; set; } = "revit-addins-disable-plan.v1";
    public DateTimeOffset GeneratedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public bool Success { get; set; }
    public string Profile { get; set; } = "";
    public List<int> Years { get; set; } = new();
    public List<RevitAddinRoot> Roots { get; set; } = new();
    public string? PlanOutputPath { get; set; }
    public int ManifestCount { get; set; }
    public int AuditIssueCount { get; set; }
    public int ActionCount { get; set; }
    public int SkippedCount { get; set; }
    public int IssueCount { get; set; }
    public List<RevitAddinDisablePlanAction> Actions { get; set; } = new();
    public List<RevitAddinDisableSkippedManifest> Skipped { get; set; } = new();
    public List<RevitAddinDisableIssue> Issues { get; set; } = new();
}

public sealed class RevitAddinDisablePlanAction
{
    public string ActionId { get; set; } = "";
    public string Kind { get; set; } = "rename-manifest";
    public int Year { get; set; }
    public string Scope { get; set; } = "";
    public string ManifestPath { get; set; } = "";
    public string DisabledPath { get; set; } = "";
    public string ManifestFileName { get; set; } = "";
    public string ManifestHash { get; set; } = "";
    public string Reason { get; set; } = "";
    public bool Reversible { get; set; }
    public string VerifyCommand { get; set; } = "";
    public RevitAddinDisableRollbackPointer? Rollback { get; set; }
}

public sealed class RevitAddinDisableRollbackPointer
{
    public string Kind { get; set; } = "";
    public string ManifestPath { get; set; } = "";
    public string DisabledPath { get; set; } = "";
}

public sealed class RevitAddinDisableSkippedManifest
{
    public int Year { get; set; }
    public string Scope { get; set; } = "";
    public string ManifestPath { get; set; } = "";
    public string ManifestFileName { get; set; } = "";
    public string Reason { get; set; } = "";
}

public sealed class RevitAddinDisableReceipt
{
    public string SchemaVersion { get; set; } = "revit-addins-disable-receipt.v1";
    public DateTimeOffset GeneratedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public bool Success { get; set; }
    public bool DryRun { get; set; }
    public bool Confirmed { get; set; }
    public string PlanPath { get; set; } = "";
    public string PlanHash { get; set; } = "";
    public string? ReceiptPath { get; set; }
    public bool ReceiptSaved { get; set; }
    public int ActionCount { get; set; }
    public int AppliedCount { get; set; }
    public int IssueCount { get; set; }
    public RevitEnvBaselineReceiptReference? EnvBaseline { get; set; }
    public List<RevitAddinDisableReceiptAction> Actions { get; set; } = new();
    public List<RevitAddinDisableIssue> Issues { get; set; } = new();
}

public sealed class RevitAddinDisableReceiptAction
{
    public string ActionId { get; set; } = "";
    public string Kind { get; set; } = "";
    public int Year { get; set; }
    public string Scope { get; set; } = "";
    public string ManifestPath { get; set; } = "";
    public string DisabledPath { get; set; } = "";
    public string ManifestFileName { get; set; } = "";
    public string ManifestHash { get; set; } = "";
    public string DisabledHash { get; set; } = "";
    public string Reason { get; set; } = "";
    public bool Applied { get; set; }
    public string RollbackCommand { get; set; } = "";
}

public sealed class RevitAddinDisableRollbackReport
{
    public string SchemaVersion { get; set; } = "revit-addins-disable-rollback.v1";
    public DateTimeOffset GeneratedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public bool Success { get; set; }
    public bool DryRun { get; set; }
    public bool Confirmed { get; set; }
    public string ReceiptPath { get; set; } = "";
    public int ActionCount { get; set; }
    public int RolledBackCount { get; set; }
    public int IssueCount { get; set; }
    public List<RevitAddinDisableRollbackAction> Actions { get; set; } = new();
    public List<RevitAddinDisableIssue> Issues { get; set; } = new();
}

public sealed class RevitAddinDisableRollbackAction
{
    public string ActionId { get; set; } = "";
    public int Year { get; set; }
    public string Scope { get; set; } = "";
    public string ManifestPath { get; set; } = "";
    public string DisabledPath { get; set; } = "";
    public string ManifestFileName { get; set; } = "";
    public string ManifestHash { get; set; } = "";
    public string Reason { get; set; } = "";
    public bool RolledBack { get; set; }
}

public sealed class RevitAddinDisableIssue
{
    public string Severity { get; set; } = "";
    public string Code { get; set; } = "";
    public string? Path { get; set; }
    public string Message { get; set; } = "";
}
