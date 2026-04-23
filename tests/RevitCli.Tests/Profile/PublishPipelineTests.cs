using System.IO;
using RevitCli.Profile;
using Xunit;

namespace RevitCli.Tests.Profile;

public class PublishPipelineTests
{
    private static ProjectProfile LoadYaml(string yaml)
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, yaml);
        try { return ProfileLoader.Load(path); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Publish_Incremental_True_ReadsNewFields()
    {
        var profile = LoadYaml(@"
version: 1
publish:
  default:
    precheck: default
    incremental: true
    baselinePath: baseline.json
    sinceMode: content
    presets: [dwg]
");
        var pipeline = profile.Publish["default"];
        Assert.True(pipeline.Incremental);
        Assert.Equal("baseline.json", pipeline.BaselinePath);
        Assert.Equal("content", pipeline.SinceMode);
        Assert.Equal(new[] { "dwg" }, pipeline.Presets);
        Assert.Equal("default", pipeline.Precheck);
    }

    [Fact]
    public void Publish_Defaults_WhenNewFieldsOmitted()
    {
        var profile = LoadYaml(@"
version: 1
publish:
  default:
    presets: [dwg]
");
        var pipeline = profile.Publish["default"];
        Assert.False(pipeline.Incremental);
        Assert.Null(pipeline.BaselinePath);
        Assert.Equal("content", pipeline.SinceMode);
    }

    [Fact]
    public void Publish_SinceMode_Meta_PassesThrough()
    {
        var profile = LoadYaml(@"
version: 1
publish:
  default:
    sinceMode: meta
    presets: [dwg]
");
        Assert.Equal("meta", profile.Publish["default"].SinceMode);
    }

    [Fact]
    public void Publish_NoIncrementalField_FalseByDefault()
    {
        var profile = LoadYaml(@"
version: 1
publish:
  default:
    precheck: default
    presets: [dwg, pdf]
");
        Assert.False(profile.Publish["default"].Incremental);
    }
}
