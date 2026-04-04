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
    public static ProjectProfile Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Profile not found: {path}");

        var yaml = File.ReadAllText(path);
        var profile = Deserializer.Deserialize<ProjectProfile>(yaml)
            ?? throw new InvalidOperationException($"Failed to parse profile: {path}");

        // Resolve inheritance
        if (!string.IsNullOrWhiteSpace(profile.Extends))
        {
            var baseDir = Path.GetDirectoryName(Path.GetFullPath(path))!;
            var basePath = Path.GetFullPath(Path.Combine(baseDir, profile.Extends));
            var baseProfile = Load(basePath); // recursive, supports chaining
            profile = Merge(baseProfile, profile);
        }

        return profile;
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
    /// Merge base profile with child. Child values override base.
    /// Maps are deep-merged by key. Lists are replaced entirely.
    /// </summary>
    private static ProjectProfile Merge(ProjectProfile baseProfile, ProjectProfile child)
    {
        var merged = new ProjectProfile
        {
            Version = child.Version > 0 ? child.Version : baseProfile.Version,
            Defaults = new ProfileDefaults
            {
                OutputDir = child.Defaults.OutputDir ?? baseProfile.Defaults.OutputDir
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

        return merged;
    }
}
