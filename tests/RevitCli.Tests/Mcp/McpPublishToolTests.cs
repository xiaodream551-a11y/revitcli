using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using RevitCli.Client;
using RevitCli.Mcp;
using RevitCli.Mcp.Tools;
using Xunit;

namespace RevitCli.Tests.Mcp;

/// <summary>
/// Verifies the MCP <c>publish</c> tool — gate, audit log, path-binding for
/// <c>since</c>, and dispatcher wiring. The orchestration (profile load,
/// precheck, export-preset loop, baseline update) stays in
/// <see cref="RevitCli.Commands.PublishCommand"/> and is covered by its own
/// tests; this file only proves the MCP wrapper.
/// </summary>
[Collection("CwdMutating")]
public class McpPublishToolTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _originalCwd;

    public McpPublishToolTests()
    {
        var setupDir = Path.Combine(Path.GetTempPath(), $"revitcli-mcp-publish-{Guid.NewGuid():N}");
        Directory.CreateDirectory(setupDir);
        _originalCwd = Environment.CurrentDirectory;
        Environment.CurrentDirectory = setupDir;
        _tempDir = Environment.CurrentDirectory; // canonical (macOS /private/var)
        Directory.CreateDirectory(Path.Combine(_tempDir, ".revitcli"));
    }

    public void Dispose()
    {
        Environment.CurrentDirectory = _originalCwd;
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task WritesDisabled_RefusesAndLogsAudit()
    {
        var tool = new PublishTool(MakeClient(new QueueHttpHandler()), allowWrites: false);

        var text = await tool.ExecuteAsync(new JsonObject
        {
            ["confirm"] = true,
            ["dryRun"] = true,
        }, CancellationToken.None);

        Assert.Equal(PublishTool.DisabledMessage, text);
        var entry = ReadOnlyJournalEntry();
        Assert.Equal("publish", entry.GetProperty("action").GetString());
        Assert.Equal("mcp", entry.GetProperty("transport").GetString());
        Assert.Equal("refused-writes-disabled", entry.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task NoConfirm_RefusesAndLogsAudit()
    {
        var tool = new PublishTool(MakeClient(new QueueHttpHandler()), allowWrites: true);

        var text = await tool.ExecuteAsync(new JsonObject
        {
            ["dryRun"] = true,
        }, CancellationToken.None);

        Assert.Equal(PublishTool.ConfirmMessage, text);
        var entry = ReadOnlyJournalEntry();
        Assert.Equal("refused-no-confirm", entry.GetProperty("outcome").GetString());
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("../../etc/passwd")]
    [InlineData("baseline.json")] // not under .revitcli/
    public async Task SinceOutsideRevitCli_RefusesAndLogsBoundsViolation(string since)
    {
        var handler = new QueueHttpHandler();
        var tool = new PublishTool(MakeClient(handler), allowWrites: true);

        var text = await tool.ExecuteAsync(new JsonObject
        {
            ["confirm"] = true,
            ["dryRun"] = true,
            ["since"] = since,
        }, CancellationToken.None);

        Assert.Contains("must be a path under .revitcli/", text);
        // Guard fires before PublishCommand is invoked, so no HTTP traffic.
        Assert.Empty(handler.Requests);
        var entry = ReadOnlyJournalEntry();
        Assert.Equal("refused-path-out-of-bounds", entry.GetProperty("outcome").GetString());
        // Original (unbound) value preserved for forensics.
        Assert.Equal(since, entry.GetProperty("since").GetString());
    }

    [Fact]
    public async Task NoProfile_LogsErrorOutcomeFromCli()
    {
        // No profile written -> PublishCommand exits 1 with "no .revitcli.yml
        // found" message. The wrapper records this as outcome=error so the
        // forensic trail captures the failure (and the LLM gets a clear hint).
        var handler = new QueueHttpHandler();
        var tool = new PublishTool(MakeClient(handler), allowWrites: true);

        var text = await tool.ExecuteAsync(new JsonObject
        {
            ["confirm"] = true,
            ["dryRun"] = true,
        }, CancellationToken.None);

        Assert.Contains("no .revitcli.yml", text, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(handler.Requests);
        var entry = ReadOnlyJournalEntry();
        Assert.Equal("error", entry.GetProperty("outcome").GetString());
        Assert.Equal(1, entry.GetProperty("exitCode").GetInt32());
    }

    [Fact]
    public async Task DryRun_WithMinimalProfile_LogsOk_NoExportCalls()
    {
        // A pipeline with no precheck and a single trivial preset, run in
        // dryRun mode, hits the "[dry-run] Would export ..." branch in
        // PublishCommand without making any /api/* calls.
        File.WriteAllText(Path.Combine(_tempDir, ".revitcli.yml"), """
version: 1
publish:
  default:
    presets: [pdf]
exports:
  pdf:
    format: pdf
    sheets: [A101]
    outputDir: out/
""");

        var handler = new QueueHttpHandler();
        var tool = new PublishTool(MakeClient(handler), allowWrites: true);

        var text = await tool.ExecuteAsync(new JsonObject
        {
            ["confirm"] = true,
            ["dryRun"] = true,
        }, CancellationToken.None);

        var entry = ReadOnlyJournalEntry();
        Assert.Equal("ok", entry.GetProperty("outcome").GetString());
        Assert.True(entry.GetProperty("dryRun").GetBoolean());
        Assert.Equal(0, entry.GetProperty("exitCode").GetInt32());
        Assert.Empty(handler.Requests);
        Assert.Contains("[dry-run]", text);
    }

    [Fact]
    public void Schema_RequiresConfirm()
    {
        var tool = new PublishTool(MakeClient(new QueueHttpHandler()), allowWrites: true);
        var schema = tool.InputSchema.AsObject();
        var required = schema["required"]!.AsArray();
        var names = new System.Collections.Generic.HashSet<string>();
        foreach (var n in required) names.Add(n!.GetValue<string>());
        Assert.Contains("confirm", names);
        // pipeline / since / sinceMode / dryRun / updateBaseline are all optional.
        Assert.DoesNotContain("pipeline", names);
        Assert.DoesNotContain("since", names);
    }

    [Fact]
    public async Task ToolsList_IncludesPublish_EvenWhenWritesDisabled()
    {
        var handler = new QueueHttpHandler();
        var input = new StringReader(string.Empty);
        var output = new StringWriter();
        var server = McpServer.CreateDefault(MakeClient(handler), input, output, TextWriter.Null, allowWrites: false);

        await server.HandleLineAsync(Request(1, "tools/list"), CancellationToken.None);

        var response = JsonNode.Parse(output.ToString().Trim().Split('\n', 2)[0].TrimEnd('\r'))!;
        var tools = response["result"]!["tools"]!.AsArray();
        var names = new System.Collections.Generic.HashSet<string>();
        foreach (var t in tools) names.Add(t!["name"]!.GetValue<string>());
        Assert.Contains("publish", names);
    }

    [Fact]
    public async Task ToolsCall_Publish_AllowWritesOff_ReturnsRefusalAsContent()
    {
        var handler = new QueueHttpHandler();
        var input = new StringReader(string.Empty);
        var output = new StringWriter();
        var server = McpServer.CreateDefault(MakeClient(handler), input, output, TextWriter.Null, allowWrites: false);

        await server.HandleLineAsync(Request(2, "tools/call", new JsonObject
        {
            ["name"] = "publish",
            ["arguments"] = new JsonObject { ["confirm"] = true },
        }), CancellationToken.None);

        var response = JsonNode.Parse(output.ToString().Trim().Split('\n', 2)[0].TrimEnd('\r'))!;
        var text = response["result"]!["content"]!.AsArray()[0]!["text"]!.GetValue<string>();
        Assert.Equal(PublishTool.DisabledMessage, text);
        Assert.Empty(handler.Requests);
    }

    private JsonElement ReadOnlyJournalEntry()
    {
        // Filter by action="publish" — see comment in McpFixToolTests.
        var journalPath = Path.Combine(_tempDir, ".revitcli", "journal.jsonl");
        Assert.True(File.Exists(journalPath), $"expected journal at {journalPath}");
        foreach (var line in File.ReadAllLines(journalPath))
        {
            var entry = JsonDocument.Parse(line).RootElement.Clone();
            if (entry.TryGetProperty("action", out var a) && a.GetString() == "publish")
                return entry;
        }
        throw new Xunit.Sdk.XunitException($"no action=publish entry in {journalPath}");
    }

    private static RevitClient MakeClient(QueueHttpHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:17839") });

    private static string Request(int id, string method, JsonNode? @params = null)
    {
        var obj = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
        };
        if (@params is not null) obj["params"] = @params;
        return obj.ToJsonString();
    }
}
