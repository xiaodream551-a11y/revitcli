using System.Text.Json;
using RevitCli.Mcp;
using Xunit;

namespace RevitCli.Tests.Mcp;

/// <summary>
/// Unit tests for the JSON-merge core powering <c>revitcli mcp init</c>.
/// File IO is tested separately at the command level — here we drive
/// <see cref="McpClientConfig.MergeRevitCliEntry"/> with hand-built
/// strings so the test surface is fast and platform-independent.
/// </summary>
public class McpClientConfigTests
{
    [Fact]
    public void MergeRevitCliEntry_NewConfig_CreatesShape()
    {
        var merged = McpClientConfig.MergeRevitCliEntry(
            existingJson: null, command: "revitcli", allowWrites: false);

        using var doc = JsonDocument.Parse(merged);
        var server = doc.RootElement.GetProperty("mcpServers").GetProperty("revitcli");
        Assert.Equal("revitcli", server.GetProperty("command").GetString());
        var args = server.GetProperty("args");
        Assert.Equal(2, args.GetArrayLength());
        Assert.Equal("mcp", args[0].GetString());
        Assert.Equal("serve", args[1].GetString());
    }

    [Fact]
    public void MergeRevitCliEntry_AllowWrites_AppendsFlag()
    {
        var merged = McpClientConfig.MergeRevitCliEntry(
            existingJson: null, command: "revitcli", allowWrites: true);

        using var doc = JsonDocument.Parse(merged);
        var args = doc.RootElement.GetProperty("mcpServers")
            .GetProperty("revitcli").GetProperty("args");
        Assert.Equal(3, args.GetArrayLength());
        Assert.Equal("--allow-writes", args[2].GetString());
    }

    [Fact]
    public void MergeRevitCliEntry_PreservesExistingServers()
    {
        // The merge must NOT trample other MCP servers the user already
        // configured. Operators commonly have multiple servers (filesystem,
        // git, sqlite, etc.) and re-running `mcp init` shouldn't touch them.
        var existing = """
        {
          "mcpServers": {
            "filesystem": { "command": "npx", "args": ["-y", "@modelcontextprotocol/server-filesystem", "/tmp"] },
            "git": { "command": "uvx", "args": ["mcp-server-git"] }
          }
        }
        """;

        var merged = McpClientConfig.MergeRevitCliEntry(existing, "revitcli", allowWrites: false);

        using var doc = JsonDocument.Parse(merged);
        var servers = doc.RootElement.GetProperty("mcpServers");
        Assert.True(servers.TryGetProperty("filesystem", out _));
        Assert.True(servers.TryGetProperty("git", out _));
        Assert.True(servers.TryGetProperty("revitcli", out _));
    }

    [Fact]
    public void MergeRevitCliEntry_ExistingRevitCliEntry_IsOverwritten()
    {
        // Re-running `mcp init` is the user saying "make this entry
        // current". A stale entry pointing at a moved binary should be
        // overwritten — leaving an inconsistent mix would be worse.
        var existing = """
        {
          "mcpServers": {
            "revitcli": { "command": "/old/path/revitcli", "args": ["mcp", "serve"] }
          }
        }
        """;

        var merged = McpClientConfig.MergeRevitCliEntry(existing, "revitcli", allowWrites: true);

        using var doc = JsonDocument.Parse(merged);
        var server = doc.RootElement.GetProperty("mcpServers").GetProperty("revitcli");
        Assert.Equal("revitcli", server.GetProperty("command").GetString());
        // --allow-writes appears in the new args.
        var args = server.GetProperty("args");
        Assert.Equal(3, args.GetArrayLength());
        Assert.Equal("--allow-writes", args[2].GetString());
    }

    [Fact]
    public void MergeRevitCliEntry_ConfigWithoutMcpServersKey_AddsIt()
    {
        // Some users have a config-shaped JSON file with other top-level
        // keys but no mcpServers block yet. Adding one alongside any
        // existing keys is the right behavior.
        var existing = """{"theme":"dark","customWindow":true}""";
        var merged = McpClientConfig.MergeRevitCliEntry(existing, "revitcli", allowWrites: false);

        using var doc = JsonDocument.Parse(merged);
        Assert.Equal("dark", doc.RootElement.GetProperty("theme").GetString());
        Assert.True(doc.RootElement.TryGetProperty("mcpServers", out _));
    }

    [Fact]
    public void MergeRevitCliEntry_InvalidJson_ThrowsTypedException()
    {
        // We catch this at the CLI layer to surface a readable diagnostic.
        // The underlying JsonException has a helpful position but operators
        // can't act on "Unexpected character at line 3 col 5" — we wrap.
        Assert.Throws<InvalidJsonException>(() =>
            McpClientConfig.MergeRevitCliEntry("{not json", "revitcli", false));
    }

    [Fact]
    public void MergeRevitCliEntry_NonObjectRoot_ThrowsTypedException()
    {
        // A JSON array at the root isn't a Claude Desktop config — it's
        // some other file. Refuse rather than try to mutate.
        Assert.Throws<InvalidJsonException>(() =>
            McpClientConfig.MergeRevitCliEntry("[1,2,3]", "revitcli", false));
    }

    [Fact]
    public void MergeRevitCliEntry_CustomCommand_FlowsToConfig()
    {
        // Operators with the binary on PATH under a different name (e.g.
        // a wrapper script) need --command to take effect.
        var merged = McpClientConfig.MergeRevitCliEntry(null, "/opt/mybin/rcli", allowWrites: false);
        using var doc = JsonDocument.Parse(merged);
        var server = doc.RootElement.GetProperty("mcpServers").GetProperty("revitcli");
        Assert.Equal("/opt/mybin/rcli", server.GetProperty("command").GetString());
    }

    [Fact]
    public void MergeRevitCliEntry_OutputIsIndented()
    {
        // Operators read this file. Indented output is meaningful UX,
        // not a nicety — pin it so we don't regress to one-liners.
        var merged = McpClientConfig.MergeRevitCliEntry(null, "revitcli", false);
        Assert.Contains("\n", merged);
        Assert.Contains("  ", merged);
    }
}
