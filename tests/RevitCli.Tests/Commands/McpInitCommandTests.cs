using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using RevitCli.Client;
using RevitCli.Commands;
using RevitCli.Shared;
using Xunit;

namespace RevitCli.Tests.Commands;

/// <summary>
/// Tests for <c>revitcli mcp init</c> — the CLI side of merging /
/// writing a Claude Desktop server config. We use <c>--config-path</c>
/// to point at a temp file so each test is hermetic and doesn't touch
/// the developer's real Claude Desktop config.
/// </summary>
public class McpInitCommandTests : IDisposable
{
    private readonly string _tempDir;

    public McpInitCommandTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"revitcli-mcp-init-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task NewConfigFile_IsCreatedWithRevitCliEntry()
    {
        var configPath = Path.Combine(_tempDir, "claude_desktop_config.json");
        var (client, _) = MakeClient();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await McpCommand.ExecuteInitAsync(
            client, clientName: "claude-desktop", allowWrites: false,
            configPathOverride: configPath, dryRun: false, smokeTest: false,
            commandName: "revitcli", stdout, stderr);

        Assert.Equal(0, exit);
        Assert.True(File.Exists(configPath));
        using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
        var server = doc.RootElement.GetProperty("mcpServers").GetProperty("revitcli");
        Assert.Equal("revitcli", server.GetProperty("command").GetString());
    }

    [Fact]
    public async Task DryRun_PrintsConfigButDoesNotWriteFile()
    {
        var configPath = Path.Combine(_tempDir, "claude_desktop_config.json");
        var (client, _) = MakeClient();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await McpCommand.ExecuteInitAsync(
            client, clientName: "claude-desktop", allowWrites: false,
            configPathOverride: configPath, dryRun: true, smokeTest: false,
            commandName: "revitcli", stdout, stderr);

        Assert.Equal(0, exit);
        Assert.False(File.Exists(configPath));
        Assert.Contains("Would write to", stdout.ToString());
        Assert.Contains("\"revitcli\"", stdout.ToString());
    }

    [Fact]
    public async Task ExistingConfigWithOtherServers_PreservesThem()
    {
        var configPath = Path.Combine(_tempDir, "claude_desktop_config.json");
        File.WriteAllText(configPath, """
        {
          "mcpServers": {
            "filesystem": { "command": "npx", "args": ["-y", "@modelcontextprotocol/server-filesystem", "/tmp"] }
          }
        }
        """);

        var (client, _) = MakeClient();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await McpCommand.ExecuteInitAsync(
            client, clientName: "claude-desktop", allowWrites: true,
            configPathOverride: configPath, dryRun: false, smokeTest: false,
            commandName: "revitcli", stdout, stderr);

        Assert.Equal(0, exit);
        using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
        var servers = doc.RootElement.GetProperty("mcpServers");
        Assert.True(servers.TryGetProperty("filesystem", out _));
        Assert.True(servers.TryGetProperty("revitcli", out _));
        // --allow-writes flag is present.
        var args = servers.GetProperty("revitcli").GetProperty("args");
        Assert.Equal("--allow-writes", args[args.GetArrayLength() - 1].GetString());
    }

    [Fact]
    public async Task UnsupportedClient_ExitsOneWithDiagnostic()
    {
        var (client, _) = MakeClient();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await McpCommand.ExecuteInitAsync(
            client, clientName: "cursor",
            allowWrites: false, configPathOverride: null,
            dryRun: false, smokeTest: false,
            commandName: "revitcli", stdout, stderr);

        Assert.Equal(1, exit);
        Assert.Contains("unsupported --client", stderr.ToString());
    }

    [Fact]
    public async Task CorruptExistingConfig_ExitsOneWithReadableHint()
    {
        var configPath = Path.Combine(_tempDir, "claude_desktop_config.json");
        File.WriteAllText(configPath, "{not json");

        var (client, _) = MakeClient();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await McpCommand.ExecuteInitAsync(
            client, clientName: "claude-desktop", allowWrites: false,
            configPathOverride: configPath, dryRun: false, smokeTest: false,
            commandName: "revitcli", stdout, stderr);

        Assert.Equal(1, exit);
        var stderrText = stderr.ToString();
        Assert.Contains("not valid JSON", stderrText);
        Assert.Contains("Move", stderrText); // hint to relocate / pass --config-path
    }

    [Fact]
    public async Task ParentDirectoryMissing_IsCreated()
    {
        // Claude Desktop's parent dir may not exist on a fresh box. The
        // command should create it rather than fail with a cryptic
        // "directory not found".
        var nestedPath = Path.Combine(_tempDir, "nested", "dir", "config.json");
        var (client, _) = MakeClient();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await McpCommand.ExecuteInitAsync(
            client, clientName: "claude-desktop", allowWrites: false,
            configPathOverride: nestedPath, dryRun: false, smokeTest: false,
            commandName: "revitcli", stdout, stderr);

        Assert.Equal(0, exit);
        Assert.True(File.Exists(nestedPath));
    }

    [Fact]
    public async Task SmokeTest_AddinReachable_ExitsZeroAndPrintsResult()
    {
        var configPath = Path.Combine(_tempDir, "claude_desktop_config.json");
        var (client, handler) = MakeClient();
        handler.Enqueue("/api/status", ApiResponse<StatusInfo>.Ok(new StatusInfo
        {
            RevitVersion = "2026", DocumentName = "Demo.rvt", DocumentPath = @"C:\Demo.rvt"
        }));

        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await McpCommand.ExecuteInitAsync(
            client, clientName: "claude-desktop", allowWrites: false,
            configPathOverride: configPath, dryRun: false, smokeTest: true,
            commandName: "revitcli", stdout, stderr);

        Assert.Equal(0, exit);
        var output = stdout.ToString();
        Assert.Contains("Smoke test: OK", output);
        Assert.Contains("2026", output); // RevitVersion appears in status output
    }

    [Fact]
    public async Task SmokeTest_AddinUnreachable_ExitsTwoButConfigStillWritten()
    {
        // The distinct exit code (2) signals "config wrote, smoke failed"
        // — a meaningful state for CI scripts that want to retry the
        // smoke test without re-doing the merge.
        var configPath = Path.Combine(_tempDir, "claude_desktop_config.json");
        var (client, handler) = MakeClient();
        handler.Enqueue("/api/status", ApiResponse<StatusInfo>.Fail("Revit is not running"));

        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await McpCommand.ExecuteInitAsync(
            client, clientName: "claude-desktop", allowWrites: false,
            configPathOverride: configPath, dryRun: false, smokeTest: true,
            commandName: "revitcli", stdout, stderr);

        Assert.Equal(2, exit);
        Assert.True(File.Exists(configPath));
        Assert.Contains("FAILED", stdout.ToString());
    }

    private static (RevitClient Client, RevitCli.Tests.Mcp.QueueHttpHandler Handler) MakeClient()
    {
        var handler = new RevitCli.Tests.Mcp.QueueHttpHandler();
        var client = new RevitClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:17839") });
        return (client, handler);
    }
}
