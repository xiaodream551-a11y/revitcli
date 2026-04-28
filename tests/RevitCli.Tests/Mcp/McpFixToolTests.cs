using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using RevitCli.Client;
using RevitCli.Mcp;
using RevitCli.Mcp.Tools;
using RevitCli.Shared;
using Xunit;

namespace RevitCli.Tests.Mcp;

/// <summary>
/// Verifies the MCP <c>fix</c> tool — gate + audit log + dispatcher wiring.
///
/// Tests in this class mutate the process-global <c>Environment.CurrentDirectory</c>
/// (so <c>ProfileLoader.Discover</c> finds the temp profile, and the audit
/// journal lands under <c>.revitcli/journal.jsonl</c> in the temp dir). They
/// are placed in a non-parallel collection to keep that mutation safe.
///
/// We do NOT retest the fix orchestration here — the underlying
/// <c>FixCommand.ExecuteAsync</c> already has its own coverage. This file
/// only proves the MCP wrapper maps args correctly, gates writes, audit-logs
/// each outcome, and round-trips through the JSON-RPC dispatcher.
/// </summary>
[Collection("CwdMutating")]
public class McpFixToolTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _originalCwd;

    public McpFixToolTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"revitcli-mcp-fix-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _originalCwd = Environment.CurrentDirectory;
        Environment.CurrentDirectory = _tempDir;
    }

    public void Dispose()
    {
        Environment.CurrentDirectory = _originalCwd;
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task WritesDisabled_ReturnsRefusalAndLogsAudit()
    {
        var tool = new FixTool(MakeClient(new QueueHttpHandler()), allowWrites: false);

        var text = await tool.ExecuteAsync(new JsonObject
        {
            ["confirm"] = true,
            ["dryRun"] = true,
        }, CancellationToken.None);

        Assert.Equal(FixTool.DisabledMessage, text);
        var entry = ReadOnlyJournalEntry();
        Assert.Equal("fix", entry.GetProperty("action").GetString());
        Assert.Equal("mcp", entry.GetProperty("transport").GetString());
        Assert.Equal("refused-writes-disabled", entry.GetProperty("outcome").GetString());
        Assert.True(entry.GetProperty("dryRun").GetBoolean());
    }

    [Fact]
    public async Task NoConfirm_ReturnsRefusalAndLogsAudit()
    {
        var tool = new FixTool(MakeClient(new QueueHttpHandler()), allowWrites: true);

        var text = await tool.ExecuteAsync(new JsonObject
        {
            ["dryRun"] = true,
        }, CancellationToken.None);

        Assert.Equal(FixTool.ConfirmMessage, text);
        var entry = ReadOnlyJournalEntry();
        Assert.Equal("refused-no-confirm", entry.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task DryRun_EmptyAudit_ReturnsPlan_LogsOk_NoWrites()
    {
        WriteProfile(_tempDir, """
version: 1
checks:
  default:
    failOn: error
""");
        var handler = new QueueHttpHandler();
        // Empty audit -> FixPlanner produces zero actions -> "no fixable issues" path.
        handler.Enqueue("/api/audit", ApiResponse<AuditResult>.Ok(new AuditResult
        {
            Passed = 0,
            Failed = 0,
            Issues = new List<AuditIssue>(),
        }));
        var tool = new FixTool(MakeClient(handler), allowWrites: true);

        var text = await tool.ExecuteAsync(new JsonObject
        {
            ["confirm"] = true,
            ["dryRun"] = true,
        }, CancellationToken.None);

        var entry = ReadOnlyJournalEntry();
        Assert.Equal("ok", entry.GetProperty("outcome").GetString());
        Assert.True(entry.GetProperty("dryRun").GetBoolean());
        Assert.Equal(0, entry.GetProperty("exitCode").GetInt32());
        // No /api/elements/set calls in dryRun mode — gate is enforced by the CLI.
        Assert.DoesNotContain(handler.Requests, r => r.EndsWith("/api/elements/set", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(handler.Requests, r => r.EndsWith("/api/snapshot", StringComparison.OrdinalIgnoreCase));
        // The CLI prints "Fix plan" (or a "no fixable issues" variant) — either is a valid no-write outcome.
        Assert.NotEmpty(text);
    }

    [Fact]
    public void Schema_RequiresConfirm()
    {
        var tool = new FixTool(MakeClient(new QueueHttpHandler()), allowWrites: true);

        var schema = tool.InputSchema.AsObject();
        var required = schema["required"]!.AsArray();
        var names = new HashSet<string>();
        foreach (var n in required) names.Add(n!.GetValue<string>());
        Assert.Contains("confirm", names);
    }

    [Fact]
    public async Task ToolsList_AlwaysIncludesFix_EvenWhenWritesDisabled()
    {
        var (server, _) = BuildServer(allowWrites: false, out var output);

        await server.HandleLineAsync(Request(1, "tools/list"), CancellationToken.None);

        var response = ReadOneResponse(output);
        var tools = response["result"]!["tools"]!.AsArray();
        var names = new HashSet<string>();
        foreach (var t in tools) names.Add(t!["name"]!.GetValue<string>());
        Assert.Contains("fix", names);
        Assert.Contains("rollback", names);
    }

    [Fact]
    public async Task ToolsCall_Fix_AllowWritesOff_ReturnsRefusalAsContent()
    {
        var (server, handler) = BuildServer(allowWrites: false, out var output);

        await server.HandleLineAsync(Request(2, "tools/call", new JsonObject
        {
            ["name"] = "fix",
            ["arguments"] = new JsonObject { ["confirm"] = true },
        }), CancellationToken.None);

        var response = ReadOneResponse(output);
        var text = response["result"]!["content"]!.AsArray()[0]!["text"]!.GetValue<string>();
        Assert.Equal(FixTool.DisabledMessage, text);
        Assert.Empty(handler.Requests);
    }

    // -- helpers ------------------------------------------------------------

    private JsonElement ReadOnlyJournalEntry()
    {
        var journalPath = Path.Combine(_tempDir, ".revitcli", "journal.jsonl");
        Assert.True(File.Exists(journalPath), $"expected journal at {journalPath}");
        var lines = File.ReadAllLines(journalPath);
        var line = Assert.Single(lines);
        return JsonDocument.Parse(line).RootElement.Clone();
    }

    private static void WriteProfile(string dir, string body)
    {
        File.WriteAllText(Path.Combine(dir, ".revitcli.yml"), body);
    }

    private (McpServer Server, QueueHttpHandler Handler) BuildServer(bool allowWrites, out StringWriter output)
    {
        var handler = new QueueHttpHandler();
        var client = MakeClient(handler);
        var input = new StringReader(string.Empty);
        output = new StringWriter();
        var server = McpServer.CreateDefault(client, input, output, TextWriter.Null, allowWrites);
        return (server, handler);
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

    private static JsonNode ReadOneResponse(StringWriter output)
    {
        var raw = output.ToString().Trim();
        Assert.False(string.IsNullOrEmpty(raw), "expected one response on the writer");
        var line = raw.Split('\n', 2)[0].TrimEnd('\r');
        return JsonNode.Parse(line)!;
    }
}
