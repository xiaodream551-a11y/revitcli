using System;
using System.IO;
using RevitCli.Mcp.Tools;
using Xunit;

namespace RevitCli.Tests.Mcp;

/// <summary>
/// Unit tests for the path-bounded resolver shared by the MCP write tools
/// (rollback's baseline, import's file). The boundary is <c>.revitcli/</c>
/// resolved against the process cwd; we mutate cwd to a known temp dir so
/// the tests don't depend on the test runner's working directory.
/// </summary>
[Collection("CwdMutating")]
public class McpPathGuardTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _originalCwd;

    public McpPathGuardTests()
    {
        var setupDir = Path.Combine(Path.GetTempPath(), $"revitcli-pathguard-{Guid.NewGuid():N}");
        Directory.CreateDirectory(setupDir);
        _originalCwd = Environment.CurrentDirectory;
        Environment.CurrentDirectory = setupDir;
        _tempDir = Environment.CurrentDirectory; // canonical (handles macOS /private/var)
        Directory.CreateDirectory(Path.Combine(_tempDir, ".revitcli"));
    }

    public void Dispose()
    {
        Environment.CurrentDirectory = _originalCwd;
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void NullOrWhitespace_Rejected(string? input)
    {
        Assert.Null(McpPathGuard.ResolveUnderRevitCli(input));
    }

    [Fact]
    public void RelativePathInsideRevitCli_AcceptedAndResolvedAbsolute()
    {
        var resolved = McpPathGuard.ResolveUnderRevitCli(".revitcli/baseline.json");
        Assert.NotNull(resolved);
        Assert.True(Path.IsPathRooted(resolved));
        Assert.Equal(Path.Combine(_tempDir, ".revitcli", "baseline.json"), resolved);
    }

    [Fact]
    public void AbsolutePathInsideRevitCli_Accepted()
    {
        var inside = Path.Combine(_tempDir, ".revitcli", "fix-baseline.json");
        Assert.Equal(inside, McpPathGuard.ResolveUnderRevitCli(inside));
    }

    [Fact]
    public void NestedPathInsideRevitCli_Accepted()
    {
        // Nested subdirectories under .revitcli/ are fine.
        var nested = Path.Combine(_tempDir, ".revitcli", "imports", "january", "doors.csv");
        var resolved = McpPathGuard.ResolveUnderRevitCli(nested);
        Assert.Equal(nested, resolved);
    }

    [Fact]
    public void BoundaryItself_Accepted()
    {
        // The boundary directory itself resolves to a non-null path —
        // a tool that wants to enumerate the boundary is in-scope.
        var resolved = McpPathGuard.ResolveUnderRevitCli(".revitcli");
        Assert.Equal(Path.Combine(_tempDir, ".revitcli"), resolved);
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("/var/log/system.log")]
    public void AbsolutePathOutsideRevitCli_Rejected(string outside)
    {
        Assert.Null(McpPathGuard.ResolveUnderRevitCli(outside));
    }

    [Fact]
    public void RelativeTraversalEscape_Rejected()
    {
        // .revitcli/../escape.json resolves to <tempDir>/escape.json — outside.
        Assert.Null(McpPathGuard.ResolveUnderRevitCli(".revitcli/../escape.json"));
    }

    [Fact]
    public void DeeperTraversal_Rejected()
    {
        // ../../etc/passwd canonicalizes to a path far above the boundary.
        Assert.Null(McpPathGuard.ResolveUnderRevitCli("../../etc/passwd"));
    }

    [Fact]
    public void SiblingDirectoryOfRevitCli_Rejected()
    {
        // A directory NEXT TO .revitcli/ is still outside.
        var sibling = Path.Combine(_tempDir, "not-revitcli", "leaked.json");
        Assert.Null(McpPathGuard.ResolveUnderRevitCli(sibling));
    }

    [Fact]
    public void PathWithSimilarPrefix_Rejected()
    {
        // ".revitcli-evil/" should NOT pass — segment-aware comparison via
        // GetRelativePath rules out string-prefix attacks like
        // "/proj/.revitcli" + "-evil/foo" looking like a child.
        var lookalike = Path.Combine(_tempDir, ".revitcli-evil", "x.json");
        Assert.Null(McpPathGuard.ResolveUnderRevitCli(lookalike));
    }
}
