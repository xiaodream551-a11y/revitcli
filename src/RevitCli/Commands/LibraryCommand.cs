using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RevitCli.Libraries;
using RevitCli.Output;
using RevitCli.Shared;

namespace RevitCli.Commands;

public static class LibraryCommand
{
    private const string RepairPlanSchemaVersion = "revit-library-repair-plan.v1";
    private const string RepairReceiptSchemaVersion = "revit-library-repair-receipt.v1";
    private const string RepairRollbackSchemaVersion = "revit-library-repair-rollback.v1";
    private static readonly JsonSerializerOptions RepairReadOptions = new() { PropertyNameCaseInsensitive = true };

    public static Command Create()
    {
        var command = new Command("library", "Find, verify, and fetch Autodesk Revit content libraries");
        command.AddCommand(CreateCheckCommand());
        command.AddCommand(CreateSourcesCommand());
        command.AddCommand(CreateDownloadCommand());
        command.AddCommand(CreateInstallCommand());
        command.AddCommand(CreateRepairPlanCommand());
        command.AddCommand(CreateRepairApplyCommand());
        command.AddCommand(CreateRepairRollbackCommand());
        return command;
    }

    private static Command CreateCheckCommand()
    {
        var yearOpt = new Option<int>("--year", () => 2026, "Revit version year: 2024, 2025, or 2026");
        var localeOpt = new Option<string>("--locale", () => "ENU", "Autodesk content locale code such as ENU or CHS");
        var contentRootOpt = new Option<string?>("--content-root", "Override the Revit content root path");
        var revitIniOpt = new Option<string?>("--revit-ini", "Override the Revit.ini path to inspect");
        var outputOpt = new Option<string>("--output", () => "table", "Output format: table, json, markdown");
        var findingsOpt = new Option<bool>("--findings", "Emit a bimops-finding.v1 JSON envelope instead of the legacy library report");

        var command = new Command("check", "Check local Revit library and family template content")
        {
            yearOpt,
            localeOpt,
            contentRootOpt,
            revitIniOpt,
            outputOpt,
            findingsOpt,
        };

        command.SetHandler(async (year, locale, contentRoot, revitIni, outputFormat, findings) =>
        {
            Environment.ExitCode = await ExecuteCheckAsync(
                year, locale, contentRoot, revitIni, outputFormat, Console.Out, findings);
        }, yearOpt, localeOpt, contentRootOpt, revitIniOpt, outputOpt, findingsOpt);

        return command;
    }

    private static Command CreateRepairApplyCommand()
    {
        var planOpt = new Option<string>("--plan", "Repair plan JSON produced by library repair-plan") { IsRequired = true };
        var dryRunOpt = new Option<bool>("--dry-run", "Preview Revit.ini repair without writing");
        var yesOpt = new Option<bool>("--yes", "Confirm Revit.ini repair in non-interactive mode");
        var receiptOutputOpt = new Option<string?>("--receipt-output", "Optional path to write the repair receipt JSON");
        var envBaselineOpt = new Option<string?>("--env-baseline", $"Environment baseline JSON to link into the receipt; defaults to {RevitEnvBaselineReceiptReferenceBuilder.DefaultBaselinePath}");
        var outputOpt = new Option<string>("--output", () => "table", "Output format: table, json, markdown");

        var command = new Command("repair-apply", "Apply a reviewed Revit.ini library repair plan")
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
            Environment.ExitCode = await ExecuteRepairApplyAsync(
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

    private static Command CreateRepairRollbackCommand()
    {
        var receiptOpt = new Option<string>("--receipt", "Repair receipt JSON produced by library repair-apply") { IsRequired = true };
        var dryRunOpt = new Option<bool>("--dry-run", "Preview Revit.ini rollback without writing");
        var yesOpt = new Option<bool>("--yes", "Confirm Revit.ini rollback in non-interactive mode");
        var outputOpt = new Option<string>("--output", () => "table", "Output format: table, json, markdown");

        var command = new Command("repair-rollback", "Rollback a Revit.ini library repair receipt")
        {
            receiptOpt,
            dryRunOpt,
            yesOpt,
            outputOpt,
        };

        command.SetHandler(async (receipt, dryRun, yes, outputFormat) =>
        {
            Environment.ExitCode = await ExecuteRepairRollbackAsync(
                receipt,
                dryRun,
                yes,
                outputFormat,
                Console.Out);
        }, receiptOpt, dryRunOpt, yesOpt, outputOpt);

        return command;
    }

    private static Command CreateRepairPlanCommand()
    {
        var yearOpt = new Option<int>("--year", () => 2026, "Revit version year: 2024, 2025, or 2026");
        var localeOpt = new Option<string>("--locale", () => "ENU", "Autodesk content locale code such as ENU or CHS");
        var contentRootOpt = new Option<string?>("--content-root", "Override the Revit content root path");
        var revitIniOpt = new Option<string>("--revit-ini", "Revit.ini path to inspect and plan repairs for") { IsRequired = true };
        var planOutputOpt = new Option<string?>("--plan-output", "Optional path to write the repair plan JSON");
        var outputOpt = new Option<string>("--output", () => "table", "Output format: table, json, markdown");

        var command = new Command("repair-plan", "Create a reviewable Revit.ini library repair plan without applying it")
        {
            yearOpt,
            localeOpt,
            contentRootOpt,
            revitIniOpt,
            planOutputOpt,
            outputOpt,
        };

        command.SetHandler(async (year, locale, contentRoot, revitIni, planOutput, outputFormat) =>
        {
            Environment.ExitCode = await ExecuteRepairPlanAsync(
                year,
                locale,
                contentRoot,
                revitIni,
                planOutput,
                outputFormat,
                Console.Out);
        }, yearOpt, localeOpt, contentRootOpt, revitIniOpt, planOutputOpt, outputOpt);

        return command;
    }

    private static Command CreateSourcesCommand()
    {
        var yearOpt = new Option<int>("--year", () => 2026, "Revit version year: 2024, 2025, or 2026");
        var localeOpt = new Option<string>("--locale", () => "ENU", "Autodesk content locale code such as ENU or CHS");
        var outputOpt = new Option<string>("--output", () => "table", "Output format: table, json, markdown");

        var command = new Command("sources", "Show official Autodesk Revit library content sources")
        {
            yearOpt,
            localeOpt,
            outputOpt,
        };

        command.SetHandler(async (year, locale, outputFormat) =>
        {
            Environment.ExitCode = await ExecuteSourcesAsync(year, locale, outputFormat, Console.Out);
        }, yearOpt, localeOpt, outputOpt);

        return command;
    }

    private static Command CreateDownloadCommand()
    {
        var yearOpt = new Option<int>("--year", () => 2026, "Revit version year: 2024, 2025, or 2026");
        var localeOpt = new Option<string>("--locale", () => "ENU", "Autodesk content locale code such as ENU or CHS");
        var downloadDirOpt = new Option<string>(
            "--download-dir",
            () => RevitLibraryPathMapper.DefaultDownloadDirectory(),
            "Directory where the official content installer should be saved");
        var urlOpt = new Option<string?>("--url", "Official Autodesk HTTPS package URL to download");
        var openAccountOpt = new Option<bool>("--open-account", "Open Autodesk Account when a direct package URL is unavailable");
        var outputOpt = new Option<string>("--output", () => "table", "Output format: table, json, markdown");

        var command = new Command("download", "Download an official Autodesk Revit content installer when a direct URL is available")
        {
            yearOpt,
            localeOpt,
            downloadDirOpt,
            urlOpt,
            openAccountOpt,
            outputOpt,
        };

        command.SetHandler(async (year, locale, downloadDir, url, openAccount, outputFormat) =>
        {
            Environment.ExitCode = await ExecuteDownloadAsync(
                year, locale, downloadDir, url, openAccount, outputFormat, Console.Out);
        }, yearOpt, localeOpt, downloadDirOpt, urlOpt, openAccountOpt, outputOpt);

        return command;
    }

    private static Command CreateInstallCommand()
    {
        var yearOpt = new Option<int>("--year", () => 2026, "Revit version year for post-install verification: 2024, 2025, or 2026");
        var localeOpt = new Option<string>("--locale", () => "ENU", "Autodesk content locale code for post-install verification");
        var packageOpt = new Option<string>("--package", "Official Autodesk content installer package") { IsRequired = true };
        var dryRunOpt = new Option<bool>("--dry-run", "Preview installer launch without starting it");
        var applyOpt = new Option<bool>("--apply", "Start the official content installer");
        var yesOpt = new Option<bool>("--yes", "Confirm installer launch in non-interactive mode");
        var receiptOutputOpt = new Option<string?>("--receipt-output", "Optional path to write the installer launch receipt JSON");
        var envBaselineOpt = new Option<string?>("--env-baseline", $"Environment baseline JSON to link into the receipt; defaults to {RevitEnvBaselineReceiptReferenceBuilder.DefaultBaselinePath}");
        var outputOpt = new Option<string>("--output", () => "table", "Output format: table, json, markdown");

        var command = new Command("install", "Start a downloaded Autodesk Revit content installer after confirmation")
        {
            yearOpt,
            localeOpt,
            packageOpt,
            dryRunOpt,
            applyOpt,
            yesOpt,
            receiptOutputOpt,
            envBaselineOpt,
            outputOpt,
        };

        command.SetHandler(async ctx =>
        {
            Environment.ExitCode = await ExecuteInstallAsync(
                ctx.ParseResult.GetValueForOption(packageOpt)!,
                ctx.ParseResult.GetValueForOption(dryRunOpt),
                ctx.ParseResult.GetValueForOption(applyOpt),
                ctx.ParseResult.GetValueForOption(yesOpt),
                ctx.ParseResult.GetValueForOption(outputOpt)!,
                Console.Out,
                receiptOutput: ctx.ParseResult.GetValueForOption(receiptOutputOpt),
                year: ctx.ParseResult.GetValueForOption(yearOpt),
                locale: ctx.ParseResult.GetValueForOption(localeOpt)!,
                envBaselinePath: ctx.ParseResult.GetValueForOption(envBaselineOpt));
        });

        return command;
    }

    public static async Task<int> ExecuteCheckAsync(
        int year,
        string locale,
        string? contentRoot,
        string? revitIni,
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

        var report = RevitLibraryDetector.Check(year, locale, contentRoot, revitIni);
        if (findings)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(ToFindingEnvelope(report), TerminalJsonOptions.PrettyCamel));
            return report.Success ? 0 : 1;
        }

        await WriteOutputAsync(output, report, normalizedOutput);
        return report.Success ? 0 : 1;
    }

    public static async Task<int> ExecuteRepairApplyAsync(
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

        var fullPlanPath = Path.GetFullPath(RevitLibraryPathMapper.ExpandToLocalPath(planPath));
        var report = new RevitLibraryRepairReceipt
        {
            DryRun = dryRun,
            Confirmed = yes,
            PlanPath = fullPlanPath,
        };

        if (!dryRun && !yes)
        {
            AddRepairIssue(
                report.Issues,
                "error",
                "confirmation-required",
                fullPlanPath,
                "--yes is required before applying a Revit.ini repair plan.");
            FinalizeRepairReceipt(report);
            await WriteOutputAsync(output, report, normalizedOutput);
            return 1;
        }

        if (dryRun && !string.IsNullOrWhiteSpace(receiptOutput))
        {
            AddRepairIssue(
                report.Issues,
                "error",
                "receipt-output-refused",
                receiptOutput,
                "--receipt-output is only written for an approved apply; dry-run must not create a receipt.");
            FinalizeRepairReceipt(report);
            await WriteOutputAsync(output, report, normalizedOutput);
            return 1;
        }

        RevitLibraryRepairPlanReport? plan;
        try
        {
            plan = JsonSerializer.Deserialize<RevitLibraryRepairPlanReport>(
                await File.ReadAllTextAsync(fullPlanPath),
                RepairReadOptions);
            report.PlanHash = ComputeSha256Hex(fullPlanPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            AddRepairIssue(report.Issues, "error", "plan-read-failed", fullPlanPath, ex.Message);
            FinalizeRepairReceipt(report);
            await WriteOutputAsync(output, report, normalizedOutput);
            return 1;
        }

        if (!ValidateRepairPlan(plan, report.Issues, fullPlanPath))
        {
            FinalizeRepairReceipt(report);
            await WriteOutputAsync(output, report, normalizedOutput);
            return 1;
        }

        var backupTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var action in plan!.Actions)
        {
            if (!ValidateRepairAction(action, report.Issues, fullPlanPath))
                continue;

            var targetPath = RevitLibraryPathMapper.ExpandToLocalPath(action.TargetPath);
            if (!File.Exists(targetPath))
            {
                AddRepairIssue(
                    report.Issues,
                    "error",
                    "target-missing",
                    targetPath,
                    "The target Revit.ini file no longer exists.");
                continue;
            }

            var currentExists = TryGetIniValue(targetPath, action.Section, action.Key, out var currentValue);
            if (currentExists != action.BeforeValuePresent ||
                (currentExists && !string.Equals(currentValue, action.BeforeValue, StringComparison.Ordinal)))
            {
                AddRepairIssue(
                    report.Issues,
                    "error",
                    "current-value-conflict",
                    targetPath,
                    $"Current {action.Section}.{action.Key} no longer matches the reviewed plan.");
                continue;
            }

            var beforeFileHash = ComputeSha256Hex(targetPath);
            var receiptAction = new RevitLibraryRepairReceiptAction
            {
                ActionId = action.ActionId,
                Kind = action.Kind,
                TargetPath = targetPath,
                Section = action.Section,
                Key = action.Key,
                BeforeValue = action.BeforeValue,
                BeforeValuePresent = action.BeforeValuePresent,
                AfterValue = action.AfterValue,
                BackupPath = action.BackupPath,
                RestoreValue = action.Rollback?.RestoreValue ?? action.BeforeValue,
                RestoreValuePresent = action.Rollback?.RestoreValuePresent ?? action.BeforeValuePresent,
                VerifyCommand = action.VerifyCommand,
                RollbackCommand = "",
                BeforeFileHash = beforeFileHash,
                AfterFileHash = beforeFileHash,
                Applied = false,
            };

            if (!dryRun)
            {
                try
                {
                    var backupPath = RevitLibraryPathMapper.ExpandToLocalPath(action.BackupPath);
                    if (backupTargets.Add(targetPath))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                        File.Copy(targetPath, backupPath, overwrite: true);
                    }

                    SetIniValue(targetPath, action.Section, action.Key, action.AfterValue, present: true);
                    receiptAction.BackupPath = backupPath;
                    receiptAction.AfterFileHash = ComputeSha256Hex(targetPath);
                    receiptAction.Applied = true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    AddRepairIssue(report.Issues, "error", "apply-failed", targetPath, ex.Message);
                    continue;
                }
            }

            report.Actions.Add(receiptAction);
        }

        FinalizeRepairReceipt(report);
        if (!dryRun && report.Actions.Any(action => action.Applied))
        {
            var receiptPath = Path.GetFullPath(RevitLibraryPathMapper.ExpandToLocalPath(
                string.IsNullOrWhiteSpace(receiptOutput) ? fullPlanPath + ".receipt.json" : receiptOutput));
            foreach (var action in report.Actions)
                action.RollbackCommand = $"revitcli library repair-rollback --receipt {QuoteArgument(receiptPath)} --dry-run";

            report.ReceiptPath = receiptPath;
            report.ReceiptSaved = true;
            report.EnvBaseline = RevitEnvBaselineReceiptReferenceBuilder.Capture(envBaselinePath);
            Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
            await File.WriteAllTextAsync(receiptPath, JsonSerializer.Serialize(report, TerminalJsonOptions.PrettyCamel));
        }

        await WriteOutputAsync(output, report, normalizedOutput);
        return report.Success ? 0 : 1;
    }

    public static async Task<int> ExecuteRepairRollbackAsync(
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

        var fullReceiptPath = Path.GetFullPath(RevitLibraryPathMapper.ExpandToLocalPath(receiptPath));
        var report = new RevitLibraryRepairRollbackReport
        {
            DryRun = dryRun,
            Confirmed = yes,
            ReceiptPath = fullReceiptPath,
        };

        if (!dryRun && !yes)
        {
            AddRepairIssue(
                report.Issues,
                "error",
                "confirmation-required",
                fullReceiptPath,
                "--yes is required before applying a Revit.ini repair rollback.");
            FinalizeRepairRollback(report);
            await WriteOutputAsync(output, report, normalizedOutput);
            return 1;
        }

        RevitLibraryRepairReceipt? receipt;
        try
        {
            receipt = JsonSerializer.Deserialize<RevitLibraryRepairReceipt>(
                await File.ReadAllTextAsync(fullReceiptPath),
                RepairReadOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            AddRepairIssue(report.Issues, "error", "receipt-read-failed", fullReceiptPath, ex.Message);
            FinalizeRepairRollback(report);
            await WriteOutputAsync(output, report, normalizedOutput);
            return 1;
        }

        if (receipt == null ||
            !string.Equals(receipt.SchemaVersion, RepairReceiptSchemaVersion, StringComparison.OrdinalIgnoreCase) ||
            !receipt.Actions.Any(action => action.Applied))
        {
            AddRepairIssue(
                report.Issues,
                "error",
                "invalid-receipt",
                fullReceiptPath,
                "The repair receipt is missing, failed, or has an unsupported schema version.");
            FinalizeRepairRollback(report);
            await WriteOutputAsync(output, report, normalizedOutput);
            return 1;
        }

        foreach (var action in receipt.Actions)
        {
            var currentExists = TryGetIniValue(action.TargetPath, action.Section, action.Key, out var currentValue);
            if (!currentExists || !string.Equals(currentValue, action.AfterValue, StringComparison.Ordinal))
            {
                AddRepairIssue(
                    report.Issues,
                    "error",
                    "current-value-conflict",
                    action.TargetPath,
                    $"Current {action.Section}.{action.Key} no longer matches the applied receipt.");
                continue;
            }

            var beforeFileHash = ComputeSha256Hex(action.TargetPath);
            var rollbackAction = new RevitLibraryRepairRollbackAction
            {
                ActionId = action.ActionId,
                TargetPath = action.TargetPath,
                Section = action.Section,
                Key = action.Key,
                CurrentValue = currentValue,
                RestoreValue = action.RestoreValue,
                RestoreValuePresent = action.RestoreValuePresent,
                BackupPath = action.TargetPath + ".revitcli.rollback.bak",
                BeforeFileHash = beforeFileHash,
                AfterFileHash = beforeFileHash,
                Applied = false,
            };

            if (!dryRun)
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(rollbackAction.BackupPath)!);
                    File.Copy(action.TargetPath, rollbackAction.BackupPath, overwrite: true);
                    SetIniValue(action.TargetPath, action.Section, action.Key, action.RestoreValue, action.RestoreValuePresent);
                    rollbackAction.AfterFileHash = ComputeSha256Hex(action.TargetPath);
                    rollbackAction.Applied = true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    AddRepairIssue(report.Issues, "error", "rollback-failed", action.TargetPath, ex.Message);
                    continue;
                }
            }

            report.Actions.Add(rollbackAction);
        }

        FinalizeRepairRollback(report);
        await WriteOutputAsync(output, report, normalizedOutput);
        return report.Success ? 0 : 1;
    }

    public static async Task<int> ExecuteSourcesAsync(
        int year,
        string locale,
        string outputFormat,
        TextWriter output)
    {
        if (!TerminalOutputFormat.TryNormalize(outputFormat, out var normalizedOutput, "table", "json", "markdown"))
        {
            await output.WriteLineAsync("Error: --output must be 'table', 'json', or 'markdown'.");
            return 1;
        }

        if (!RevitLibraryCatalog.IsSupportedYear(year))
        {
            await output.WriteLineAsync($"Error: unsupported Revit year {year}. Supported years: 2024, 2025, 2026.");
            return 1;
        }

        var report = RevitLibraryCatalog.CreateSourceReport(year, locale);
        await WriteOutputAsync(output, report, normalizedOutput);
        return 0;
    }

    public static async Task<int> ExecuteRepairPlanAsync(
        int year,
        string locale,
        string? contentRoot,
        string revitIni,
        string? planOutput,
        string outputFormat,
        TextWriter output)
    {
        if (!TerminalOutputFormat.TryNormalize(outputFormat, out var normalizedOutput, "table", "json", "markdown"))
        {
            await output.WriteLineAsync("Error: --output must be 'table', 'json', or 'markdown'.");
            return 1;
        }

        var report = BuildRepairPlan(year, locale, contentRoot, revitIni);
        if (!string.IsNullOrWhiteSpace(planOutput))
        {
            var planPath = Path.GetFullPath(planOutput);
            Directory.CreateDirectory(Path.GetDirectoryName(planPath)!);
            report.PlanOutputPath = planPath;
            await File.WriteAllTextAsync(planPath, JsonSerializer.Serialize(report, TerminalJsonOptions.PrettyCamel));
        }

        await WriteOutputAsync(output, report, normalizedOutput);
        return report.Success ? 0 : 1;
    }

    private static RevitLibraryRepairPlanReport BuildRepairPlan(
        int year,
        string locale,
        string? contentRoot,
        string revitIni)
    {
        var normalizedLocale = RevitLibraryCatalog.NormalizeLocale(locale);
        var revitIniPath = RevitLibraryPathMapper.ExpandToLocalPath(revitIni);
        var check = RevitLibraryDetector.Check(year, normalizedLocale, contentRoot, revitIniPath);
        var report = new RevitLibraryRepairPlanReport
        {
            RevitYear = year,
            Locale = normalizedLocale,
            ContentRoot = check.ContentRoot,
            RevitIniPath = revitIniPath,
            Check = check,
        };

        if (!File.Exists(revitIniPath))
        {
            report.Issues.Add(new RevitLibraryIssue
            {
                Severity = "error",
                Code = "revit-ini-missing",
                Path = revitIniPath,
                Message = "Revit.ini must exist before a reversible repair plan can be generated.",
                NextAction = "Run library check to locate Revit.ini or create a Revit profile by starting Revit once.",
            });
            FinalizeRepairPlan(report);
            return report;
        }

        var candidate = check.FamilyTemplatePaths
            .Where(path => path.Exists && path.RftCount > 0)
            .OrderByDescending(path => string.Equals(path.Source, "default-content-root", StringComparison.OrdinalIgnoreCase))
            .ThenBy(path => path.LocalPath, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (candidate == null)
        {
            report.Issues.Add(new RevitLibraryIssue
            {
                Severity = "error",
                Code = "family-template-candidate-missing",
                Path = check.ContentRoot,
                Message = "No existing Family Templates path with .rft files was found; Revit.ini repair would point at another missing path.",
                NextAction = $"Run `revitcli library sources --year {year} --locale {normalizedLocale}` and install the matching Autodesk content pack first.",
            });
            FinalizeRepairPlan(report);
            return report;
        }

        string? beforeValue;
        try
        {
            beforeValue = RevitIniDocument.Load(revitIniPath).GetDirectoryValue(normalizedLocale, "FamilyTemplatePath");
        }
        catch (Exception ex) when (RevitLibraryPathMapper.IsFileSystemException(ex))
        {
            report.Issues.Add(new RevitLibraryIssue
            {
                Severity = "error",
                Code = "revit-ini-unreadable",
                Path = revitIniPath,
                Message = ex.Message,
            });
            FinalizeRepairPlan(report);
            return report;
        }

        var baseDirectory = Path.GetDirectoryName(revitIniPath);
        var beforeLocalPath = string.IsNullOrWhiteSpace(beforeValue)
            ? ""
            : RevitLibraryPathMapper.ExpandToLocalPath(beforeValue, baseDirectory);
        if (string.Equals(beforeLocalPath, candidate.LocalPath, StringComparison.OrdinalIgnoreCase))
        {
            report.Issues.Add(new RevitLibraryIssue
            {
                Severity = "info",
                Code = "family-template-path-current",
                Path = revitIniPath,
                Message = "FamilyTemplatePath already points to the selected existing template path.",
            });
            FinalizeRepairPlan(report);
            return report;
        }

        var section = $"Directories{normalizedLocale}";
        var beforeValuePresent = TryGetIniValue(revitIniPath, section, "FamilyTemplatePath", out var exactBeforeValue);
        report.Actions.Add(new RevitLibraryRepairAction
        {
            ActionId = "library.revitini.family-template-path",
            Kind = "set-revit-ini-value",
            Description = "Set Revit.ini FamilyTemplatePath to an existing Autodesk Family Templates folder.",
            TargetPath = revitIniPath,
            Section = section,
            Key = "FamilyTemplatePath",
            BeforeValue = beforeValuePresent ? exactBeforeValue : "",
            BeforeValuePresent = beforeValuePresent,
            AfterValue = candidate.LocalPath,
            BackupPath = revitIniPath + ".revitcli.bak",
            Reversible = true,
            VerifyCommand = $"revitcli library check --year {year} --locale {normalizedLocale} --revit-ini {QuoteArgument(revitIniPath)} --output json",
            Rollback = new RevitLibraryRepairRollback
            {
                Kind = "restore-revit-ini-value",
                TargetPath = revitIniPath,
                Section = section,
                Key = "FamilyTemplatePath",
                RestoreValue = beforeValuePresent ? exactBeforeValue : "",
                RestoreValuePresent = beforeValuePresent,
                BackupPath = revitIniPath + ".revitcli.bak",
            },
        });
        FinalizeRepairPlan(report);
        return report;
    }

    private static bool ValidateRepairPlan(
        RevitLibraryRepairPlanReport? plan,
        List<RevitLibraryIssue> issues,
        string planPath)
    {
        if (plan == null)
        {
            AddRepairIssue(issues, "error", "invalid-plan", planPath, "Repair plan JSON could not be read.");
            return false;
        }

        if (!string.Equals(plan.SchemaVersion, RepairPlanSchemaVersion, StringComparison.OrdinalIgnoreCase))
        {
            AddRepairIssue(issues, "error", "unsupported-plan-schema", planPath, $"Expected {RepairPlanSchemaVersion}.");
            return false;
        }

        if (!plan.Success)
        {
            AddRepairIssue(issues, "error", "failed-plan", planPath, "Only successful repair plans can be applied.");
            return false;
        }

        if (plan.Actions.Count == 0)
        {
            AddRepairIssue(issues, "warning", "empty-plan", planPath, "Repair plan has no actions.");
            return true;
        }

        return true;
    }

    private static bool ValidateRepairAction(
        RevitLibraryRepairAction action,
        List<RevitLibraryIssue> issues,
        string planPath)
    {
        var errors = new List<string>();
        if (!string.Equals(action.Kind, "set-revit-ini-value", StringComparison.OrdinalIgnoreCase))
            errors.Add("kind must be set-revit-ini-value");
        if (string.IsNullOrWhiteSpace(action.TargetPath))
            errors.Add("targetPath is required");
        if (string.IsNullOrWhiteSpace(action.Section))
            errors.Add("section is required");
        if (string.IsNullOrWhiteSpace(action.Key))
            errors.Add("key is required");
        if (string.IsNullOrWhiteSpace(action.BackupPath))
            errors.Add("backupPath is required");
        if (!action.Reversible || action.Rollback == null)
            errors.Add("rollback metadata is required");

        if (errors.Count == 0)
            return true;

        AddRepairIssue(
            issues,
            "error",
            "invalid-action",
            planPath,
            $"Invalid repair action {action.ActionId}: {string.Join(", ", errors)}.");
        return false;
    }

    private static void FinalizeRepairReceipt(RevitLibraryRepairReceipt report)
    {
        report.ActionCount = report.Actions.Count;
        report.AppliedCount = report.Actions.Count(action => action.Applied);
        report.IssueCount = report.Issues.Count;
        report.Success = report.Issues.All(issue =>
            !string.Equals(issue.Severity, "error", StringComparison.OrdinalIgnoreCase));
    }

    private static void FinalizeRepairRollback(RevitLibraryRepairRollbackReport report)
    {
        report.ActionCount = report.Actions.Count;
        report.RolledBackCount = report.Actions.Count(action => action.Applied);
        report.IssueCount = report.Issues.Count;
        report.Success = report.Issues.All(issue =>
            !string.Equals(issue.Severity, "error", StringComparison.OrdinalIgnoreCase));
    }

    private static void AddRepairIssue(
        List<RevitLibraryIssue> issues,
        string severity,
        string code,
        string? path,
        string message,
        string? nextAction = null)
    {
        issues.Add(new RevitLibraryIssue
        {
            Severity = severity,
            Code = code,
            Path = path,
            Message = message,
            NextAction = nextAction,
        });
    }

    private static string ComputeSha256Hex(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool TryGetIniValue(string path, string section, string key, out string value)
    {
        value = "";
        if (!File.Exists(path))
            return false;

        var currentSection = "";
        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line.StartsWith('[') && line.EndsWith(']') && line.Length > 2)
            {
                currentSection = line[1..^1].Trim();
                continue;
            }

            if (!string.Equals(currentSection, section, StringComparison.OrdinalIgnoreCase))
                continue;

            var equals = line.IndexOf('=');
            if (equals <= 0)
                continue;

            if (!string.Equals(line[..equals].Trim(), key, StringComparison.OrdinalIgnoreCase))
                continue;

            value = line[(equals + 1)..].Trim();
            return true;
        }

        return false;
    }

    private static void SetIniValue(string path, string section, string key, string value, bool present)
    {
        var format = ReadIniFile(path);
        var lines = format.Lines.ToList();
        var sectionIndex = FindIniSection(lines, section);
        if (sectionIndex < 0)
        {
            if (!present)
                return;

            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1]))
                lines.Add("");
            lines.Add($"[{section}]");
            lines.Add($"{key}={value}");
            WriteIniFile(path, lines, format);
            return;
        }

        var nextSectionIndex = FindNextIniSection(lines, sectionIndex + 1);
        var endIndex = nextSectionIndex < 0 ? lines.Count : nextSectionIndex;
        var keyIndex = FindIniKey(lines, sectionIndex + 1, endIndex, key);
        if (!present)
        {
            if (keyIndex >= 0)
                lines.RemoveAt(keyIndex);
            WriteIniFile(path, lines, format);
            return;
        }

        if (keyIndex >= 0)
        {
            lines[keyIndex] = $"{key}={value}";
        }
        else
        {
            lines.Insert(endIndex, $"{key}={value}");
        }

        WriteIniFile(path, lines, format);
    }

    // Preserves the original Revit.ini byte encoding (UTF-16/UTF-8 BOM or none) and
    // line-ending style so a repair never silently re-encodes the file (which would
    // corrupt non-ASCII paths) or flip CRLF<->LF when run from WSL against a Windows ini.
    private static IniFileFormat ReadIniFile(string path)
    {
        if (!File.Exists(path))
            return new IniFileFormat(new List<string>(), new UTF8Encoding(false), Environment.NewLine, true);

        var bytes = File.ReadAllBytes(path);
        var encoding = DetectEncoding(bytes, out var preambleLength);
        var text = encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength);
        var newLine = text.Contains("\r\n", StringComparison.Ordinal)
            ? "\r\n"
            : text.Contains('\n')
                ? "\n"
                : Environment.NewLine;
        var trailingNewLine = text.Length > 0 && (text[^1] == '\n' || text[^1] == '\r');
        var lines = text.Length == 0
            ? new List<string>()
            : text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n').ToList();
        if (trailingNewLine && lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);
        return new IniFileFormat(lines, encoding, newLine, trailingNewLine);
    }

    private static Encoding DetectEncoding(byte[] bytes, out int preambleLength)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            preambleLength = 3;
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            preambleLength = 2;
            return new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            preambleLength = 2;
            return new UnicodeEncoding(bigEndian: true, byteOrderMark: true);
        }

        preambleLength = 0;
        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    }

    private static void WriteIniFile(string path, IReadOnlyList<string> lines, IniFileFormat format)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < lines.Count; i++)
        {
            builder.Append(lines[i]);
            if (i < lines.Count - 1 || format.TrailingNewLine)
                builder.Append(format.NewLine);
        }

        File.WriteAllText(path, builder.ToString(), format.Encoding);
    }

    private sealed record IniFileFormat(
        IReadOnlyList<string> Lines,
        Encoding Encoding,
        string NewLine,
        bool TrailingNewLine);

    private static int FindIniSection(IReadOnlyList<string> lines, string section)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith('[') &&
                line.EndsWith(']') &&
                line.Length > 2 &&
                string.Equals(line[1..^1].Trim(), section, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static int FindNextIniSection(IReadOnlyList<string> lines, int startIndex)
    {
        for (var i = startIndex; i < lines.Count; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith('[') && line.EndsWith(']') && line.Length > 2)
                return i;
        }

        return -1;
    }

    private static int FindIniKey(IReadOnlyList<string> lines, int startIndex, int endIndex, string key)
    {
        for (var i = startIndex; i < endIndex; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
                continue;

            var equals = line.IndexOf('=');
            if (equals <= 0)
                continue;

            if (string.Equals(line[..equals].Trim(), key, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    private static void FinalizeRepairPlan(RevitLibraryRepairPlanReport report)
    {
        report.ActionCount = report.Actions.Count;
        report.IssueCount = report.Issues.Count;
        report.Success = report.Issues.All(issue =>
            !string.Equals(issue.Severity, "error", StringComparison.OrdinalIgnoreCase));
    }

    public static async Task<int> ExecuteDownloadAsync(
        int year,
        string locale,
        string downloadDirectory,
        string? packageUrl,
        bool openAccount,
        string outputFormat,
        TextWriter output,
        HttpClient? httpClient = null,
        Func<string, bool>? openUrl = null)
    {
        if (!TerminalOutputFormat.TryNormalize(outputFormat, out var normalizedOutput, "table", "json", "markdown"))
        {
            await output.WriteLineAsync("Error: --output must be 'table', 'json', or 'markdown'.");
            return 1;
        }

        var report = new RevitLibraryDownloadReport
        {
            RevitYear = year,
            Locale = RevitLibraryCatalog.NormalizeLocale(locale),
            DownloadDirectory = RevitLibraryPathMapper.ExpandToLocalPath(downloadDirectory),
            Sources = RevitLibraryCatalog.CreateSources(year, locale),
        };

        if (!RevitLibraryCatalog.IsSupportedYear(year))
        {
            report.Success = false;
            report.Mode = "refused";
            report.Issues.Add(new RevitLibraryIssue
            {
                Severity = "error",
                Code = "unsupported-year",
                Message = $"Unsupported Revit year {year}. Supported years: 2024, 2025, 2026.",
            });
            await WriteOutputAsync(output, report, normalizedOutput);
            return 1;
        }

        if (string.IsNullOrWhiteSpace(packageUrl))
        {
            report.Success = false;
            report.Mode = "manual-download-required";
            report.ManualDownloadUrl = RevitLibraryCatalog.AutodeskAccountUrl;
            report.Issues.Add(new RevitLibraryIssue
            {
                Severity = "error",
                Code = "manual-auth-required",
                Message = "Autodesk does not expose a stable anonymous direct package URL for this content pack in the bundled catalog.",
                NextAction = "Sign in to Autodesk Account, open Revit > Available downloads > Libraries, then rerun with --url if you have an official Autodesk HTTPS package URL.",
            });

            if (openAccount)
                TryOpenUrl(report, RevitLibraryCatalog.AutodeskAccountUrl, openUrl);

            await WriteOutputAsync(output, report, normalizedOutput);
            return 1;
        }

        if (!RevitLibraryCatalog.IsOfficialAutodeskUrl(packageUrl))
        {
            report.Success = false;
            report.Mode = "refused";
            report.Issues.Add(new RevitLibraryIssue
            {
                Severity = "error",
                Code = "non-autodesk-url",
                Message = "Only official Autodesk HTTPS package URLs are allowed.",
            });
            await WriteOutputAsync(output, report, normalizedOutput);
            return 1;
        }

        report.PackageUrl = packageUrl.Trim();
        try
        {
            Directory.CreateDirectory(report.DownloadDirectory);
            using var ownedClient = httpClient is null ? new HttpClient() : null;
            var client = httpClient ?? ownedClient!;
            using var response = await client.GetAsync(report.PackageUrl, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
            {
                report.Success = false;
                report.Mode = "download-failed";
                report.Issues.Add(new RevitLibraryIssue
                {
                    Severity = "error",
                    Code = "download-failed",
                    Message = $"Autodesk package download returned {(int)response.StatusCode} {response.ReasonPhrase}.",
                });
                await WriteOutputAsync(output, report, normalizedOutput);
                return 1;
            }

            var fileName = ResolveDownloadFileName(response.Content.Headers.ContentDisposition, report.PackageUrl, year, report.Locale);
            var targetPath = Path.Combine(report.DownloadDirectory, fileName);
            await using (var source = await response.Content.ReadAsStreamAsync())
            await using (var target = File.Create(targetPath))
            {
                await source.CopyToAsync(target);
            }

            var info = new FileInfo(targetPath);
            report.Success = true;
            report.Mode = "downloaded";
            report.PackagePath = targetPath;
            report.Bytes = info.Length;
            await WriteOutputAsync(output, report, normalizedOutput);
            return 0;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or TaskCanceledException)
        {
            report.Success = false;
            report.Mode = "download-failed";
            report.Issues.Add(new RevitLibraryIssue
            {
                Severity = "error",
                Code = "download-failed",
                Message = ex.Message,
            });
            await WriteOutputAsync(output, report, normalizedOutput);
            return 1;
        }
    }

    public static async Task<int> ExecuteInstallAsync(
        string packagePath,
        bool dryRun,
        bool apply,
        bool yes,
        string outputFormat,
        TextWriter output,
        Func<string, bool>? launcher = null,
        string? receiptOutput = null,
        int year = 2026,
        string locale = "ENU",
        string? envBaselinePath = null)
    {
        if (!TerminalOutputFormat.TryNormalize(outputFormat, out var normalizedOutput, "table", "json", "markdown"))
        {
            await output.WriteLineAsync("Error: --output must be 'table', 'json', or 'markdown'.");
            return 1;
        }

        var normalizedLocale = RevitLibraryCatalog.NormalizeLocale(locale);
        var expandedPackagePath = RevitLibraryPathMapper.ExpandToLocalPath(packagePath);
        var report = new RevitLibraryInstallReport
        {
            RevitYear = year,
            Locale = normalizedLocale,
            PackagePath = expandedPackagePath,
            WindowsPackagePath = RevitLibraryPathMapper.ToWindowsPath(expandedPackagePath),
            DryRun = dryRun || !apply,
            ApplyRequested = apply,
            Confirmed = yes,
            VerifyCommand = $"revitcli library check --year {year} --locale {normalizedLocale} --output markdown",
        };

        if (!RevitLibraryCatalog.IsSupportedYear(year))
        {
            report.Success = false;
            report.Mode = "refused";
            report.Issues.Add(new RevitLibraryIssue
            {
                Severity = "error",
                Code = "unsupported-year",
                Message = $"Unsupported Revit year {year}. Supported years: 2024, 2025, 2026.",
            });
            await WriteOutputAsync(output, report, normalizedOutput);
            return 1;
        }

        if (dryRun && apply)
        {
            report.Success = false;
            report.Mode = "refused";
            report.Issues.Add(new RevitLibraryIssue
            {
                Severity = "error",
                Code = "conflicting-options",
                Message = "--dry-run and --apply cannot be combined.",
            });
            await WriteOutputAsync(output, report, normalizedOutput);
            return 1;
        }

        if (!apply && !string.IsNullOrWhiteSpace(receiptOutput))
        {
            report.Success = false;
            report.Mode = "refused";
            report.Issues.Add(new RevitLibraryIssue
            {
                Severity = "error",
                Code = "receipt-output-refused",
                Message = "--receipt-output is only written after --apply --yes starts the official installer; dry-runs must not create receipts.",
            });
            await WriteOutputAsync(output, report, normalizedOutput);
            return 1;
        }

        if (!File.Exists(expandedPackagePath))
        {
            report.Success = false;
            report.Mode = "missing-package";
            report.Issues.Add(new RevitLibraryIssue
            {
                Severity = "error",
                Code = "package-missing",
                Path = expandedPackagePath,
                Message = "The installer package does not exist.",
            });
            await WriteOutputAsync(output, report, normalizedOutput);
            return 1;
        }

        PopulateInstallPackageEvidence(report, expandedPackagePath);

        if (!IsSupportedInstallerPackage(expandedPackagePath))
        {
            report.Success = false;
            report.Mode = "refused";
            report.Issues.Add(new RevitLibraryIssue
            {
                Severity = "error",
                Code = "unsupported-package",
                Path = expandedPackagePath,
                Message = "Only .exe and .msi installer packages are supported.",
            });
            await WriteOutputAsync(output, report, normalizedOutput);
            return 1;
        }

        if (!apply)
        {
            report.Success = true;
            report.Mode = "dry-run";
            await WriteOutputAsync(output, report, normalizedOutput);
            return 0;
        }

        if (!yes)
        {
            report.Success = false;
            report.Mode = "refused";
            report.Issues.Add(new RevitLibraryIssue
            {
                Severity = "error",
                Code = "confirmation-required",
                Message = "--yes is required with --apply before starting the Autodesk installer.",
            });
            await WriteOutputAsync(output, report, normalizedOutput);
            return 1;
        }

        try
        {
            var started = launcher?.Invoke(expandedPackagePath) ?? StartInstaller(expandedPackagePath);
            report.Success = started;
            report.Mode = started ? "started" : "start-failed";
            report.ProcessStarted = started;
            if (!started)
            {
                report.Issues.Add(new RevitLibraryIssue
                {
                    Severity = "error",
                    Code = "installer-start-failed",
                    Message = "The installer process could not be started on this platform.",
                });
            }

            var receiptReady = !started || await TryWriteInstallReceiptAsync(report, expandedPackagePath, receiptOutput, envBaselinePath);

            await WriteOutputAsync(output, report, normalizedOutput);
            return started && receiptReady ? 0 : 1;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            report.Success = false;
            report.Mode = "start-failed";
            report.Issues.Add(new RevitLibraryIssue
            {
                Severity = "error",
                Code = "installer-start-failed",
                Message = ex.Message,
            });
            await WriteOutputAsync(output, report, normalizedOutput);
            return 1;
        }
    }

    private static void TryOpenUrl(RevitLibraryDownloadReport report, string url, Func<string, bool>? openUrl)
    {
        try
        {
            report.AccountUrlOpenAttempted = true;
            report.AccountUrlOpened = openUrl?.Invoke(url) ?? OpenUrl(url);
            if (!report.AccountUrlOpened)
            {
                report.Issues.Add(new RevitLibraryIssue
                {
                    Severity = "warning",
                    Code = "open-account-failed",
                    Message = "Could not open Autodesk Account from this environment.",
                    NextAction = url,
                });
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            report.AccountUrlOpenAttempted = true;
            report.AccountUrlOpened = false;
            report.Issues.Add(new RevitLibraryIssue
            {
                Severity = "warning",
                Code = "open-account-failed",
                Message = ex.Message,
                NextAction = url,
            });
        }
    }

    private static bool OpenUrl(string url)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList = { "/c", "start", "", url },
            });
            return true;
        }

        if (RevitLibraryPathMapper.IsRunningOnWsl())
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList = { "/c", "start", "", url },
            });
            return true;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "xdg-open",
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { url },
        });
        return true;
    }

    private static bool StartInstaller(string packagePath)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = packagePath,
                UseShellExecute = true,
            });
            return true;
        }

        if (RevitLibraryPathMapper.IsRunningOnWsl())
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList = { "/c", "start", "", RevitLibraryPathMapper.ToWindowsPath(packagePath) },
            });
            return true;
        }

        return false;
    }

    private static void PopulateInstallPackageEvidence(RevitLibraryInstallReport report, string packagePath)
    {
        try
        {
            var info = new FileInfo(packagePath);
            report.PackageBytes = info.Length;
            report.PackageSha256 = ComputeSha256Hex(packagePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            report.Issues.Add(new RevitLibraryIssue
            {
                Severity = "warning",
                Code = "package-evidence-unreadable",
                Path = packagePath,
                Message = $"Package hash/size evidence could not be read: {ex.Message}",
            });
        }
    }

    private static async Task<bool> TryWriteInstallReceiptAsync(
        RevitLibraryInstallReport report,
        string packagePath,
        string? receiptOutput,
        string? envBaselinePath)
    {
        if (string.IsNullOrWhiteSpace(receiptOutput))
            return true;

        try
        {
            var receiptPath = Path.GetFullPath(RevitLibraryPathMapper.ExpandToLocalPath(receiptOutput));
            Directory.CreateDirectory(Path.GetDirectoryName(receiptPath)!);
            report.ReceiptPath = receiptPath;
            report.ReceiptSaved = true;
            report.ReviewCommand = $"revitcli library install --package {QuoteArgument(packagePath)} --dry-run --output markdown";
            report.EnvBaseline = RevitEnvBaselineReceiptReferenceBuilder.Capture(envBaselinePath);
            await File.WriteAllTextAsync(receiptPath, JsonSerializer.Serialize(report, TerminalJsonOptions.PrettyCamel));
            return true;
        }
        catch (Exception ex) when (RevitLibraryPathMapper.IsFileSystemException(ex))
        {
            report.Success = false;
            report.Mode = "receipt-write-failed";
            report.ReceiptSaved = false;
            report.Issues.Add(new RevitLibraryIssue
            {
                Severity = "error",
                Code = "receipt-write-failed",
                Path = receiptOutput,
                Message = ex.Message,
            });
            return false;
        }
    }

    private static bool IsSupportedInstallerPackage(string packagePath)
    {
        var ext = Path.GetExtension(packagePath);
        return string.Equals(ext, ".exe", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(ext, ".msi", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveDownloadFileName(
        ContentDispositionHeaderValue? contentDisposition,
        string packageUrl,
        int year,
        string locale)
    {
        var name = contentDisposition?.FileNameStar ?? contentDisposition?.FileName;
        if (string.IsNullOrWhiteSpace(name) && Uri.TryCreate(packageUrl, UriKind.Absolute, out var uri))
            name = Path.GetFileName(uri.LocalPath);

        if (string.IsNullOrWhiteSpace(name))
            name = $"revit-content-{year}-{locale}.exe";

        name = name.Trim().Trim('"');
        foreach (var invalid in Path.GetInvalidFileNameChars())
            name = name.Replace(invalid, '_');

        return string.IsNullOrWhiteSpace(name) ? $"revit-content-{year}-{locale}.exe" : name;
    }

    private static BimOpsFindingEnvelope ToFindingEnvelope(RevitLibraryCheckReport report)
    {
        var envelope = new BimOpsFindingEnvelope
        {
            Source = "library.check",
            GeneratedAtUtc = report.GeneratedAtUtc.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            RequiresRevit = false,
            Summary = new BimOpsFindingSummary
            {
                Info = report.Issues.Count(issue => string.Equals(issue.Severity, "info", StringComparison.OrdinalIgnoreCase)),
                Warnings = report.Issues.Count(issue => string.Equals(issue.Severity, "warning", StringComparison.OrdinalIgnoreCase)),
                Errors = report.Issues.Count(issue => string.Equals(issue.Severity, "error", StringComparison.OrdinalIgnoreCase)),
                Blockers = report.Issues.Count(issue => string.Equals(issue.Severity, "blocker", StringComparison.OrdinalIgnoreCase)),
            },
        };

        var index = 1;
        foreach (var issue in report.Issues)
        {
            envelope.Findings.Add(new BimOpsFinding
            {
                FindingId = $"LIB-{index++:D3}-{NormalizeFindingToken(issue.Code)}",
                RuleId = $"library.{issue.Code}",
                Source = "library.check",
                Severity = NormalizeFindingSeverity(issue.Severity),
                Category = "library",
                Message = issue.Message,
                RequiresRevit = false,
                Fixability = BimOpsFindingSchema.FixabilityManual,
                Target = new BimOpsFindingTarget
                {
                    ArtifactPath = issue.Path ?? report.ContentRoot ?? report.RevitIniPath,
                    Category = "revit-content",
                },
                Evidence =
                {
                    new BimOpsFindingEvidence
                    {
                        Kind = "library-check",
                        Source = report.SchemaVersion,
                        Locator = issue.Path ?? report.ContentRoot ?? report.RevitIniPath,
                        Value = issue.Code,
                    },
                },
                Remediation = string.IsNullOrWhiteSpace(issue.NextAction)
                    ? null
                    : new BimOpsFindingRemediation
                    {
                        Mode = BimOpsFindingSchema.FixabilityManual,
                        VerifyCommand = $"revitcli library check --year {report.RevitYear} --locale {report.Locale} --findings --output json",
                        NextAction = issue.NextAction,
                    },
            });
        }

        return envelope;
    }

    private static string NormalizeFindingSeverity(string severity) =>
        severity.ToLowerInvariant() switch
        {
            "info" => BimOpsFindingSchema.SeverityInfo,
            "warning" => BimOpsFindingSchema.SeverityWarning,
            "error" => BimOpsFindingSchema.SeverityError,
            "blocker" => BimOpsFindingSchema.SeverityBlocker,
            _ => BimOpsFindingSchema.SeverityWarning,
        };

    private static string NormalizeFindingToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "unknown";

        var chars = value
            .Trim()
            .Select(ch => char.IsLetterOrDigit(ch) ? char.ToUpperInvariant(ch) : '-')
            .ToArray();
        var token = new string(chars).Trim('-');
        while (token.Contains("--", StringComparison.Ordinal))
            token = token.Replace("--", "-", StringComparison.Ordinal);

        return string.IsNullOrWhiteSpace(token) ? "unknown" : token;
    }

    private static async Task WriteOutputAsync(TextWriter output, object report, string outputFormat)
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
            RevitLibraryCheckReport check => RenderCheckTable(check),
            RevitLibrarySourceReport sources => RenderSourcesTable(sources),
            RevitLibraryDownloadReport download => RenderDownloadTable(download),
            RevitLibraryInstallReport install => RenderInstallTable(install),
            RevitLibraryRepairPlanReport repair => RenderRepairPlanTable(repair),
            RevitLibraryRepairReceipt repairReceipt => RenderRepairReceiptTable(repairReceipt),
            RevitLibraryRepairRollbackReport rollback => RenderRepairRollbackTable(rollback),
            _ => report.ToString() ?? "",
        };

    private static string RenderMarkdown(object report) =>
        report switch
        {
            RevitLibraryCheckReport check => RenderCheckMarkdown(check),
            RevitLibrarySourceReport sources => RenderSourcesMarkdown(sources),
            RevitLibraryDownloadReport download => RenderDownloadMarkdown(download),
            RevitLibraryInstallReport install => RenderInstallMarkdown(install),
            RevitLibraryRepairPlanReport repair => RenderRepairPlanMarkdown(repair),
            RevitLibraryRepairReceipt repairReceipt => RenderRepairReceiptMarkdown(repairReceipt),
            RevitLibraryRepairRollbackReport rollback => RenderRepairRollbackMarkdown(rollback),
            _ => report.ToString() ?? "",
        };

    private static string RenderCheckTable(RevitLibraryCheckReport report)
    {
        var lines = new List<string>
        {
            $"Revit library check ({report.SchemaVersion})",
            $"Year: {report.RevitYear}",
            $"Locale: {report.Locale}",
            $"Status: {(report.Success ? "OK" : "MISSING")}",
            $"Content root: {report.ContentRoot}",
            $"Revit.ini: {report.RevitIniPath ?? "(not found)"}",
            $"Library .rfa files: {report.Summary.FamilyFileCount}",
            $"Family template .rft files: {report.Summary.TemplateFileCount}",
            "",
            "Paths:",
            $"{"Kind",-16} {"Exists",-7} {"RFA",-5} {"RFT",-5} {"Source",-20} Path",
        };

        foreach (var path in report.LibraryPaths.Concat(report.FamilyTemplatePaths))
        {
            lines.Add(
                $"{path.Kind,-16} {(path.Exists ? "yes" : "no"),-7} {path.RfaCount,-5} {path.RftCount,-5} {path.Source,-20} {path.LocalPath}");
        }

        AppendIssues(lines, report.Issues);
        if (!report.Success)
        {
            lines.Add("");
            lines.Add("Next:");
            lines.Add($"  revitcli library sources --year {report.RevitYear} --locale {report.Locale}");
            lines.Add($"  revitcli library download --year {report.RevitYear} --locale {report.Locale} --download-dir {RevitLibraryPathMapper.DefaultDownloadDirectory()}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string RenderSourcesTable(RevitLibrarySourceReport report)
    {
        var lines = new List<string>
        {
            $"Revit library sources ({report.SchemaVersion})",
            $"Year: {report.RevitYear}",
            $"Locale: {report.Locale}",
            $"Direct package URL bundled: {(string.IsNullOrWhiteSpace(report.Package.DirectDownloadUrl) ? "no" : "yes")}",
            $"Manual download: {report.Package.ManualDownloadUrl}",
            "",
            "Official sources:",
        };

        foreach (var source in report.Sources)
            lines.Add($"  - {source.Id}: {source.Url}");

        lines.Add("");
        lines.Add("Manual steps:");
        foreach (var step in report.Package.ManualSteps)
            lines.Add($"  - {step}");

        return string.Join(Environment.NewLine, lines);
    }

    private static string RenderDownloadTable(RevitLibraryDownloadReport report)
    {
        var lines = new List<string>
        {
            $"Revit library download ({report.SchemaVersion})",
            $"Mode: {report.Mode}",
            $"Status: {(report.Success ? "OK" : "FAILED")}",
            $"Download directory: {report.DownloadDirectory}",
        };

        if (!string.IsNullOrWhiteSpace(report.PackageUrl))
            lines.Add($"Package URL: {report.PackageUrl}");
        if (!string.IsNullOrWhiteSpace(report.PackagePath))
            lines.Add($"Package path: {report.PackagePath} ({FormatBytes(report.Bytes)})");
        if (!string.IsNullOrWhiteSpace(report.ManualDownloadUrl))
            lines.Add($"Manual download: {report.ManualDownloadUrl}");

        AppendIssues(lines, report.Issues);
        return string.Join(Environment.NewLine, lines);
    }

    private static string RenderInstallTable(RevitLibraryInstallReport report)
    {
        var lines = new List<string>
        {
            $"Revit library install ({report.SchemaVersion})",
            $"Mode: {report.Mode}",
            $"Status: {(report.Success ? "OK" : "FAILED")}",
            $"Revit year: {report.RevitYear}",
            $"Locale: {report.Locale}",
            $"Package: {report.PackagePath}",
            $"Windows package: {report.WindowsPackagePath}",
            $"Package bytes: {report.PackageBytes}",
            $"Package SHA256: {report.PackageSha256}",
            $"Process started: {(report.ProcessStarted ? "yes" : "no")}",
            $"Receipt saved: {(report.ReceiptSaved ? "yes" : "no")}",
        };
        if (!string.IsNullOrWhiteSpace(report.ReceiptPath))
            lines.Add($"Receipt: {report.ReceiptPath}");
        AppendEnvBaselineTable(lines, report.EnvBaseline);
        if (!string.IsNullOrWhiteSpace(report.VerifyCommand))
            lines.Add($"Verify after install: {report.VerifyCommand}");
        if (!string.IsNullOrWhiteSpace(report.ReviewCommand))
            lines.Add($"Review command: {report.ReviewCommand}");

        if (report.Mode == "dry-run")
            lines.Add("Re-run with --apply --yes to start the official Autodesk installer.");

        AppendIssues(lines, report.Issues);
        return string.Join(Environment.NewLine, lines);
    }

    private static string RenderRepairPlanTable(RevitLibraryRepairPlanReport report)
    {
        var lines = new List<string>
        {
            $"Revit library repair plan ({report.SchemaVersion})",
            $"Status: {(report.Success ? "OK" : "FAILED")}",
            $"Revit.ini: {report.RevitIniPath}",
            $"Actions: {report.ActionCount}",
        };

        if (!string.IsNullOrWhiteSpace(report.PlanOutputPath))
            lines.Add($"Plan output: {report.PlanOutputPath}");

        foreach (var action in report.Actions)
        {
            lines.Add($"  {action.ActionId}: {action.Section}.{action.Key}");
            lines.Add($"    before: {action.BeforeValue}");
            lines.Add($"    after:  {action.AfterValue}");
            lines.Add($"    backup: {action.BackupPath}");
        }

        AppendIssues(lines, report.Issues);
        return string.Join(Environment.NewLine, lines);
    }

    private static string RenderRepairReceiptTable(RevitLibraryRepairReceipt report)
    {
        var lines = new List<string>
        {
            $"Revit library repair receipt ({report.SchemaVersion})",
            $"Status: {(report.Success ? "OK" : "FAILED")}",
            $"Mode: {(report.DryRun ? "dry-run" : "apply")}",
            $"Plan: {report.PlanPath}",
            $"Actions: {report.ActionCount}",
            $"Applied: {report.AppliedCount}",
        };

        if (!string.IsNullOrWhiteSpace(report.ReceiptPath))
            lines.Add($"Receipt: {report.ReceiptPath}");
        AppendEnvBaselineTable(lines, report.EnvBaseline);

        foreach (var action in report.Actions)
        {
            lines.Add($"  {action.ActionId}: {action.Section}.{action.Key}");
            lines.Add($"    before: {action.BeforeValue}");
            lines.Add($"    after:  {action.AfterValue}");
            lines.Add($"    backup: {action.BackupPath}");
        }

        AppendIssues(lines, report.Issues);
        return string.Join(Environment.NewLine, lines);
    }

    private static string RenderRepairRollbackTable(RevitLibraryRepairRollbackReport report)
    {
        var lines = new List<string>
        {
            $"Revit library repair rollback ({report.SchemaVersion})",
            $"Status: {(report.Success ? "OK" : "FAILED")}",
            $"Mode: {(report.DryRun ? "dry-run" : "apply")}",
            $"Receipt: {report.ReceiptPath}",
            $"Actions: {report.ActionCount}",
            $"Rolled back: {report.RolledBackCount}",
        };

        foreach (var action in report.Actions)
        {
            lines.Add($"  {action.ActionId}: {action.Section}.{action.Key}");
            lines.Add($"    current: {action.CurrentValue}");
            lines.Add($"    restore: {(action.RestoreValuePresent ? action.RestoreValue : "(remove key)")}");
            lines.Add($"    backup:  {action.BackupPath}");
        }

        AppendIssues(lines, report.Issues);
        return string.Join(Environment.NewLine, lines);
    }

    private static string RenderCheckMarkdown(RevitLibraryCheckReport report)
    {
        var lines = new List<string>
        {
            "# Revit Library Check",
            "",
            $"- Schema: `{report.SchemaVersion}`",
            $"- Year: `{report.RevitYear}`",
            $"- Locale: `{EscapeMarkdown(report.Locale)}`",
            $"- Status: `{(report.Success ? "ok" : "missing")}`",
            $"- Library `.rfa` files: `{report.Summary.FamilyFileCount}`",
            $"- Family template `.rft` files: `{report.Summary.TemplateFileCount}`",
            "",
            "## Paths",
            "",
            "| Kind | Exists | RFA | RFT | Source | Path |",
            "| --- | --- | ---: | ---: | --- | --- |",
        };

        foreach (var path in report.LibraryPaths.Concat(report.FamilyTemplatePaths))
        {
            lines.Add(
                $"| `{EscapeMarkdown(path.Kind)}` | `{path.Exists.ToString().ToLowerInvariant()}` | {path.RfaCount} | {path.RftCount} | `{EscapeMarkdown(path.Source)}` | `{EscapeMarkdown(path.LocalPath)}` |");
        }

        AppendIssuesMarkdown(lines, report.Issues);
        return string.Join(Environment.NewLine, lines);
    }

    private static string RenderSourcesMarkdown(RevitLibrarySourceReport report)
    {
        var lines = new List<string>
        {
            "# Revit Library Sources",
            "",
            $"- Schema: `{report.SchemaVersion}`",
            $"- Year: `{report.RevitYear}`",
            $"- Locale: `{EscapeMarkdown(report.Locale)}`",
            $"- Direct package URL bundled: `{(!string.IsNullOrWhiteSpace(report.Package.DirectDownloadUrl)).ToString().ToLowerInvariant()}`",
            "",
            "## Official Sources",
            "",
            "| Id | Kind | Sign-in | URL |",
            "| --- | --- | --- | --- |",
        };

        foreach (var source in report.Sources)
        {
            lines.Add(
                $"| `{EscapeMarkdown(source.Id)}` | `{EscapeMarkdown(source.Kind)}` | `{source.RequiresAutodeskSignIn.ToString().ToLowerInvariant()}` | {EscapeMarkdown(source.Url)} |");
        }

        lines.Add("");
        lines.Add("## Manual Steps");
        lines.Add("");
        foreach (var step in report.Package.ManualSteps)
            lines.Add($"- {EscapeMarkdown(step)}");

        return string.Join(Environment.NewLine, lines);
    }

    private static string RenderDownloadMarkdown(RevitLibraryDownloadReport report)
    {
        var lines = new List<string>
        {
            "# Revit Library Download",
            "",
            $"- Schema: `{report.SchemaVersion}`",
            $"- Mode: `{EscapeMarkdown(report.Mode)}`",
            $"- Success: `{report.Success.ToString().ToLowerInvariant()}`",
            $"- Download directory: `{EscapeMarkdown(report.DownloadDirectory)}`",
        };

        if (!string.IsNullOrWhiteSpace(report.PackagePath))
            lines.Add($"- Package path: `{EscapeMarkdown(report.PackagePath)}`");

        AppendIssuesMarkdown(lines, report.Issues);
        return string.Join(Environment.NewLine, lines);
    }

    private static string RenderInstallMarkdown(RevitLibraryInstallReport report)
    {
        var lines = new List<string>
        {
            "# Revit Library Install",
            "",
            $"- Schema: `{report.SchemaVersion}`",
            $"- Mode: `{EscapeMarkdown(report.Mode)}`",
            $"- Success: `{report.Success.ToString().ToLowerInvariant()}`",
            $"- Revit year: `{report.RevitYear}`",
            $"- Locale: `{EscapeMarkdown(report.Locale)}`",
            $"- Package: `{EscapeMarkdown(report.PackagePath)}`",
            $"- Windows package: `{EscapeMarkdown(report.WindowsPackagePath)}`",
            $"- Package bytes: `{report.PackageBytes}`",
            $"- Package SHA256: `{EscapeMarkdown(report.PackageSha256)}`",
            $"- Process started: `{report.ProcessStarted.ToString().ToLowerInvariant()}`",
            $"- Receipt saved: `{report.ReceiptSaved.ToString().ToLowerInvariant()}`",
        };
        if (!string.IsNullOrWhiteSpace(report.ReceiptPath))
            lines.Add($"- Receipt: `{EscapeMarkdown(report.ReceiptPath)}`");
        AppendEnvBaselineMarkdown(lines, report.EnvBaseline);
        if (!string.IsNullOrWhiteSpace(report.VerifyCommand))
            lines.Add($"- Verify after install: `{EscapeMarkdown(report.VerifyCommand)}`");
        if (!string.IsNullOrWhiteSpace(report.ReviewCommand))
            lines.Add($"- Review command: `{EscapeMarkdown(report.ReviewCommand)}`");

        AppendIssuesMarkdown(lines, report.Issues);
        return string.Join(Environment.NewLine, lines);
    }

    private static string RenderRepairPlanMarkdown(RevitLibraryRepairPlanReport report)
    {
        var lines = new List<string>
        {
            "# Revit Library Repair Plan",
            "",
            $"- Schema: `{report.SchemaVersion}`",
            $"- Success: `{report.Success.ToString().ToLowerInvariant()}`",
            $"- Revit.ini: `{EscapeMarkdown(report.RevitIniPath)}`",
            $"- Actions: `{report.ActionCount}`",
        };

        if (!string.IsNullOrWhiteSpace(report.PlanOutputPath))
            lines.Add($"- Plan output: `{EscapeMarkdown(report.PlanOutputPath)}`");

        lines.Add("");
        lines.Add("## Actions");
        if (report.Actions.Count == 0)
        {
            lines.Add("- None.");
        }
        else
        {
            lines.Add("| Action | Section | Key | Before | After | Backup |");
            lines.Add("| --- | --- | --- | --- | --- | --- |");
            foreach (var action in report.Actions)
            {
                lines.Add(
                    $"| `{EscapeMarkdown(action.ActionId)}` | `{EscapeMarkdown(action.Section)}` | `{EscapeMarkdown(action.Key)}` | `{EscapeMarkdown(action.BeforeValue)}` | `{EscapeMarkdown(action.AfterValue)}` | `{EscapeMarkdown(action.BackupPath)}` |");
            }
        }

        AppendIssuesMarkdown(lines, report.Issues);
        return string.Join(Environment.NewLine, lines);
    }

    private static string RenderRepairReceiptMarkdown(RevitLibraryRepairReceipt report)
    {
        var lines = new List<string>
        {
            "# Revit Library Repair Receipt",
            "",
            $"- Schema: `{report.SchemaVersion}`",
            $"- Success: `{report.Success.ToString().ToLowerInvariant()}`",
            $"- Mode: `{(report.DryRun ? "dry-run" : "apply")}`",
            $"- Plan: `{EscapeMarkdown(report.PlanPath)}`",
            $"- Actions: `{report.ActionCount}`",
            $"- Applied: `{report.AppliedCount}`",
        };

        if (!string.IsNullOrWhiteSpace(report.ReceiptPath))
            lines.Add($"- Receipt: `{EscapeMarkdown(report.ReceiptPath)}`");
        AppendEnvBaselineMarkdown(lines, report.EnvBaseline);

        lines.Add("");
        lines.Add("## Actions");
        if (report.Actions.Count == 0)
        {
            lines.Add("- None.");
        }
        else
        {
            lines.Add("| Action | Section | Key | Before | After | Backup |");
            lines.Add("| --- | --- | --- | --- | --- | --- |");
            foreach (var action in report.Actions)
            {
                lines.Add(
                    $"| `{EscapeMarkdown(action.ActionId)}` | `{EscapeMarkdown(action.Section)}` | `{EscapeMarkdown(action.Key)}` | `{EscapeMarkdown(action.BeforeValue)}` | `{EscapeMarkdown(action.AfterValue)}` | `{EscapeMarkdown(action.BackupPath)}` |");
            }
        }

        AppendIssuesMarkdown(lines, report.Issues);
        return string.Join(Environment.NewLine, lines);
    }

    private static void AppendEnvBaselineTable(List<string> lines, RevitEnvBaselineReceiptReference? reference)
    {
        if (reference == null)
            return;

        lines.Add($"Env baseline: {reference.Status} ({reference.Path})");
        if (!string.IsNullOrWhiteSpace(reference.Sha256))
            lines.Add($"Env baseline SHA256: {reference.Sha256}");
        if (!string.IsNullOrWhiteSpace(reference.Issue))
            lines.Add($"Env baseline issue: {reference.Issue}");
        if (!string.IsNullOrWhiteSpace(reference.CaptureCommand) &&
            !string.Equals(reference.Status, "linked", StringComparison.OrdinalIgnoreCase))
        {
            lines.Add($"Env baseline capture: {reference.CaptureCommand}");
        }
    }

    private static void AppendEnvBaselineMarkdown(List<string> lines, RevitEnvBaselineReceiptReference? reference)
    {
        if (reference == null)
            return;

        lines.Add($"- Env baseline: `{EscapeMarkdown(reference.Status)}` `{EscapeMarkdown(reference.Path)}`");
        if (!string.IsNullOrWhiteSpace(reference.Sha256))
            lines.Add($"- Env baseline SHA256: `{EscapeMarkdown(reference.Sha256)}`");
        if (!string.IsNullOrWhiteSpace(reference.Issue))
            lines.Add($"- Env baseline issue: {EscapeMarkdown(reference.Issue)}");
        if (!string.IsNullOrWhiteSpace(reference.CaptureCommand) &&
            !string.Equals(reference.Status, "linked", StringComparison.OrdinalIgnoreCase))
        {
            lines.Add($"- Env baseline capture: `{EscapeMarkdown(reference.CaptureCommand)}`");
        }
    }

    private static string RenderRepairRollbackMarkdown(RevitLibraryRepairRollbackReport report)
    {
        var lines = new List<string>
        {
            "# Revit Library Repair Rollback",
            "",
            $"- Schema: `{report.SchemaVersion}`",
            $"- Success: `{report.Success.ToString().ToLowerInvariant()}`",
            $"- Mode: `{(report.DryRun ? "dry-run" : "apply")}`",
            $"- Receipt: `{EscapeMarkdown(report.ReceiptPath)}`",
            $"- Actions: `{report.ActionCount}`",
            $"- Rolled back: `{report.RolledBackCount}`",
            "",
            "## Actions",
        };

        if (report.Actions.Count == 0)
        {
            lines.Add("- None.");
        }
        else
        {
            lines.Add("| Action | Section | Key | Current | Restore | Backup |");
            lines.Add("| --- | --- | --- | --- | --- | --- |");
            foreach (var action in report.Actions)
            {
                var restore = action.RestoreValuePresent ? action.RestoreValue : "(remove key)";
                lines.Add(
                    $"| `{EscapeMarkdown(action.ActionId)}` | `{EscapeMarkdown(action.Section)}` | `{EscapeMarkdown(action.Key)}` | `{EscapeMarkdown(action.CurrentValue)}` | `{EscapeMarkdown(restore)}` | `{EscapeMarkdown(action.BackupPath)}` |");
            }
        }

        AppendIssuesMarkdown(lines, report.Issues);
        return string.Join(Environment.NewLine, lines);
    }

    private static void AppendIssues(List<string> lines, IReadOnlyCollection<RevitLibraryIssue> issues)
    {
        if (issues.Count == 0)
            return;

        lines.Add("");
        lines.Add("Issues:");
        foreach (var issue in issues)
            lines.Add($"  {issue.Severity}: {issue.Code}: {issue.Message}");
    }

    private static void AppendIssuesMarkdown(List<string> lines, IReadOnlyCollection<RevitLibraryIssue> issues)
    {
        if (issues.Count == 0)
            return;

        lines.Add("");
        lines.Add("## Issues");
        lines.Add("");
        lines.Add("| Severity | Code | Message | Next action |");
        lines.Add("| --- | --- | --- | --- |");
        foreach (var issue in issues)
        {
            lines.Add(
                $"| `{EscapeMarkdown(issue.Severity)}` | `{EscapeMarkdown(issue.Code)}` | {EscapeMarkdown(issue.Message)} | {EscapeMarkdown(issue.NextAction ?? "")} |");
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} {units[unit]}" : $"{value:0.##} {units[unit]}";
    }

    private static string EscapeMarkdown(string value) =>
        value.Replace("|", "\\|", StringComparison.Ordinal);

    private static string QuoteArgument(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "\"\"";

        return value.Any(char.IsWhiteSpace) || value.Contains('"', StringComparison.Ordinal)
            ? "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\""
            : value;
    }
}

public sealed class RevitLibraryDownloadReport
{
    public string SchemaVersion { get; init; } = "revit-library-download.v1";
    public DateTimeOffset GeneratedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public bool Success { get; set; }
    public string Mode { get; set; } = "download";
    public int RevitYear { get; init; }
    public string Locale { get; init; } = "ENU";
    public string DownloadDirectory { get; init; } = "";
    public string? PackageUrl { get; set; }
    public string? PackagePath { get; set; }
    public long Bytes { get; set; }
    public string? ManualDownloadUrl { get; set; }
    public bool AccountUrlOpenAttempted { get; set; }
    public bool AccountUrlOpened { get; set; }
    public RevitLibrarySource[] Sources { get; init; } = Array.Empty<RevitLibrarySource>();
    public List<RevitLibraryIssue> Issues { get; } = new();
}

public sealed class RevitLibraryInstallReport
{
    public string SchemaVersion { get; init; } = "revit-library-install.v1";
    public DateTimeOffset GeneratedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public bool Success { get; set; }
    public string Mode { get; set; } = "install";
    public int RevitYear { get; init; }
    public string Locale { get; init; } = "ENU";
    public string PackagePath { get; init; } = "";
    public string WindowsPackagePath { get; init; } = "";
    public long PackageBytes { get; set; }
    public string PackageSha256 { get; set; } = "";
    public bool DryRun { get; init; }
    public bool ApplyRequested { get; init; }
    public bool Confirmed { get; init; }
    public bool ProcessStarted { get; set; }
    public bool ReceiptSaved { get; set; }
    public string? ReceiptPath { get; set; }
    public RevitEnvBaselineReceiptReference? EnvBaseline { get; set; }
    public string VerifyCommand { get; set; } = "";
    public string ReviewCommand { get; set; } = "";
    public List<RevitLibraryIssue> Issues { get; } = new();
}

public sealed class RevitLibraryRepairPlanReport
{
    public string SchemaVersion { get; init; } = "revit-library-repair-plan.v1";
    public DateTimeOffset GeneratedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public bool Success { get; set; }
    public int RevitYear { get; init; }
    public string Locale { get; init; } = "ENU";
    public string? ContentRoot { get; init; }
    public string RevitIniPath { get; init; } = "";
    public string? PlanOutputPath { get; set; }
    public int ActionCount { get; set; }
    public int IssueCount { get; set; }
    public RevitLibraryCheckReport? Check { get; init; }
    public List<RevitLibraryRepairAction> Actions { get; set; } = new();
    public List<RevitLibraryIssue> Issues { get; set; } = new();
}

public sealed class RevitLibraryRepairAction
{
    public string ActionId { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Description { get; init; } = "";
    public string TargetPath { get; init; } = "";
    public string Section { get; init; } = "";
    public string Key { get; init; } = "";
    public string BeforeValue { get; init; } = "";
    public bool BeforeValuePresent { get; init; } = true;
    public string AfterValue { get; init; } = "";
    public string BackupPath { get; init; } = "";
    public bool Reversible { get; init; }
    public string VerifyCommand { get; init; } = "";
    public RevitLibraryRepairRollback? Rollback { get; init; }
}

public sealed class RevitLibraryRepairRollback
{
    public string Kind { get; init; } = "";
    public string TargetPath { get; init; } = "";
    public string Section { get; init; } = "";
    public string Key { get; init; } = "";
    public string RestoreValue { get; init; } = "";
    public bool RestoreValuePresent { get; init; } = true;
    public string BackupPath { get; init; } = "";
}

public sealed class RevitLibraryRepairReceipt
{
    public string SchemaVersion { get; init; } = "revit-library-repair-receipt.v1";
    public DateTimeOffset GeneratedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public bool Success { get; set; }
    public bool DryRun { get; init; }
    public bool Confirmed { get; init; }
    public string PlanPath { get; init; } = "";
    public string PlanHash { get; set; } = "";
    public string? ReceiptPath { get; set; }
    public bool ReceiptSaved { get; set; }
    public int ActionCount { get; set; }
    public int AppliedCount { get; set; }
    public int IssueCount { get; set; }
    public RevitEnvBaselineReceiptReference? EnvBaseline { get; set; }
    public List<RevitLibraryRepairReceiptAction> Actions { get; set; } = new();
    public List<RevitLibraryIssue> Issues { get; set; } = new();
}

public sealed class RevitLibraryRepairReceiptAction
{
    public string ActionId { get; init; } = "";
    public string Kind { get; init; } = "";
    public string TargetPath { get; init; } = "";
    public string Section { get; init; } = "";
    public string Key { get; init; } = "";
    public string BeforeValue { get; init; } = "";
    public bool BeforeValuePresent { get; init; }
    public string AfterValue { get; init; } = "";
    public string BackupPath { get; set; } = "";
    public string RestoreValue { get; init; } = "";
    public bool RestoreValuePresent { get; init; }
    public string VerifyCommand { get; init; } = "";
    public string RollbackCommand { get; set; } = "";
    public string BeforeFileHash { get; init; } = "";
    public string AfterFileHash { get; set; } = "";
    public bool Applied { get; set; }
}

public sealed class RevitLibraryRepairRollbackReport
{
    public string SchemaVersion { get; init; } = "revit-library-repair-rollback.v1";
    public DateTimeOffset GeneratedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public bool Success { get; set; }
    public bool DryRun { get; init; }
    public bool Confirmed { get; init; }
    public string ReceiptPath { get; init; } = "";
    public int ActionCount { get; set; }
    public int RolledBackCount { get; set; }
    public int IssueCount { get; set; }
    public List<RevitLibraryRepairRollbackAction> Actions { get; set; } = new();
    public List<RevitLibraryIssue> Issues { get; set; } = new();
}

public sealed class RevitLibraryRepairRollbackAction
{
    public string ActionId { get; init; } = "";
    public string TargetPath { get; init; } = "";
    public string Section { get; init; } = "";
    public string Key { get; init; } = "";
    public string CurrentValue { get; init; } = "";
    public string RestoreValue { get; init; } = "";
    public bool RestoreValuePresent { get; init; }
    public string BackupPath { get; init; } = "";
    public string BeforeFileHash { get; init; } = "";
    public string AfterFileHash { get; set; } = "";
    public bool Applied { get; set; }
}
