using RevitCli.Addin.Services;

namespace RevitCli.Addin.Tests.Services;

public class AddinVersionProviderTests
{
    [Fact]
    public void Current_ReturnsParseableAssemblyVersion()
    {
        var version = AddinVersionProvider.Current();

        Assert.Matches(@"^\d+\.\d+\.\d+", version);
    }

    [Fact]
    public void NormalizeVersion_PreservesValidInformationalVersionWithBuildMetadata()
    {
        var version = AddinVersionProvider.NormalizeVersion("1.2.3+build.456", new Version(9, 8, 7, 6));

        Assert.Equal("1.2.3+build.456", version);
    }

    [Fact]
    public void NormalizeVersion_FallsBackToAssemblyVersionWhenInformationalVersionIsInvalid()
    {
        var version = AddinVersionProvider.NormalizeVersion("local-dev", new Version(9, 8, 7, 6));

        Assert.Equal("9.8.7", version);
    }

    [Fact]
    public void NormalizeVersion_FallsBackWhenInformationalVersionHasInvalidSuffix()
    {
        var version = AddinVersionProvider.NormalizeVersion("1.2.3+build/bad", new Version(9, 8, 7, 6));

        Assert.Equal("9.8.7", version);
    }

    [Fact]
    public void NormalizeVersion_FallsBackWhenMajorVersionHasLeadingZero()
    {
        var version = AddinVersionProvider.NormalizeVersion("01.2.3", new Version(9, 8, 7, 6));

        Assert.Equal("9.8.7", version);
    }

    [Fact]
    public void NormalizeVersion_FallsBackWhenNumericPrereleaseIdentifierHasLeadingZero()
    {
        var version = AddinVersionProvider.NormalizeVersion("1.2.3-01", new Version(9, 8, 7, 6));

        Assert.Equal("9.8.7", version);
    }
}
