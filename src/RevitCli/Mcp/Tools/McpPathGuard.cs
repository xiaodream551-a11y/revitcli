using System;
using System.IO;

namespace RevitCli.Mcp.Tools;

/// <summary>
/// Path validator shared by MCP write tools that take caller-supplied
/// filesystem paths (rollback's <c>baseline</c>, import's <c>file</c>).
///
/// Threat model: an LLM-driven MCP client can supply arbitrary strings.
/// Without bounding, a tool could be coaxed into reading or writing files
/// anywhere the server process has access to. By forcing all caller-paths
/// through the project's <c>.revitcli/</c> subtree we get a small, easy
/// audit boundary: operators can stage inputs there and reason about what
/// the LLM might touch.
///
/// The check is symlink-naive — we canonicalize via <see cref="Path.GetFullPath"/>
/// and compare with <see cref="Path.GetRelativePath"/>, which together
/// resolve traversal segments (<c>..</c>) but do not chase symlinks. A
/// hostile operator who plants a symlink under <c>.revitcli/</c> pointing
/// outside is out of scope for this defense; the operator is trusted.
/// </summary>
internal static class McpPathGuard
{
    /// <summary>
    /// Resolve <paramref name="callerSuppliedPath"/> against the current
    /// working directory and assert it lies under <c>.revitcli/</c>. Returns
    /// the resolved absolute path on success, or <c>null</c> if the path is
    /// missing/empty, escapes the boundary, lives on a different drive
    /// (Windows), or contains traversal segments after canonicalization.
    /// </summary>
    public static string? ResolveUnderRevitCli(string? callerSuppliedPath)
    {
        if (string.IsNullOrWhiteSpace(callerSuppliedPath)) return null;

        string boundary, resolved;
        try
        {
            boundary = Path.GetFullPath(".revitcli");
            resolved = Path.GetFullPath(callerSuppliedPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        // The boundary itself is allowed (a tool that reads the directory).
        if (string.Equals(resolved, boundary, StringComparison.Ordinal))
            return resolved;

        string rel;
        try
        {
            rel = Path.GetRelativePath(boundary, resolved);
        }
        catch (ArgumentException)
        {
            return null;
        }

        // Different drive on Windows surfaces as an absolute path here.
        if (Path.IsPathRooted(rel)) return null;

        // Any "..": GetRelativePath only emits this when the target is
        // outside the source directory. A legitimate path under .revitcli/
        // never produces ".." as a segment.
        foreach (var segment in rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment == "..")
                return null;
        }

        return resolved;
    }
}
