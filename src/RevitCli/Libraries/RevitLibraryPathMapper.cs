using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace RevitCli.Libraries;

internal static partial class RevitLibraryPathMapper
{
    private static readonly Regex WindowsDrivePathPattern =
        new(@"^(?<drive>[A-Za-z]):[\\/](?<tail>.*)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string ExpandToLocalPath(string rawPath, string? baseDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            return rawPath;

        var expanded = ExpandKnownWindowsVariables(rawPath.Trim().Trim('"'));
        var match = WindowsDrivePathPattern.Match(expanded);
        if (match.Success)
        {
            var drive = char.ToLowerInvariant(match.Groups["drive"].Value[0]);
            var tail = match.Groups["tail"].Value.Replace('\\', '/');
            return string.IsNullOrEmpty(tail)
                ? $"/mnt/{drive}"
                : $"/mnt/{drive}/{tail}";
        }

        if (expanded.StartsWith("~/", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(home))
                expanded = Path.Combine(home, expanded[2..]);
        }

        if (!Path.IsPathRooted(expanded) && !string.IsNullOrWhiteSpace(baseDirectory))
            expanded = Path.Combine(baseDirectory, expanded);

        return expanded.Replace('\\', Path.DirectorySeparatorChar);
    }

    public static string ToWindowsPath(string localPath)
    {
        if (string.IsNullOrWhiteSpace(localPath))
            return localPath;

        var normalized = localPath.Replace('\\', '/');
        if (normalized.Length > 7 &&
            normalized.StartsWith("/mnt/", StringComparison.OrdinalIgnoreCase) &&
            normalized[6] == '/')
        {
            var drive = char.ToUpperInvariant(normalized[5]);
            var tail = normalized[7..].Replace('/', '\\');
            return $"{drive}:\\{tail}";
        }

        return localPath;
    }

    public static bool IsRunningOnWsl()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return false;

        try
        {
            return File.Exists("/proc/version") &&
                   File.ReadAllText("/proc/version").Contains("microsoft", StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static string DefaultDownloadDirectory()
    {
        if (Directory.Exists("/mnt/d"))
            return "/mnt/d/temp/revit-content";

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(home)
            ? Path.Combine(Path.GetTempPath(), "revit-content")
            : Path.Combine(home, "Downloads", "revit-content");
    }

    public static string DefaultContentRoot(int year)
    {
        var primary = $"/mnt/c/ProgramData/Autodesk/RVT {year}";
        if (Directory.Exists(primary))
            return primary;

        var release = $"/mnt/c/ProgramData/Autodesk/RVT {year} Release";
        return Directory.Exists(release) ? release : primary;
    }

    public static IReadOnlyList<string> CandidateRevitIniPaths(int year)
    {
        var paths = new List<string>();
        AddIfNotEmpty(paths, Environment.GetEnvironmentVariable("APPDATA"),
            "Autodesk", "Revit", $"Autodesk Revit {year}", "Revit.ini");

        var windowsUserProfile = GetWindowsUserProfile();
        AddIfNotEmpty(paths, windowsUserProfile,
            "AppData", "Roaming", "Autodesk", "Revit", $"Autodesk Revit {year}", "Revit.ini");
        AddIfNotEmpty(paths, $"/mnt/c/ProgramData/Autodesk/RVT {year}/UserDataCache", "Revit.ini");
        AddIfNotEmpty(paths, $"/mnt/c/ProgramData/Autodesk/RVT {year} Release/UserDataCache", "Revit.ini");

        var roamingRoot = "/mnt/c/Users";
        if (Directory.Exists(roamingRoot))
        {
            try
            {
                foreach (var userDir in Directory.EnumerateDirectories(roamingRoot))
                {
                    var candidate = Path.Combine(
                        userDir,
                        "AppData",
                        "Roaming",
                        "Autodesk",
                        "Revit",
                        $"Autodesk Revit {year}",
                        "Revit.ini");
                    paths.Add(candidate);
                }
            }
            catch (Exception ex) when (IsFileSystemException(ex))
            {
            }
        }

        return paths
            .Select(path => ExpandToLocalPath(path))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string ExpandKnownWindowsVariables(string value)
    {
        var result = value;
        result = ReplaceToken(result, "%ALLUSERSPROFILE%", @"C:\ProgramData");
        result = ReplaceToken(result, "%PROGRAMDATA%", @"C:\ProgramData");

        var userProfile = GetWindowsUserProfile();
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            result = ReplaceToken(result, "%USERPROFILE%", userProfile);
            result = ReplaceToken(result, "%APPDATA%", Path.Combine(userProfile, "AppData", "Roaming"));
            result = ReplaceToken(result, "%LOCALAPPDATA%", Path.Combine(userProfile, "AppData", "Local"));
        }

        return result;
    }

    private static string ReplaceToken(string value, string token, string replacement) =>
        value.Replace(token, replacement, StringComparison.OrdinalIgnoreCase);

    private static string? GetWindowsUserProfile()
    {
        var explicitProfile = Environment.GetEnvironmentVariable("USERPROFILE");
        if (!string.IsNullOrWhiteSpace(explicitProfile) && WindowsDrivePathPattern.IsMatch(explicitProfile))
            return explicitProfile;

        var user = Environment.GetEnvironmentVariable("USERNAME");
        if (string.IsNullOrWhiteSpace(user))
            user = Environment.GetEnvironmentVariable("USER");

        if (!string.IsNullOrWhiteSpace(user))
        {
            var candidate = $"/mnt/c/Users/{user}";
            if (Directory.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static void AddIfNotEmpty(List<string> paths, string? root, params string[] parts)
    {
        if (string.IsNullOrWhiteSpace(root))
            return;

        paths.Add(parts.Length == 0 ? root : Path.Combine(new[] { root }.Concat(parts).ToArray()));
    }

    internal static bool IsFileSystemException(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException;
}
