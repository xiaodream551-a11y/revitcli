using System;
using System.IO;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace RevitCli.Profile;

public static class ProfileLoader
{
    public const string FileName = ".revitcli.yml";

    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>
    /// Discover .revitcli.yml by walking up from startDir.
    /// Returns null if not found.
    /// </summary>
    public static string? Discover(string? startDir = null)
    {
        var dir = startDir ?? Directory.GetCurrentDirectory();

        while (dir != null)
        {
            var candidate = Path.Combine(dir, FileName);
            if (File.Exists(candidate))
                return candidate;

            dir = Directory.GetParent(dir)?.FullName;
        }

        return null;
    }

    /// <summary>
    /// Load and parse a profile from the given path.
    /// Resolves single-parent inheritance via 'extends'.
    /// </summary>
    public static ProjectProfile Load(string path) => Load(path, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    private static ProjectProfile Load(string path, HashSet<string> visited)
    {
        var canonical = Path.GetFullPath(path);
        if (!visited.Add(canonical))
            throw new InvalidOperationException($"Circular profile inheritance detected: {canonical}");

        if (!File.Exists(canonical))
            throw new FileNotFoundException($"Profile not found: {canonical}");

        var yaml = File.ReadAllText(canonical);
        var profile = Deserializer.Deserialize<ProjectProfile>(yaml)
            ?? throw new InvalidOperationException($"Failed to parse profile: {canonical}");

        ValidateProfile(profile, canonical);

        // Resolve inheritance
        if (!string.IsNullOrWhiteSpace(profile.Extends))
        {
            var baseDir = Path.GetDirectoryName(canonical)!;
            var basePath = Path.GetFullPath(Path.Combine(baseDir, profile.Extends));
            if (!basePath.StartsWith(baseDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(Path.GetDirectoryName(basePath), baseDir, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Profile 'extends' path escapes the profile directory: {profile.Extends}");
            var baseProfile = Load(basePath, visited);
            profile = Merge(baseProfile, profile);
        }

        return profile;
    }

    private static readonly HashSet<string> ValidSeverities = new(StringComparer.OrdinalIgnoreCase)
        { "error", "warning", "info" };

    private static readonly HashSet<string> ValidFailOn = new(StringComparer.OrdinalIgnoreCase)
        { "error", "warning" };

    private static void ValidateProfile(ProjectProfile profile, string path)
    {
        foreach (var (name, check) in profile.Checks)
        {
            if (!ValidFailOn.Contains(check.FailOn))
                throw new InvalidOperationException(
                    $"Profile {path}: checks.{name}.failOn must be 'error' or 'warning', got '{check.FailOn}'");

            foreach (var req in check.RequiredParameters)
            {
                if (!ValidSeverities.Contains(req.Severity))
                    throw new InvalidOperationException(
                        $"Profile {path}: checks.{name}.requiredParameters severity must be error/warning/info, got '{req.Severity}'");
            }

            foreach (var naming in check.Naming)
            {
                if (!ValidSeverities.Contains(naming.Severity))
                    throw new InvalidOperationException(
                        $"Profile {path}: checks.{name}.naming severity must be error/warning/info, got '{naming.Severity}'");
            }
        }
    }

    /// <summary>
    /// Discover and load profile. Returns null if no profile found.
    /// </summary>
    public static ProjectProfile? DiscoverAndLoad(string? startDir = null)
    {
        var path = Discover(startDir);
        if (path == null)
            return null;

        return Load(path);
    }

    /// <summary>
    /// Merge base profile with child.
    /// - defaults: field-level merge (child overrides individual fields, inherits the rest)
    /// - checks/exports/publish: merged by name (child replaces entire named entry)
    /// - lists within named entries: NOT deep-merged (child replaces parent's lists)
    /// </summary>
    private static ProjectProfile Merge(ProjectProfile baseProfile, ProjectProfile child)
    {
        var merged = new ProjectProfile
        {
            Version = child.Version > 0 ? child.Version : baseProfile.Version,
            Defaults = new ProfileDefaults
            {
                OutputDir = child.Defaults.OutputDir ?? baseProfile.Defaults.OutputDir,
                Notify = child.Defaults.Notify ?? baseProfile.Defaults.Notify
            }
        };

        // Merge checks by name
        foreach (var kvp in baseProfile.Checks)
            merged.Checks[kvp.Key] = kvp.Value;
        foreach (var kvp in child.Checks)
            merged.Checks[kvp.Key] = kvp.Value;

        // Merge exports by name
        foreach (var kvp in baseProfile.Exports)
            merged.Exports[kvp.Key] = kvp.Value;
        foreach (var kvp in child.Exports)
            merged.Exports[kvp.Key] = kvp.Value;

        // Merge publish by name
        foreach (var kvp in baseProfile.Publish)
            merged.Publish[kvp.Key] = kvp.Value;
        foreach (var kvp in child.Publish)
            merged.Publish[kvp.Key] = kvp.Value;

        // Merge schedules by name
        foreach (var kvp in baseProfile.Schedules)
            merged.Schedules[kvp.Key] = kvp.Value;
        foreach (var kvp in child.Schedules)
            merged.Schedules[kvp.Key] = kvp.Value;

        return merged;
    }
}
