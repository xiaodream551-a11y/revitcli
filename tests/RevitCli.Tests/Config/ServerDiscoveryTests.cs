using System.IO;
using System.Text.Json;
using RevitCli.Client;
using RevitCli.Shared;
using Xunit;

namespace RevitCli.Tests.Config;

public class ServerDiscoveryTests
{
    [Fact]
    public void DiscoverServerUrl_NoFile_ReturnsConfigured()
    {
        var (url, token) = RevitClient.DiscoverServerUrl("http://localhost:17839");
        // If no server.json exists at the expected path, should return configured URL
        // This test works because we don't write a server.json during testing
        Assert.StartsWith("http://localhost:", url);
    }

    [Fact]
    public void ServerInfo_Serialize_Roundtrip()
    {
        var info = new ServerInfo
        {
            Port = 17840,
            Pid = 1234,
            RevitVersion = "2025",
            StartedAt = "2026-04-02T12:00:00Z"
        };

        var json = JsonSerializer.Serialize(info);
        var loaded = JsonSerializer.Deserialize<ServerInfo>(json);

        Assert.NotNull(loaded);
        Assert.Equal(17840, loaded.Port);
        Assert.Equal(1234, loaded.Pid);
        Assert.Equal("2025", loaded.RevitVersion);
    }
}
