using System.CommandLine;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text.Json;
using RevitCli.Libraries;
using RevitCli.Output;

namespace RevitCli.Commands;

public static class LibraryCommand
{
    public static Command Create()
    {
        var command = new Command("library", "Find, verify, and fetch Autodesk Revit content libraries");
        command.AddCommand(CreateCheckCommand());
        command.AddCommand(CreateSourcesCommand());
        command.AddCommand(CreateDownloadCommand());
        command.AddCommand(CreateInstallCommand());
        return command;
    }

    private static Command CreateCheckCommand()
    {
        var yearOpt = new Option<int>("--year", () => 2026, "Revit version year: 2024, 2025, or 2026");
        var localeOpt = new Option<string>("--locale", () => "ENU", "Autodesk content locale code such as ENU or CHS");
        var contentRootOpt = new Option<string?>("--content-root", "Override the Revit content root path");
        var revitIniOpt = new Option<string?>("--revit-ini", "Override the Revit.ini path to inspect");
        var outputOpt = new Option<string>("--output", () => "table", "Output format: table, json, markdown");

        var command = new Command("check", "Check local Revit library and family template content")
        {
            yearOpt,
            localeOpt,
            contentRootOpt,
            revitIniOpt,
            outputOpt,
        };

        command.SetHandler(async (year, locale, contentRoot, revitIni, outputFormat) =>
        {
            Environment.ExitCode = await ExecuteCheckAsync(
                year, locale, contentRoot, revitIni, outputFormat, Console.Out);
        }, yearOpt, localeOpt, contentRootOpt, revitIniOpt, outputOpt);

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
        var packageOpt = new Option<string>("--package", "Official Autodesk content installer package") { IsRequired = true };
        var dryRunOpt = new Option<bool>("--dry-run", "Preview installer launch without starting it");
        var applyOpt = new Option<bool>("--apply", "Start the official content installer");
        var yesOpt = new Option<bool>("--yes", "Confirm installer launch in non-interactive mode");
        var outputOpt = new Option<string>("--output", () => "table", "Output format: table, json, markdown");

        var command = new Command("install", "Start a downloaded Autodesk Revit content installer after confirmation")
        {
            packageOpt,
            dryRunOpt,
            applyOpt,
            yesOpt,
            outputOpt,
        };

        command.SetHandler(async (packagePath, dryRun, apply, yes, outputFormat) =>
        {
            Environment.ExitCode = await ExecuteInstallAsync(
                packagePath, dryRun, apply, yes, outputFormat, Console.Out);
        }, packageOpt, dryRunOpt, applyOpt, yesOpt, outputOpt);

        return command;
    }

    public static async Task<int> ExecuteCheckAsync(
        int year,
        string locale,
        string? contentRoot,
        string? revitIni,
        string outputFormat,
        TextWriter output)
    {
        if (!TerminalOutputFormat.TryNormalize(outputFormat, out var normalizedOutput, "table", "json", "markdown"))
        {
            await output.WriteLineAsync("Error: --output must be 'table', 'json', or 'markdown'.");
            return 1;
        }

        var report = RevitLibraryDetector.Check(year, locale, contentRoot, revitIni);
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
        Func<string, bool>? launcher = null)
    {
        if (!TerminalOutputFormat.TryNormalize(outputFormat, out var normalizedOutput, "table", "json", "markdown"))
        {
            await output.WriteLineAsync("Error: --output must be 'table', 'json', or 'markdown'.");
            return 1;
        }

        var expandedPackagePath = RevitLibraryPathMapper.ExpandToLocalPath(packagePath);
        var report = new RevitLibraryInstallReport
        {
            PackagePath = expandedPackagePath,
            WindowsPackagePath = RevitLibraryPathMapper.ToWindowsPath(expandedPackagePath),
            DryRun = dryRun || !apply,
            ApplyRequested = apply,
            Confirmed = yes,
        };

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

            await WriteOutputAsync(output, report, normalizedOutput);
            return started ? 0 : 1;
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
            _ => report.ToString() ?? "",
        };

    private static string RenderMarkdown(object report) =>
        report switch
        {
            RevitLibraryCheckReport check => RenderCheckMarkdown(check),
            RevitLibrarySourceReport sources => RenderSourcesMarkdown(sources),
            RevitLibraryDownloadReport download => RenderDownloadMarkdown(download),
            RevitLibraryInstallReport install => RenderInstallMarkdown(install),
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
            $"Package: {report.PackagePath}",
            $"Windows package: {report.WindowsPackagePath}",
            $"Process started: {(report.ProcessStarted ? "yes" : "no")}",
        };

        if (report.Mode == "dry-run")
            lines.Add("Re-run with --apply --yes to start the official Autodesk installer.");

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
            $"- Package: `{EscapeMarkdown(report.PackagePath)}`",
            $"- Windows package: `{EscapeMarkdown(report.WindowsPackagePath)}`",
            $"- Process started: `{report.ProcessStarted.ToString().ToLowerInvariant()}`",
        };

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
    public string PackagePath { get; init; } = "";
    public string WindowsPackagePath { get; init; } = "";
    public bool DryRun { get; init; }
    public bool ApplyRequested { get; init; }
    public bool Confirmed { get; init; }
    public bool ProcessStarted { get; set; }
    public List<RevitLibraryIssue> Issues { get; } = new();
}
