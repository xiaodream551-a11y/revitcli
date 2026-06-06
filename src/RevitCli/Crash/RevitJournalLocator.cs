namespace RevitCli.Crash;

internal static class RevitJournalLocator
{
    public static IReadOnlyList<string> FindJournals(
        int revitYear,
        string? journalPath,
        DateTimeOffset sinceUtc,
        List<RevitCrashIssue> issues)
    {
        if (!string.IsNullOrWhiteSpace(journalPath))
        {
            var expanded = ExpandPath(journalPath);
            if (File.Exists(expanded))
                return new[] { Path.GetFullPath(expanded) };

            issues.Add(new RevitCrashIssue
            {
                Severity = "error",
                Code = "journal-missing",
                Path = expanded,
                Message = $"Journal file does not exist: {expanded}",
            });
            return Array.Empty<string>();
        }

        var directories = CandidateJournalDirectories(revitYear)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var files = new List<string>();
        foreach (var directory in directories)
        {
            if (!Directory.Exists(directory))
                continue;

            try
            {
                files.AddRange(Directory.EnumerateFiles(directory, "*.txt", SearchOption.TopDirectoryOnly));
            }
            catch (IOException ex)
            {
                issues.Add(Warning("journal-enumeration-failed", directory, ex.Message));
            }
            catch (UnauthorizedAccessException ex)
            {
                issues.Add(Warning("journal-enumeration-failed", directory, ex.Message));
            }
        }

        var selected = files
            .Select(path => new FileInfo(path))
            .Where(file => file.Exists && new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero) >= sinceUtc)
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Take(5)
            .Select(file => file.FullName)
            .ToArray();

        if (selected.Length == 0)
        {
            issues.Add(new RevitCrashIssue
            {
                Severity = "warning",
                Code = "no-recent-journals",
                Message = directories.Length == 0
                    ? $"No Revit {revitYear} journal directories were discovered."
                    : $"No Revit {revitYear} journal files were found in the requested time window.",
            });
        }

        return selected;
    }

    internal static string ExpandPath(string path)
    {
        var trimmed = path.Trim();
        if (trimmed == "~")
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (trimmed.StartsWith("~/", StringComparison.Ordinal))
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                trimmed[2..]);
        return trimmed;
    }

    internal static string PreferredJournalDirectory(int revitYear) =>
        CandidateJournalDirectories(revitYear).FirstOrDefault()
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "AppData",
            "Local",
            "Autodesk",
            "Revit",
            $"Autodesk Revit {revitYear}",
            "Journals");

    private static IEnumerable<string> CandidateJournalDirectories(int revitYear)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            yield return Path.Combine(
                localAppData,
                "Autodesk",
                "Revit",
                $"Autodesk Revit {revitYear}",
                "Journals");
        }

        var userNames = new[]
        {
            Environment.GetEnvironmentVariable("USERNAME"),
            Environment.GetEnvironmentVariable("USER"),
        }.Where(name => !string.IsNullOrWhiteSpace(name));

        foreach (var userName in userNames)
        {
            yield return Path.Combine(
                "/mnt/c/Users",
                userName!,
                "AppData",
                "Local",
                "Autodesk",
                "Revit",
                $"Autodesk Revit {revitYear}",
                "Journals");
        }

        var usersRoot = "/mnt/c/Users";
        if (!Directory.Exists(usersRoot))
            yield break;

        IEnumerable<string> userDirectories;
        try
        {
            userDirectories = Directory.EnumerateDirectories(usersRoot).ToArray();
        }
        catch (IOException)
        {
            yield break;
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var userDirectory in userDirectories)
        {
            yield return Path.Combine(
                userDirectory,
                "AppData",
                "Local",
                "Autodesk",
                "Revit",
                $"Autodesk Revit {revitYear}",
                "Journals");
        }
    }

    private static RevitCrashIssue Warning(string code, string path, string message) =>
        new()
        {
            Severity = "warning",
            Code = code,
            Path = path,
            Message = message,
        };
}
