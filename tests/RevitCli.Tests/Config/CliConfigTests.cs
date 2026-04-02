using System.IO;
using System.Text.Json;
using RevitCli.Config;
using Xunit;

namespace RevitCli.Tests.Config;

public class CliConfigTests
{
    [Fact]
    public void Load_NoFile_ReturnsDefaults()
    {
        var config = new CliConfig();
        Assert.Equal("http://localhost:17839", config.ServerUrl);
        Assert.Equal("table", config.DefaultOutput);
        Assert.Equal(".", config.ExportDir);
    }

    [Fact]
    public void Serialize_Roundtrip_PreservesValues()
    {
        var config = new CliConfig
        {
            ServerUrl = "http://localhost:9999",
            DefaultOutput = "json",
            ExportDir = "/tmp/exports"
        };

        var json = JsonSerializer.Serialize(config);
        var loaded = JsonSerializer.Deserialize<CliConfig>(json);

        Assert.NotNull(loaded);
        Assert.Equal("http://localhost:9999", loaded.ServerUrl);
        Assert.Equal("json", loaded.DefaultOutput);
        Assert.Equal("/tmp/exports", loaded.ExportDir);
    }

    [Fact]
    public void Deserialize_PartialJson_UsesDefaults()
    {
        var json = """{"serverUrl": "http://custom:8080"}""";
        var config = JsonSerializer.Deserialize<CliConfig>(json);

        Assert.NotNull(config);
        Assert.Equal("http://custom:8080", config.ServerUrl);
        Assert.Equal("table", config.DefaultOutput);  // default
        Assert.Equal(".", config.ExportDir);  // default
    }
}
