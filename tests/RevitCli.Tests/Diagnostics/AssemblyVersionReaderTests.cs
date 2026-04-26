using RevitCli.Diagnostics;

namespace RevitCli.Tests.Diagnostics;

public class AssemblyVersionReaderTests
{
    [Fact]
    public void TryRead_ReturnsVersionForManagedAssembly()
    {
        var path = typeof(AssemblyVersionReaderTests).Assembly.Location;

        Assert.True(AssemblyVersionReader.TryRead(path, out var version, out var error));
        Assert.True(ComponentVersion.TryParse(version, out _));
        Assert.Null(error);
    }

    [Fact]
    public void TryRead_ReturnsErrorForMissingFile()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.dll");

        Assert.False(AssemblyVersionReader.TryRead(path, out var version, out var error));
        Assert.Equal("", version);
        Assert.Contains("does not exist", error);
    }

    [Fact]
    public void TryRead_ReturnsErrorForBadAssemblyFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"revitcli_bad_{Guid.NewGuid():N}.dll");
        try
        {
            File.WriteAllText(path, "not a managed assembly");
            Assert.False(AssemblyVersionReader.TryRead(path, out var version, out var error));
            Assert.Equal("", version);
            Assert.Contains("cannot be read", error);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void CurrentCliVersion_IsParseable()
    {
        var version = AssemblyVersionReader.CurrentCliVersion();

        Assert.True(ComponentVersion.TryParse(version, out _));
    }

    [Theory]
    [InlineData("1.3.0+local", "1.3.0+local")]
    [InlineData("1.3.0.0", "1.3.0")]
    [InlineData("1.3.0.42+build", "1.3.0+build")]
    public void TryNormalizeVersion_NormalizesSupportedWindowsVersionStrings(string input, string expected)
    {
        Assert.True(AssemblyVersionReader.TryNormalizeVersion(input, out var normalized));
        Assert.Equal(expected, normalized);
        Assert.True(ComponentVersion.TryParse(normalized, out _));
    }

    [Theory]
    [InlineData("RevitCli 1.3")]
    [InlineData("1.3")]
    [InlineData("1.3.x.0")]
    public void TryNormalizeVersion_RejectsUnsupportedProductVersionStrings(string input)
    {
        Assert.False(AssemblyVersionReader.TryNormalizeVersion(input, out _));
    }
}
