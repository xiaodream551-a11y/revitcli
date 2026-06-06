using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace RevitCli.Addins;

public static class RevitAddinAuditor
{
    public const string SchemaVersion = "revit-addins-audit.v1";

    public static RevitAddinAuditReport Audit(
        IReadOnlyList<int> years,
        string? allUsersRoot = null,
        string? perUserRoot = null)
    {
        var normalizedYears = years.Count == 0
            ? new[] { 2024, 2025, 2026 }
            : years.Distinct().OrderBy(year => year).ToArray();
        var roots = CreateRoots(allUsersRoot, perUserRoot);
        var report = new RevitAddinAuditReport
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            Years = normalizedYears.ToList(),
            Roots = roots.Select(root => new RevitAddinRoot(root.Scope, root.Path)).ToList(),
        };

        foreach (var year in normalizedYears)
        {
            foreach (var root in roots)
            {
                var directory = Path.Combine(root.Path, year.ToString(CultureInfo.InvariantCulture));
                if (!Directory.Exists(directory))
                    continue;

                string[] manifestPaths;
                try
                {
                    manifestPaths = Directory.EnumerateFiles(directory, "*.addin", SearchOption.TopDirectoryOnly)
                        .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    report.Issues.Add(new RevitAddinAuditIssue
                    {
                        Severity = "warning",
                        Code = "addin-directory-unreadable",
                        Year = year,
                        Scope = root.Scope,
                        ManifestPath = directory,
                        Message = $"Add-in directory could not be listed: {ex.Message}",
                    });
                    continue;
                }

                foreach (var manifestPath in manifestPaths)
                {
                    ReadManifest(report, year, root.Scope, manifestPath);
                }
            }
        }

        AddDuplicateAddInIdIssues(report);
        AddShadowingIssues(report);
        report.ManifestCount = report.Manifests.Count;
        report.AddInCount = report.AddIns.Count;
        report.IssueCount = report.Issues.Count;
        report.Success = report.Issues.All(issue => !string.Equals(issue.Severity, "error", StringComparison.OrdinalIgnoreCase));
        return report;
    }

    public static IReadOnlyList<int> ParseYears(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new[] { 2024, 2025, 2026 };

        var years = new List<int>();
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year) ||
                year < 2000 ||
                year > 2100)
            {
                throw new ArgumentException($"Invalid Revit year: {part}");
            }

            years.Add(year);
        }

        return years.Count == 0 ? new[] { 2024, 2025, 2026 } : years;
    }

    private static IReadOnlyList<RevitAddinAuditRoot> CreateRoots(string? allUsersRoot, string? perUserRoot)
    {
        return new[]
        {
            new RevitAddinAuditRoot("all-users", ExpandRoot(allUsersRoot, "PROGRAMDATA", "/mnt/c/ProgramData", "Autodesk/Revit/Addins")),
            new RevitAddinAuditRoot("per-user", ExpandRoot(perUserRoot, "APPDATA", "", "Autodesk/Revit/Addins")),
        };
    }

    private static string ExpandRoot(string? explicitRoot, string envVar, string fallbackBase, string suffix)
    {
        if (!string.IsNullOrWhiteSpace(explicitRoot))
            return Path.GetFullPath(ExpandEnvironmentTokens(explicitRoot));

        var envValue = Environment.GetEnvironmentVariable(envVar);
        if (!string.IsNullOrWhiteSpace(envValue))
            return Path.GetFullPath(Path.Combine(ExpandEnvironmentTokens(envValue), suffix));

        if (!string.IsNullOrWhiteSpace(fallbackBase))
            return Path.GetFullPath(Path.Combine(fallbackBase, suffix));

        var home = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(home))
            return Path.GetFullPath(Path.Combine(home, suffix));

        return Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", suffix));
    }

    private static void ReadManifest(
        RevitAddinAuditReport report,
        int year,
        string scope,
        string manifestPath)
    {
        var manifest = new RevitAddinManifestRecord
        {
            Year = year,
            Scope = scope,
            Path = Path.GetFullPath(manifestPath),
            FileName = Path.GetFileName(manifestPath),
        };
        report.Manifests.Add(manifest);

        XDocument document;
        try
        {
            document = XDocument.Load(manifestPath, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            report.Issues.Add(new RevitAddinAuditIssue
            {
                Severity = "error",
                Code = "manifest-unreadable",
                Year = year,
                Scope = scope,
                ManifestPath = manifest.Path,
                Message = $"Add-in manifest could not be read: {ex.Message}",
            });
            return;
        }

        foreach (var addIn in document.Descendants("AddIn"))
        {
            var record = CreateAddInRecord(year, scope, manifest.Path, addIn);
            report.AddIns.Add(record);
            AddRecordIssues(report, record, manifest.Path);
        }
    }

    private static RevitAddinRecord CreateAddInRecord(
        int year,
        string scope,
        string manifestPath,
        XElement addIn)
    {
        var assemblyRaw = ElementValue(addIn, "Assembly");
        return new RevitAddinRecord
        {
            Year = year,
            Scope = scope,
            ManifestPath = manifestPath,
            ManifestFileName = Path.GetFileName(manifestPath),
            Type = addIn.Attribute("Type")?.Value ?? "",
            Name = ElementValue(addIn, "Name"),
            AddInId = ElementValue(addIn, "AddInId"),
            VendorId = ElementValue(addIn, "VendorId"),
            AssemblyPath = ResolveAssemblyPath(manifestPath, assemblyRaw),
            AssemblyRaw = assemblyRaw,
        };
    }

    private static string ElementValue(XElement element, string localName) =>
        element.Elements()
            .FirstOrDefault(child => string.Equals(child.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase))
            ?.Value.Trim() ?? "";

    private static string ResolveAssemblyPath(string manifestPath, string assembly)
    {
        if (string.IsNullOrWhiteSpace(assembly))
            return "";

        var expanded = ExpandEnvironmentTokens(assembly.Trim());
        if (Path.IsPathFullyQualified(expanded))
            return Path.GetFullPath(expanded);

        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(manifestPath) ?? "", expanded));
    }

    private static string ExpandEnvironmentTokens(string value)
    {
        var expanded = Environment.ExpandEnvironmentVariables(value);
        if (!expanded.Contains('%', StringComparison.Ordinal))
            return expanded;

        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var key = entry.Key?.ToString();
            var replacement = entry.Value?.ToString();
            if (string.IsNullOrWhiteSpace(key) || replacement == null)
                continue;
            expanded = expanded.Replace($"%{key}%", replacement, StringComparison.OrdinalIgnoreCase);
        }

        return expanded;
    }

    private static void AddRecordIssues(RevitAddinAuditReport report, RevitAddinRecord record, string manifestPath)
    {
        if (string.IsNullOrWhiteSpace(record.AddInId))
        {
            report.Issues.Add(CreateIssue("error", "missing-addin-id", record, "AddInId is missing."));
        }

        if (string.IsNullOrWhiteSpace(record.AssemblyPath))
        {
            report.Issues.Add(CreateIssue("error", "missing-assembly", record, "Assembly path is missing."));
        }
        else if (!File.Exists(record.AssemblyPath))
        {
            report.Issues.Add(CreateIssue("error", "missing-assembly", record, $"Assembly path does not exist: {record.AssemblyPath}"));
        }
        else if (LooksLikeDevelopmentPath(record.AssemblyPath))
        {
            report.Issues.Add(CreateIssue("warning", "dev-path-assembly", record, $"Assembly path looks like a development build: {record.AssemblyPath}"));
        }

        if (string.IsNullOrWhiteSpace(record.VendorId))
        {
            report.Issues.Add(CreateIssue("warning", "missing-vendor-id", record, "VendorId is missing."));
        }

        if (!string.Equals(Path.GetFullPath(manifestPath), record.ManifestPath, StringComparison.Ordinal))
        {
            report.Issues.Add(CreateIssue("warning", "manifest-path-mismatch", record, "Manifest path normalization mismatch."));
        }
    }

    private static void AddDuplicateAddInIdIssues(RevitAddinAuditReport report)
    {
        foreach (var group in report.AddIns
                     .Where(addin => !string.IsNullOrWhiteSpace(addin.AddInId))
                     .GroupBy(addin => addin.AddInId, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
        {
            foreach (var addin in group)
            {
                report.Issues.Add(CreateIssue(
                    "error",
                    "duplicate-addin-id",
                    addin,
                    $"AddInId {group.Key} appears in {group.Count()} manifests."));
            }
        }
    }

    private static void AddShadowingIssues(RevitAddinAuditReport report)
    {
        foreach (var group in report.Manifests
                     .GroupBy(manifest => $"{manifest.Year}|{manifest.FileName}", StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Select(manifest => manifest.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1))
        {
            foreach (var manifest in group)
            {
                report.Issues.Add(new RevitAddinAuditIssue
                {
                    Severity = "warning",
                    Code = "same-filename-shadowing",
                    Year = manifest.Year,
                    Scope = manifest.Scope,
                    ManifestPath = manifest.Path,
                    Message = $"Manifest filename {manifest.FileName} appears in multiple add-in roots for Revit {manifest.Year}. Revit loads manifests by filename order.",
                });
            }
        }
    }

    private static RevitAddinAuditIssue CreateIssue(
        string severity,
        string code,
        RevitAddinRecord record,
        string message)
    {
        return new RevitAddinAuditIssue
        {
            Severity = severity,
            Code = code,
            Year = record.Year,
            Scope = record.Scope,
            ManifestPath = record.ManifestPath,
            AddInId = record.AddInId,
            Name = record.Name,
            AssemblyPath = record.AssemblyPath,
            Message = message,
        };
    }

    private static bool LooksLikeDevelopmentPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.Contains("/bin/Debug/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/bin/Release/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/src/", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("/home/", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record RevitAddinAuditRoot(string Scope, string Path);
}

public sealed class RevitAddinAuditReport
{
    public string SchemaVersion { get; set; } = RevitAddinAuditor.SchemaVersion;
    public DateTimeOffset GeneratedAt { get; set; }
    public bool Success { get; set; }
    public List<int> Years { get; set; } = new();
    public List<RevitAddinRoot> Roots { get; set; } = new();
    public int ManifestCount { get; set; }
    public int AddInCount { get; set; }
    public int IssueCount { get; set; }
    public List<RevitAddinManifestRecord> Manifests { get; set; } = new();
    public List<RevitAddinRecord> AddIns { get; set; } = new();
    public List<RevitAddinAuditIssue> Issues { get; set; } = new();
}

public sealed record RevitAddinRoot(string Scope, string Path);

public sealed class RevitAddinManifestRecord
{
    public int Year { get; set; }
    public string Scope { get; set; } = "";
    public string Path { get; set; } = "";
    public string FileName { get; set; } = "";
}

public sealed class RevitAddinRecord
{
    public int Year { get; set; }
    public string Scope { get; set; } = "";
    public string ManifestPath { get; set; } = "";
    public string ManifestFileName { get; set; } = "";
    public string Type { get; set; } = "";
    public string Name { get; set; } = "";
    public string AddInId { get; set; } = "";
    public string VendorId { get; set; } = "";
    public string AssemblyPath { get; set; } = "";
    public string AssemblyRaw { get; set; } = "";
}

public sealed class RevitAddinAuditIssue
{
    public string Severity { get; set; } = "warning";
    public string Code { get; set; } = "";
    public int Year { get; set; }
    public string Scope { get; set; } = "";
    public string ManifestPath { get; set; } = "";
    public string AddInId { get; set; } = "";
    public string Name { get; set; } = "";
    public string AssemblyPath { get; set; } = "";
    public string Message { get; set; } = "";
}
