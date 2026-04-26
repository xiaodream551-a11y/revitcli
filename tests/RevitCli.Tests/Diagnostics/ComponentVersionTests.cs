using RevitCli.Diagnostics;

namespace RevitCli.Tests.Diagnostics;

public class ComponentVersionTests
{
    [Theory]
    [InlineData("1.3.0", 1, 3, 0, "")]
    [InlineData("1.3.0+local", 1, 3, 0, "local")]
    [InlineData("1.3.0-beta.1+sha", 1, 3, 0, "sha")]
    [InlineData("v1.3.2", 1, 3, 2, "")]
    public void TryParse_AcceptsAssemblyAndInformationalVersions(
        string input, int major, int minor, int patch, string metadata)
    {
        Assert.True(ComponentVersion.TryParse(input, out var parsed));
        Assert.Equal(major, parsed.Major);
        Assert.Equal(minor, parsed.Minor);
        Assert.Equal(patch, parsed.Patch);
        Assert.Equal(metadata, parsed.Metadata);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("1")]
    [InlineData("1.x.0")]
    [InlineData("1.3.0.0")]
    [InlineData("999999999999999999999.0.0")]
    [InlineData("1.999999999999999999999.0")]
    [InlineData("1.3.999999999999999999999")]
    public void TryParse_RejectsUnusableVersions(string input)
    {
        Assert.False(ComponentVersion.TryParse(input, out _));
    }

    [Theory]
    [InlineData("1.3.0", "1.3.1", (int)VersionCompatibility.PatchMismatch)]
    [InlineData("1.3.0+local", "1.3.0", (int)VersionCompatibility.MetadataMismatch)]
    [InlineData("1.3.0", "1.0.0", (int)VersionCompatibility.MajorMinorMismatch)]
    [InlineData("2.0.0", "1.3.0", (int)VersionCompatibility.MajorMinorMismatch)]
    [InlineData("1.3.0", "1.3.0", (int)VersionCompatibility.Compatible)]
    public void Compare_UsesMajorMinorAsFailureBoundary(
        string left, string right, int expected)
    {
        Assert.True(ComponentVersion.TryParse(left, out var a));
        Assert.True(ComponentVersion.TryParse(right, out var b));

        Assert.Equal((VersionCompatibility)expected, ComponentVersion.Compare(a, b));
    }
}
