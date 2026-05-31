namespace RevitCli.Libraries;

internal static class RevitLibraryDetector
{
    public static RevitLibraryCheckReport Check(
        int year,
        string locale,
        string? contentRoot,
        string? revitIniPath)
    {
        var normalizedLocale = RevitLibraryCatalog.NormalizeLocale(locale);
        var root = string.IsNullOrWhiteSpace(contentRoot)
            ? RevitLibraryPathMapper.DefaultContentRoot(year)
            : RevitLibraryPathMapper.ExpandToLocalPath(contentRoot);
        var report = new RevitLibraryCheckReport
        {
            RevitYear = year,
            Locale = normalizedLocale,
            ContentRoot = root,
            OfficialSources = RevitLibraryCatalog.CreateSources(year, normalizedLocale),
        };

        if (!RevitLibraryCatalog.IsSupportedYear(year))
        {
            report.Issues.Add(new RevitLibraryIssue
            {
                Severity = "error",
                Code = "unsupported-year",
                Message = $"Unsupported Revit year {year}. Supported years: 2024, 2025, 2026.",
            });
            FinalizeReport(report);
            return report;
        }

        AddDefaultPaths(report, root);
        LoadRevitIni(report, normalizedLocale, revitIniPath);
        ScanPaths(report);
        FinalizeReport(report);
        return report;
    }

    private static void AddDefaultPaths(RevitLibraryCheckReport report, string contentRoot)
    {
        AddPath(report.LibraryPaths, new RevitLibraryPathStatus
        {
            Kind = "library",
            Name = "Default Library Content",
            Source = "default-content-root",
            OriginalPath = Path.Combine(contentRoot, "Libraries"),
            LocalPath = Path.Combine(contentRoot, "Libraries"),
        });

        AddPath(report.FamilyTemplatePaths, new RevitLibraryPathStatus
        {
            Kind = "familyTemplate",
            Name = "Default Family Templates",
            Source = "default-content-root",
            OriginalPath = Path.Combine(contentRoot, "Family Templates"),
            LocalPath = Path.Combine(contentRoot, "Family Templates"),
        });
    }

    private static void LoadRevitIni(
        RevitLibraryCheckReport report,
        string locale,
        string? explicitRevitIniPath)
    {
        var candidates = string.IsNullOrWhiteSpace(explicitRevitIniPath)
            ? RevitLibraryPathMapper.CandidateRevitIniPaths(report.RevitYear)
            : new[] { RevitLibraryPathMapper.ExpandToLocalPath(explicitRevitIniPath) };
        var path = candidates.FirstOrDefault(File.Exists);
        if (path is null)
        {
            if (!string.IsNullOrWhiteSpace(explicitRevitIniPath))
            {
                report.Issues.Add(new RevitLibraryIssue
                {
                    Severity = "warning",
                    Code = "revit-ini-missing",
                    Path = RevitLibraryPathMapper.ExpandToLocalPath(explicitRevitIniPath),
                    Message = "The requested Revit.ini file was not found; only default content paths were checked.",
                });
            }

            return;
        }

        report.RevitIniPath = path;
        RevitIniDocument ini;
        try
        {
            ini = RevitIniDocument.Load(path);
        }
        catch (Exception ex) when (RevitLibraryPathMapper.IsFileSystemException(ex))
        {
            report.Issues.Add(new RevitLibraryIssue
            {
                Severity = "warning",
                Code = "revit-ini-unreadable",
                Path = path,
                Message = ex.Message,
            });
            return;
        }

        var baseDirectory = Path.GetDirectoryName(path);
        foreach (var library in ini.GetDataLibraryLocations(locale))
        {
            AddPath(report.LibraryPaths, new RevitLibraryPathStatus
            {
                Kind = "library",
                Name = library.Name,
                Source = "revit.ini",
                OriginalPath = library.Path,
                LocalPath = RevitLibraryPathMapper.ExpandToLocalPath(library.Path, baseDirectory),
            });
        }

        var templatePath = ini.GetDirectoryValue(locale, "FamilyTemplatePath");
        if (!string.IsNullOrWhiteSpace(templatePath))
        {
            AddPath(report.FamilyTemplatePaths, new RevitLibraryPathStatus
            {
                Kind = "familyTemplate",
                Name = "FamilyTemplatePath",
                Source = "revit.ini",
                OriginalPath = templatePath,
                LocalPath = RevitLibraryPathMapper.ExpandToLocalPath(templatePath, baseDirectory),
            });
        }
    }

    private static void AddPath(List<RevitLibraryPathStatus> paths, RevitLibraryPathStatus candidate)
    {
        if (paths.Any(path => string.Equals(path.LocalPath, candidate.LocalPath, StringComparison.OrdinalIgnoreCase)))
            return;

        paths.Add(candidate);
    }

    private static void ScanPaths(RevitLibraryCheckReport report)
    {
        foreach (var path in report.LibraryPaths.Concat(report.FamilyTemplatePaths))
        {
            path.Exists = Directory.Exists(path.LocalPath);
            if (!path.Exists)
                continue;

            try
            {
                path.RfaCount = CountFiles(path.LocalPath, "*.rfa");
                path.RftCount = CountFiles(path.LocalPath, "*.rft");
            }
            catch (Exception ex) when (RevitLibraryPathMapper.IsFileSystemException(ex))
            {
                path.ScanError = ex.Message;
                report.Issues.Add(new RevitLibraryIssue
                {
                    Severity = "warning",
                    Code = "path-scan-failed",
                    Path = path.LocalPath,
                    Message = ex.Message,
                });
            }
        }
    }

    private static int CountFiles(string root, string pattern)
    {
        var count = 0;
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            try
            {
                count += Directory.EnumerateFiles(current, pattern, SearchOption.TopDirectoryOnly).Count();
                foreach (var child in Directory.EnumerateDirectories(current))
                {
                    var attributes = File.GetAttributes(child);
                    if ((attributes & FileAttributes.ReparsePoint) == 0)
                        pending.Push(child);
                }
            }
            catch (Exception ex) when (RevitLibraryPathMapper.IsFileSystemException(ex))
            {
                throw new IOException($"Failed to scan '{current}': {ex.Message}", ex);
            }
        }

        return count;
    }

    private static void FinalizeReport(RevitLibraryCheckReport report)
    {
        report.Summary = new RevitLibraryCheckSummary
        {
            LibraryPathCount = report.LibraryPaths.Count,
            ExistingLibraryPathCount = report.LibraryPaths.Count(path => path.Exists),
            FamilyTemplatePathCount = report.FamilyTemplatePaths.Count,
            ExistingFamilyTemplatePathCount = report.FamilyTemplatePaths.Count(path => path.Exists),
            FamilyFileCount = report.LibraryPaths.Sum(path => path.RfaCount),
            TemplateFileCount = report.FamilyTemplatePaths.Sum(path => path.RftCount),
            IssueCount = report.Issues.Count,
        };

        if (report.Issues.All(issue => issue.Code != "unsupported-year") &&
            report.Summary.FamilyFileCount == 0)
        {
            report.Issues.Add(new RevitLibraryIssue
            {
                Severity = "error",
                Code = "library-content-missing",
                Message = "No loadable .rfa family files were found in the detected Revit library paths.",
                NextAction = $"Run `revitcli library sources --year {report.RevitYear} --locale {report.Locale}` or download the content pack from Autodesk Account.",
            });
        }

        if (report.Issues.All(issue => issue.Code != "unsupported-year") &&
            report.Summary.TemplateFileCount == 0)
        {
            report.Issues.Add(new RevitLibraryIssue
            {
                Severity = "warning",
                Code = "family-templates-missing",
                Message = "No .rft family template files were found in the detected Family Templates paths.",
                NextAction = "Install the matching Autodesk Revit content pack or repair the FamilyTemplatePath in Revit.ini.",
            });
        }

        report.Summary = report.Summary with { IssueCount = report.Issues.Count };
        report.Success = report.Issues.All(issue =>
            !string.Equals(issue.Severity, "error", StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class RevitLibraryCheckReport
{
    public string SchemaVersion { get; init; } = "revit-library-check.v1";
    public DateTimeOffset GeneratedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public bool Success { get; set; } = true;
    public int RevitYear { get; init; }
    public string Locale { get; init; } = "ENU";
    public string? ContentRoot { get; init; }
    public string? RevitIniPath { get; set; }
    public RevitLibraryCheckSummary Summary { get; set; } = new();
    public List<RevitLibraryPathStatus> LibraryPaths { get; } = new();
    public List<RevitLibraryPathStatus> FamilyTemplatePaths { get; } = new();
    public RevitLibrarySource[] OfficialSources { get; init; } = Array.Empty<RevitLibrarySource>();
    public List<RevitLibraryIssue> Issues { get; } = new();
}

public sealed record RevitLibraryCheckSummary
{
    public int LibraryPathCount { get; init; }
    public int ExistingLibraryPathCount { get; init; }
    public int FamilyTemplatePathCount { get; init; }
    public int ExistingFamilyTemplatePathCount { get; init; }
    public int FamilyFileCount { get; init; }
    public int TemplateFileCount { get; init; }
    public int IssueCount { get; init; }
}

public sealed class RevitLibraryPathStatus
{
    public string Kind { get; init; } = "";
    public string Name { get; init; } = "";
    public string Source { get; init; } = "";
    public string OriginalPath { get; init; } = "";
    public string LocalPath { get; init; } = "";
    public bool Exists { get; set; }
    public int RfaCount { get; set; }
    public int RftCount { get; set; }
    public string? ScanError { get; set; }
}

public sealed class RevitLibraryIssue
{
    public string Severity { get; init; } = "";
    public string Code { get; init; } = "";
    public string? Path { get; init; }
    public string Message { get; init; } = "";
    public string? NextAction { get; init; }
}
