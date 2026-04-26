using System;
using System.Text.RegularExpressions;

namespace RevitCli.Diagnostics;

internal enum VersionCompatibility
{
    Compatible,
    MetadataMismatch,
    PatchMismatch,
    MajorMinorMismatch
}

internal readonly record struct ComponentVersion(
    int Major,
    int Minor,
    int Patch,
    string Metadata,
    string Original)
{
    private static readonly Regex VersionPattern = new(
        @"^v?(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)(?:-[^+]+)?(?:\+(?<metadata>.+))?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static bool TryParse(string? value, out ComponentVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();
        var match = VersionPattern.Match(trimmed);
        if (!match.Success)
            return false;

        if (!int.TryParse(match.Groups["major"].Value, out var major)
            || !int.TryParse(match.Groups["minor"].Value, out var minor)
            || !int.TryParse(match.Groups["patch"].Value, out var patch))
        {
            return false;
        }

        version = new ComponentVersion(
            major,
            minor,
            patch,
            match.Groups["metadata"].Success ? match.Groups["metadata"].Value : "",
            trimmed);
        return true;
    }

    public static VersionCompatibility Compare(ComponentVersion expected, ComponentVersion actual)
    {
        if (expected.Major != actual.Major || expected.Minor != actual.Minor)
            return VersionCompatibility.MajorMinorMismatch;

        if (expected.Patch != actual.Patch)
            return VersionCompatibility.PatchMismatch;

        if (!string.Equals(expected.Metadata, actual.Metadata, StringComparison.Ordinal))
            return VersionCompatibility.MetadataMismatch;

        return VersionCompatibility.Compatible;
    }

    public override string ToString() => string.IsNullOrWhiteSpace(Original)
        ? $"{Major}.{Minor}.{Patch}"
        : Original;
}
