using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RevitCli.Addins;
using RevitCli.Diagnostics;
using RevitCli.Libraries;
using RevitCli.Output;

namespace RevitCli.Commands;

public static class EnvCommand
{
    public const string BaselineSchemaVersion = "revit-env-baseline.v1";

    public static Command Create()
    {
        var command = new Command("env", "Capture local Revit machine baseline evidence");
        command.AddCommand(CreateBaselineCommand());
        return command;
    }

    private static Command CreateBaselineCommand()
    {
        var yearsOpt = new Option<string>("--years", () => "2024,2025,2026", "Comma-separated Revit years to inspect");
        var localeOpt = new Option<string>("--locale", () => "ENU", "Autodesk content locale code such as ENU or CHS");
        var contentRootOpt = new Option<string?>("--content-root", "Override the Revit content root path");
        var revitIniOpt = new Option<string?>("--revit-ini", "Override the Revit.ini path to inspect");
        var allUsersRootOpt = new Option<string?>("--all-users-root", "Override the all-users add-in root");
        var perUserRootOpt = new Option<string?>("--per-user-root", "Override the per-user add-in root");
        var outOpt = new Option<string?>("--out", "Optional path to write the baseline JSON");
        var outputOpt = new Option<string>("--output", () => "table", "Output format: table, json, markdown");

        var command = new Command("baseline", "Capture a read-only local Revit environment baseline")
        {
            yearsOpt,
            localeOpt,
            contentRootOpt,
            revitIniOpt,
            allUsersRootOpt,
            perUserRootOpt,
            outOpt,
            outputOpt,
        };

        command.SetHandler(async (years, locale, contentRoot, revitIni, allUsersRoot, perUserRoot, outPath, outputFormat) =>
        {
            Environment.ExitCode = await ExecuteBaselineAsync(
                years,
                locale,
                contentRoot,
                revitIni,
                allUsersRoot,
                perUserRoot,
                outPath,
                outputFormat,
                Console.Out);
        }, yearsOpt, localeOpt, contentRootOpt, revitIniOpt, allUsersRootOpt, perUserRootOpt, outOpt, outputOpt);

        return command;
    }

    public static async Task<int> ExecuteBaselineAsync(
        string yearsText,
        string locale,
        string? contentRoot,
        string? revitIni,
        string? allUsersRoot,
        string? perUserRoot,
        string? outPath,
        string outputFormat,
        TextWriter output,
        IReadOnlyDictionary<int, string>? installDirs = null,
        DateTimeOffset? nowUtc = null)
    {
        if (!TerminalOutputFormat.TryNormalize(outputFormat, out var normalizedOutput, "table", "json", "markdown"))
        {
            await output.WriteLineAsync("Error: --output must be 'table', 'json', or 'markdown'.");
            return 1;
        }

        IReadOnlyList<int> years;
        try
        {
            years = RevitAddinAuditor.ParseYears(yearsText);
        }
        catch (ArgumentException ex)
        {
            await output.WriteLineAsync($"Error: {ex.Message}");
            return 1;
        }

        var report = BuildBaseline(
            years,
            locale,
            contentRoot,
            revitIni,
            allUsersRoot,
            perUserRoot,
            installDirs,
            nowUtc ?? DateTimeOffset.UtcNow);

        if (!string.IsNullOrWhiteSpace(outPath))
        {
            try
            {
                var fullOutPath = Path.GetFullPath(RevitLibraryPathMapper.ExpandToLocalPath(outPath));
                Directory.CreateDirectory(Path.GetDirectoryName(fullOutPath)!);
                report.OutputPath = fullOutPath;
                await File.WriteAllTextAsync(fullOutPath, JsonSerializer.Serialize(report, TerminalJsonOptions.PrettyCamel));
            }
            catch (Exception ex) when (RevitLibraryPathMapper.IsFileSystemException(ex))
            {
                await output.WriteLineAsync($"Error: failed to write --out baseline JSON: {ex.Message}");
                return 1;
            }
        }

        await WriteOutputAsync(output, report, normalizedOutput);
        return 0;
    }

    internal static RevitEnvBaselineReport BuildBaseline(
        IReadOnlyList<int> years,
        string locale,
        string? contentRoot,
        string? revitIni,
        string? allUsersRoot,
        string? perUserRoot,
        IReadOnlyDictionary<int, string>? installDirs,
        DateTimeOffset generatedAtUtc)
    {
        var normalizedYears = years.Count == 0
            ? new[] { 2024, 2025, 2026 }
            : years.Distinct().OrderBy(year => year).ToArray();
        var normalizedLocale = RevitLibraryCatalog.NormalizeLocale(locale);
        var report = new RevitEnvBaselineReport
        {
            GeneratedAtUtc = generatedAtUtc,
            Locale = normalizedLocale,
            CliVersion = AssemblyVersionReader.CurrentCliVersion(),
            Machine = BuildMachineBaseline(),
            DesktopConnector = DetectDesktopConnector(),
            Gpu = DetectGpu(),
        };
        report.Years.AddRange(normalizedYears);

        foreach (var year in normalizedYears)
        {
            report.RevitInstallations.Add(InspectRevitInstallation(year, installDirs));
            report.ContentBaselines.Add(InspectContent(year, normalizedLocale, contentRoot, revitIni));
        }

        var audit = RevitAddinAuditor.Audit(normalizedYears, allUsersRoot, perUserRoot);
        report.AddinSummary = new RevitEnvAddinSummary
        {
            Success = audit.Success,
            ManifestCount = audit.ManifestCount,
            AddInCount = audit.AddInCount,
            IssueCount = audit.IssueCount,
            Roots = audit.Roots,
        };

        AddBaselineObservations(report);
        return report;
    }

    private static RevitEnvMachineBaseline BuildMachineBaseline()
    {
        return new RevitEnvMachineBaseline
        {
            UserName = Environment.UserName,
            MachineName = Environment.MachineName,
            OsDescription = RuntimeInformation.OSDescription,
            OsArchitecture = RuntimeInformation.OSArchitecture.ToString(),
            ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            FrameworkDescription = RuntimeInformation.FrameworkDescription,
            IsWsl = RevitLibraryPathMapper.IsRunningOnWsl(),
            RunningRevitProcessCount = CountRunningRevitProcesses(),
        };
    }

    private static int CountRunningRevitProcesses()
    {
        try
        {
            return Process.GetProcesses()
                .Count(process =>
                {
                    try
                    {
                        return process.ProcessName.Contains("Revit", StringComparison.OrdinalIgnoreCase);
                    }
                    catch (InvalidOperationException)
                    {
                        return false;
                    }
                });
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return -1;
        }
    }

    private static RevitEnvInstallation InspectRevitInstallation(
        int year,
        IReadOnlyDictionary<int, string>? installDirs)
    {
        var rawInstallDir = installDirs != null && installDirs.TryGetValue(year, out var overrideDir)
            ? overrideDir
            : RevitInstallDirResolver.Resolve(year);
        var installDir = NormalizeInstallDir(year, rawInstallDir);
        var revitExe = Path.Combine(installDir, "Revit.exe");
        var apiDll = Path.Combine(installDir, "RevitAPI.dll");
        var apiUiDll = Path.Combine(installDir, "RevitAPIUI.dll");
        var installDirExists = Directory.Exists(installDir);
        var revitExeExists = File.Exists(revitExe);

        return new RevitEnvInstallation
        {
            Year = year,
            InstallDir = installDir,
            InstallDirExists = installDirExists,
            RevitExePath = revitExe,
            RevitExeExists = revitExeExists,
            RevitApiDllExists = File.Exists(apiDll),
            RevitApiUiDllExists = File.Exists(apiUiDll),
            Build = ReadFileVersion(revitExe, revitExeExists, $"Revit {year} executable"),
        };
    }

    private static string NormalizeInstallDir(int year, string rawInstallDir)
    {
        var expanded = RevitLibraryPathMapper.ExpandToLocalPath(rawInstallDir);
        if (Path.IsPathRooted(expanded))
            return Path.GetFullPath(expanded);

        if (RevitLibraryPathMapper.IsRunningOnWsl() || Directory.Exists("/mnt/c"))
            return $"/mnt/c/Program Files/Autodesk/Revit {year}";

        return Path.GetFullPath(expanded);
    }

    private static RevitEnvEvidenceValue ReadFileVersion(string path, bool exists, string label)
    {
        if (!exists)
        {
            return RevitEnvEvidenceValue.Unknown(
                "file-version",
                $"{label} was not found at {path}.");
        }

        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            var value = FirstNonEmpty(info.ProductVersion, info.FileVersion);
            return string.IsNullOrWhiteSpace(value)
                ? RevitEnvEvidenceValue.Unknown("file-version", $"{label} has no readable product/file version.")
                : RevitEnvEvidenceValue.Known("file-version", value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return RevitEnvEvidenceValue.Unknown("file-version", ex.Message);
        }
    }

    private static RevitEnvContentBaseline InspectContent(int year, string locale, string? contentRoot, string? revitIni)
    {
        var report = RevitLibraryDetector.Check(year, locale, contentRoot, revitIni);
        return new RevitEnvContentBaseline
        {
            Year = year,
            Locale = locale,
            Success = report.Success,
            ContentRoot = report.ContentRoot ?? "",
            RevitIniPath = report.RevitIniPath ?? "",
            Summary = report.Summary,
            IssueCount = report.Issues.Count,
        };
    }

    private static RevitEnvDesktopConnector DetectDesktopConnector()
    {
        var candidates = new List<string>
        {
            "/mnt/c/Program Files/Autodesk/Desktop Connector/DesktopConnector.Applications.Tray.exe",
            "/mnt/c/Program Files/Autodesk/Desktop Connector/DesktopConnector.exe",
        };

        var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            candidates.Add(Path.Combine(
                RevitLibraryPathMapper.ExpandToLocalPath(localAppData),
                "Autodesk",
                "Desktop Connector"));
        }

        var existing = candidates
            .Select(path => RevitLibraryPathMapper.ExpandToLocalPath(path))
            .FirstOrDefault(path => File.Exists(path) || Directory.Exists(path));

        if (!string.IsNullOrWhiteSpace(existing))
        {
            return new RevitEnvDesktopConnector
            {
                Status = "installed",
                Path = existing,
                Evidence = "Desktop Connector path exists.",
            };
        }

        return new RevitEnvDesktopConnector
        {
            Status = Directory.Exists("/mnt/c") ? "missing" : "unknown",
            Path = candidates[0],
            Evidence = Directory.Exists("/mnt/c")
                ? "Default Desktop Connector paths were not found."
                : "Windows drive mount /mnt/c is unavailable; Desktop Connector cannot be inspected from WSL.",
        };
    }

    private static RevitEnvGpuSummary DetectGpu()
    {
        const string nvidiaVersionPath = "/proc/driver/nvidia/version";
        if (File.Exists(nvidiaVersionPath))
        {
            try
            {
                return new RevitEnvGpuSummary
                {
                    Status = "detected",
                    Source = nvidiaVersionPath,
                    Summary = File.ReadLines(nvidiaVersionPath).FirstOrDefault() ?? "NVIDIA driver file exists.",
                };
            }
            catch (Exception ex) when (RevitLibraryPathMapper.IsFileSystemException(ex))
            {
                return new RevitEnvGpuSummary
                {
                    Status = "unknown",
                    Source = nvidiaVersionPath,
                    Summary = ex.Message,
                };
            }
        }

        if (Directory.Exists("/dev/dri"))
        {
            return new RevitEnvGpuSummary
            {
                Status = "detected",
                Source = "/dev/dri",
                Summary = "DRI device directory exists; Windows-side driver version was not queried.",
            };
        }

        return new RevitEnvGpuSummary
        {
            Status = "unknown",
            Source = "wsl-local",
            Summary = "GPU driver summary unavailable without Windows-side query.",
        };
    }

    private static void AddBaselineObservations(RevitEnvBaselineReport report)
    {
        foreach (var install in report.RevitInstallations)
        {
            if (!install.InstallDirExists)
            {
                report.Observations.Add(new RevitEnvObservation
                {
                    Severity = "warning",
                    Code = "revit-install-dir-missing",
                    Target = install.InstallDir,
                    Message = $"Revit {install.Year} install directory was not found; build is unknown.",
                });
            }
            else if (!install.RevitExeExists)
            {
                report.Observations.Add(new RevitEnvObservation
                {
                    Severity = "warning",
                    Code = "revit-exe-missing",
                    Target = install.InstallDir,
                    Message = $"Revit {install.Year} install directory exists but Revit.exe was not found.",
                });
            }

            if (install.Build.Status == "unknown")
            {
                report.Observations.Add(new RevitEnvObservation
                {
                    Severity = "info",
                    Code = "revit-build-unknown",
                    Target = install.RevitExePath,
                    Message = install.Build.Reason ?? $"Revit {install.Year} build could not be read.",
                });
            }
        }

        foreach (var content in report.ContentBaselines.Where(content => !content.Success))
        {
            report.Observations.Add(new RevitEnvObservation
            {
                Severity = "warning",
                Code = "library-check-has-issues",
                Target = content.ContentRoot,
                Message = $"Revit {content.Year} content/library check reported {content.IssueCount.ToString(CultureInfo.InvariantCulture)} issue(s).",
            });
        }

        if (report.AddinSummary.IssueCount > 0)
        {
            report.Observations.Add(new RevitEnvObservation
            {
                Severity = report.AddinSummary.Success ? "warning" : "error",
                Code = "addins-audit-has-issues",
                Target = "addins",
                Message = $"Add-in audit reported {report.AddinSummary.IssueCount.ToString(CultureInfo.InvariantCulture)} issue(s).",
            });
        }

        if (report.Machine.RunningRevitProcessCount < 0)
        {
            report.Observations.Add(new RevitEnvObservation
            {
                Severity = "info",
                Code = "revit-process-count-unknown",
                Target = "processes",
                Message = "Running Revit process count could not be inspected.",
            });
        }
    }

    private static Task WriteOutputAsync(TextWriter output, RevitEnvBaselineReport report, string outputFormat)
    {
        return outputFormat switch
        {
            "json" => output.WriteLineAsync(JsonSerializer.Serialize(report, TerminalJsonOptions.PrettyCamel)),
            "markdown" => output.WriteLineAsync(RenderMarkdown(report)),
            _ => output.WriteLineAsync(RenderTable(report)),
        };
    }

    private static string RenderTable(RevitEnvBaselineReport report)
    {
        var lines = new List<string>
        {
            $"Revit environment baseline ({report.SchemaVersion})",
            $"Generated: {report.GeneratedAtUtc:u}",
            $"CLI version: {report.CliVersion}",
            $"Machine: {report.Machine.MachineName} / {report.Machine.OsDescription}",
            $".NET runtime: {report.Machine.FrameworkDescription}",
            $"Requires Revit: {report.RequiresRevit.ToString().ToLowerInvariant()}",
            $"Running Revit processes: {report.Machine.RunningRevitProcessCount.ToString(CultureInfo.InvariantCulture)}",
            $"Desktop Connector: {report.DesktopConnector.Status} ({report.DesktopConnector.Evidence})",
            $"GPU: {report.Gpu.Status} ({report.Gpu.Summary})",
            $"Add-ins: {report.AddinSummary.ManifestCount.ToString(CultureInfo.InvariantCulture)} manifests, {report.AddinSummary.AddInCount.ToString(CultureInfo.InvariantCulture)} add-ins, {report.AddinSummary.IssueCount.ToString(CultureInfo.InvariantCulture)} issues",
            "",
            "Revit installs:",
        };

        foreach (var install in report.RevitInstallations)
        {
            lines.Add(
                $"- {install.Year}: installDirExists={install.InstallDirExists.ToString().ToLowerInvariant()}, build={FormatEvidenceValue(install.Build)}, path={install.InstallDir}");
        }

        lines.Add("");
        lines.Add("Content:");
        foreach (var content in report.ContentBaselines)
        {
            lines.Add(
                $"- {content.Year}: families={content.Summary.FamilyFileCount.ToString(CultureInfo.InvariantCulture)}, templates={content.Summary.TemplateFileCount.ToString(CultureInfo.InvariantCulture)}, issues={content.IssueCount.ToString(CultureInfo.InvariantCulture)}, root={content.ContentRoot}");
        }

        if (report.Observations.Count > 0)
        {
            lines.Add("");
            lines.Add("Observations:");
            foreach (var observation in report.Observations)
                lines.Add($"- {observation.Severity}: {observation.Code} - {observation.Message}");
        }

        if (!string.IsNullOrWhiteSpace(report.OutputPath))
            lines.Add($"Saved JSON: {report.OutputPath}");

        return string.Join(Environment.NewLine, lines);
    }

    private static string RenderMarkdown(RevitEnvBaselineReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Revit Environment Baseline");
        builder.AppendLine();
        builder.AppendLine($"- Schema: `{report.SchemaVersion}`");
        builder.AppendLine($"- Generated: `{report.GeneratedAtUtc:u}`");
        builder.AppendLine($"- CLI version: `{report.CliVersion}`");
        builder.AppendLine($"- Requires Revit: `{report.RequiresRevit.ToString().ToLowerInvariant()}`");
        builder.AppendLine($"- Machine: `{report.Machine.MachineName}`");
        builder.AppendLine($"- OS: `{report.Machine.OsDescription}`");
        builder.AppendLine($"- .NET runtime: `{report.Machine.FrameworkDescription}`");
        builder.AppendLine($"- Desktop Connector: `{report.DesktopConnector.Status}` - {report.DesktopConnector.Evidence}");
        builder.AppendLine($"- GPU: `{report.Gpu.Status}` - {report.Gpu.Summary}");
        builder.AppendLine();
        builder.AppendLine("## Revit Installs");
        builder.AppendLine();
        builder.AppendLine("| Year | Install Dir Exists | Revit.exe | Build | Install Dir |");
        builder.AppendLine("| --- | --- | --- | --- | --- |");
        foreach (var install in report.RevitInstallations)
        {
            builder.AppendLine(
                $"| {install.Year} | {install.InstallDirExists.ToString().ToLowerInvariant()} | {install.RevitExeExists.ToString().ToLowerInvariant()} | {FormatEvidenceValue(install.Build)} | `{install.InstallDir}` |");
        }

        builder.AppendLine();
        builder.AppendLine("## Content");
        builder.AppendLine();
        builder.AppendLine("| Year | Families | Templates | Issues | Content Root |");
        builder.AppendLine("| --- | ---: | ---: | ---: | --- |");
        foreach (var content in report.ContentBaselines)
        {
            builder.AppendLine(
                $"| {content.Year} | {content.Summary.FamilyFileCount} | {content.Summary.TemplateFileCount} | {content.IssueCount} | `{content.ContentRoot}` |");
        }

        builder.AppendLine();
        builder.AppendLine("## Add-ins");
        builder.AppendLine();
        builder.AppendLine($"- Manifests: `{report.AddinSummary.ManifestCount}`");
        builder.AppendLine($"- Add-ins: `{report.AddinSummary.AddInCount}`");
        builder.AppendLine($"- Issues: `{report.AddinSummary.IssueCount}`");

        if (report.Observations.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## Observations");
            builder.AppendLine();
            foreach (var observation in report.Observations)
                builder.AppendLine($"- `{observation.Severity}` `{observation.Code}`: {observation.Message}");
        }

        if (!string.IsNullOrWhiteSpace(report.OutputPath))
        {
            builder.AppendLine();
            builder.AppendLine($"Saved JSON: `{report.OutputPath}`");
        }

        return builder.ToString();
    }

    private static string FormatEvidenceValue(RevitEnvEvidenceValue value) =>
        value.Status == "known" ? value.Value ?? "" : $"unknown ({value.Reason})";

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? "";
}

public sealed class RevitEnvBaselineReport
{
    public string SchemaVersion { get; init; } = EnvCommand.BaselineSchemaVersion;
    public DateTimeOffset GeneratedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public bool Success { get; init; } = true;
    public bool RequiresRevit { get; init; }
    public string CliVersion { get; init; } = "";
    public string Locale { get; init; } = "ENU";
    public List<int> Years { get; } = new();
    public RevitEnvMachineBaseline Machine { get; init; } = new();
    public List<RevitEnvInstallation> RevitInstallations { get; } = new();
    public RevitEnvAddinSummary AddinSummary { get; set; } = new();
    public List<RevitEnvContentBaseline> ContentBaselines { get; } = new();
    public RevitEnvDesktopConnector DesktopConnector { get; init; } = new();
    public RevitEnvGpuSummary Gpu { get; init; } = new();
    public List<RevitEnvObservation> Observations { get; } = new();
    public string? OutputPath { get; set; }
}

public sealed class RevitEnvMachineBaseline
{
    public string UserName { get; init; } = "";
    public string MachineName { get; init; } = "";
    public string OsDescription { get; init; } = "";
    public string OsArchitecture { get; init; } = "";
    public string ProcessArchitecture { get; init; } = "";
    public string FrameworkDescription { get; init; } = "";
    public bool IsWsl { get; init; }
    public int RunningRevitProcessCount { get; init; }
}

public sealed class RevitEnvInstallation
{
    public int Year { get; init; }
    public string InstallDir { get; init; } = "";
    public bool InstallDirExists { get; init; }
    public string RevitExePath { get; init; } = "";
    public bool RevitExeExists { get; init; }
    public bool RevitApiDllExists { get; init; }
    public bool RevitApiUiDllExists { get; init; }
    public RevitEnvEvidenceValue Build { get; init; } = RevitEnvEvidenceValue.Unknown("file-version", "Build was not inspected.");
}

public sealed class RevitEnvEvidenceValue
{
    public string Status { get; init; } = "unknown";
    public string? Value { get; init; }
    public string? Source { get; init; }
    public string? Reason { get; init; }

    public static RevitEnvEvidenceValue Known(string source, string value) => new()
    {
        Status = "known",
        Source = source,
        Value = value,
    };

    public static RevitEnvEvidenceValue Unknown(string source, string reason) => new()
    {
        Status = "unknown",
        Source = source,
        Reason = reason,
    };
}

public sealed class RevitEnvContentBaseline
{
    public int Year { get; init; }
    public string Locale { get; init; } = "ENU";
    public bool Success { get; init; }
    public string ContentRoot { get; init; } = "";
    public string RevitIniPath { get; init; } = "";
    public RevitLibraryCheckSummary Summary { get; init; } = new();
    public int IssueCount { get; init; }
}

public sealed class RevitEnvAddinSummary
{
    public bool Success { get; init; } = true;
    public int ManifestCount { get; init; }
    public int AddInCount { get; init; }
    public int IssueCount { get; init; }
    public IReadOnlyList<RevitAddinRoot> Roots { get; init; } = Array.Empty<RevitAddinRoot>();
}

public sealed class RevitEnvDesktopConnector
{
    public string Status { get; init; } = "unknown";
    public string Path { get; init; } = "";
    public string Evidence { get; init; } = "";
}

public sealed class RevitEnvGpuSummary
{
    public string Status { get; init; } = "unknown";
    public string Source { get; init; } = "";
    public string Summary { get; init; } = "";
}

public sealed class RevitEnvObservation
{
    public string Severity { get; init; } = "info";
    public string Code { get; init; } = "";
    public string Target { get; init; } = "";
    public string Message { get; init; } = "";
}

internal static class RevitEnvBaselineReceiptReferenceBuilder
{
    public const string DefaultBaselinePath = ".revitcli/evidence/env-baseline.json";

    public static RevitEnvBaselineReceiptReference Capture(string? baselinePath = null)
    {
        var requestedPath = string.IsNullOrWhiteSpace(baselinePath)
            ? DefaultBaselinePath
            : baselinePath.Trim();
        var captureCommand = $"revitcli env baseline --years 2024,2025,2026 --out {QuoteArgument(requestedPath)} --output json";

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(RevitLibraryPathMapper.ExpandToLocalPath(requestedPath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new RevitEnvBaselineReceiptReference
            {
                Status = "invalid",
                Path = requestedPath,
                CaptureCommand = captureCommand,
                Issue = ex.Message,
            };
        }

        var reference = new RevitEnvBaselineReceiptReference
        {
            Status = "missing",
            Path = fullPath,
            CaptureCommand = captureCommand,
        };

        if (!File.Exists(fullPath))
        {
            reference.Issue = "Environment baseline JSON was not found; run the capture command before approved writes to bind current machine evidence.";
            return reference;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(fullPath));
            var root = document.RootElement;
            reference.SchemaVersion = ReadString(root, "schemaVersion");
            reference.Sha256 = ComputeSha256Hex(fullPath);
            reference.GeneratedAtUtc = ReadDateTimeOffset(root, "generatedAtUtc");
            reference.CliVersion = ReadString(root, "cliVersion");
            reference.RequiresRevit = ReadBool(root, "requiresRevit");
            reference.MachineName = root.TryGetProperty("machine", out var machine)
                ? ReadString(machine, "machineName")
                : null;

            if (root.TryGetProperty("years", out var years) &&
                years.ValueKind == JsonValueKind.Array)
            {
                foreach (var year in years.EnumerateArray())
                {
                    if (year.ValueKind == JsonValueKind.Number &&
                        year.TryGetInt32(out var parsedYear))
                    {
                        reference.Years.Add(parsedYear);
                    }
                }
            }

            if (!string.Equals(reference.SchemaVersion, EnvCommand.BaselineSchemaVersion, StringComparison.OrdinalIgnoreCase))
            {
                reference.Status = "invalid";
                reference.Issue = $"Expected {EnvCommand.BaselineSchemaVersion}.";
                return reference;
            }

            reference.Status = "linked";
            return reference;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            reference.Status = "unreadable";
            reference.Issue = ex.Message;
            return reference;
        }
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool? ReadBool(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) &&
        (property.ValueKind == JsonValueKind.True || property.ValueKind == JsonValueKind.False)
            ? property.GetBoolean()
            : null;

    private static DateTimeOffset? ReadDateTimeOffset(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.String &&
        DateTimeOffset.TryParse(property.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;

    private static string ComputeSha256Hex(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string QuoteArgument(string value) =>
        string.IsNullOrWhiteSpace(value) || value.Any(char.IsWhiteSpace)
            ? $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'"
            : value;
}

public sealed class RevitEnvBaselineReceiptReference
{
    public string Status { get; set; } = "missing";
    public string Path { get; init; } = "";
    public string? SchemaVersion { get; set; }
    public string? Sha256 { get; set; }
    public DateTimeOffset? GeneratedAtUtc { get; set; }
    public string? CliVersion { get; set; }
    public bool? RequiresRevit { get; set; }
    public string? MachineName { get; set; }
    public List<int> Years { get; } = new();
    public string CaptureCommand { get; init; } = "";
    public string? Issue { get; set; }
}
