using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using RevitCli.Client;
using RevitCli.Fix;
using RevitCli.Mcp;
using RevitCli.Mcp.Tools;
using RevitCli.Shared;
using Xunit;

namespace RevitCli.Tests.Mcp;

/// <summary>
/// Verifies the MCP <c>rollback</c> tool — gate + audit log + dispatcher.
/// Same cwd-mutation pattern as <see cref="McpFixToolTests"/>.
/// </summary>
[Collection("CwdMutating")]
public class McpRollbackToolTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _originalCwd;

    public McpRollbackToolTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"revitcli-mcp-rollback-{Guid.NewGuid():N}");
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
        var tool = new RollbackTool(MakeClient(new QueueHttpHandler()), allowWrites: false);

        var text = await tool.ExecuteAsync(new JsonObject
        {
            ["baseline"] = "irrelevant.json",
            ["confirm"] = true,
            ["dryRun"] = true,
        }, CancellationToken.None);

        Assert.Equal(RollbackTool.DisabledMessage, text);
        var entry = ReadOnlyJournalEntry();
        Assert.Equal("rollback", entry.GetProperty("action").GetString());
        Assert.Equal("mcp", entry.GetProperty("transport").GetString());
        Assert.Equal("refused-writes-disabled", entry.GetProperty("outcome").GetString());
        Assert.Equal("irrelevant.json", entry.GetProperty("baseline").GetString());
    }

    [Fact]
    public async Task NoConfirm_ReturnsRefusalAndLogsAudit()
    {
        var tool = new RollbackTool(MakeClient(new QueueHttpHandler()), allowWrites: true);

        var text = await tool.ExecuteAsync(new JsonObject
        {
            ["baseline"] = "irrelevant.json",
            ["dryRun"] = true,
        }, CancellationToken.None);

        Assert.Equal(RollbackTool.ConfirmMessage, text);
        var entry = ReadOnlyJournalEntry();
        Assert.Equal("refused-no-confirm", entry.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task DryRun_WithValidEmptyJournal_LogsOk_NoSetCalls()
    {
        var baselinePath = Path.Combine(_tempDir, "baseline.json");
        File.WriteAllText(baselinePath, JsonSerializer.Serialize(new ModelSnapshot()));
        // Persist an empty fix journal next to the baseline so the rollback
        // command's journal-matching invariant is satisfied.
        FixJournalStore.SaveForBaseline(baselinePath, new FixJournal
        {
            CheckName = "default",
            BaselinePath = Path.GetFullPath(baselinePath),
            StartedAt = DateTime.UtcNow.ToString("o"),
            Actions = new List<FixAction>(),
        });

        var handler = new QueueHttpHandler();
        var tool = new RollbackTool(MakeClient(handler), allowWrites: true);

        var text = await tool.ExecuteAsync(new JsonObject
        {
            ["baseline"] = baselinePath,
            ["confirm"] = true,
            ["dryRun"] = true,
        }, CancellationToken.None);

        var entry = ReadOnlyJournalEntry();
        Assert.Equal("ok", entry.GetProperty("outcome").GetString());
        Assert.Equal(0, entry.GetProperty("exitCode").GetInt32());
        Assert.True(entry.GetProperty("dryRun").GetBoolean());
        // Dry-run with empty journal does no /api/ calls.
        Assert.DoesNotContain(handler.Requests, r => r.EndsWith("/api/elements/set", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Dry run: 0 rollback action", text);
    }

    [Fact]
    public async Task MissingBaseline_ReturnsErrorAndLogsError()
    {
        var tool = new RollbackTool(MakeClient(new QueueHttpHandler()), allowWrites: true);

        var text = await tool.ExecuteAsync(new JsonObject
        {
            ["baseline"] = Path.Combine(_tempDir, "does-not-exist.json"),
            ["confirm"] = true,
            ["dryRun"] = true,
        }, CancellationToken.None);

        Assert.Contains("not found", text);
        var entry = ReadOnlyJournalEntry();
        Assert.Equal("error", entry.GetProperty("outcome").GetString());
        Assert.Equal(1, entry.GetProperty("exitCode").GetInt32());
    }

    [Fact]
    public void Schema_RequiresBaselineAndConfirm()
    {
        var tool = new RollbackTool(MakeClient(new QueueHttpHandler()), allowWrites: true);

        var schema = tool.InputSchema.AsObject();
        var required = schema["required"]!.AsArray();
        var names = new HashSet<string>();
        foreach (var n in required) names.Add(n!.GetValue<string>());
        Assert.Contains("baseline", names);
        Assert.Contains("confirm", names);
    }

    [Fact]
    public async Task ToolsCall_Rollback_AllowWritesOff_ReturnsRefusalAsContent()
    {
        var handler = new QueueHttpHandler();
        var input = new StringReader(string.Empty);
        var output = new StringWriter();
        var server = McpServer.CreateDefault(MakeClient(handler), input, output, TextWriter.Null, allowWrites: false);

        await server.HandleLineAsync(Request(2, "tools/call", new JsonObject
        {
            ["name"] = "rollback",
            ["arguments"] = new JsonObject
            {
                ["baseline"] = "anything.json",
                ["confirm"] = true,
            },
        }), CancellationToken.None);

        var response = JsonNode.Parse(output.ToString().Trim().Split('\n', 2)[0].TrimEnd('\r'))!;
        var text = response["result"]!["content"]!.AsArray()[0]!["text"]!.GetValue<string>();
        Assert.Equal(RollbackTool.DisabledMessage, text);
        Assert.Empty(handler.Requests);
    }

    private JsonElement ReadOnlyJournalEntry()
    {
        var journalPath = Path.Combine(_tempDir, ".revitcli", "journal.jsonl");
        Assert.True(File.Exists(journalPath), $"expected journal at {journalPath}");
        var lines = File.ReadAllLines(journalPath);
        var line = Assert.Single(lines);
        return JsonDocument.Parse(line).RootElement.Clone();
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
