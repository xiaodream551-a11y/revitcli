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
        var setupDir = Path.Combine(Path.GetTempPath(), $"revitcli-mcp-rollback-{Guid.NewGuid():N}");
        Directory.CreateDirectory(setupDir);
        _originalCwd = Environment.CurrentDirectory;
        Environment.CurrentDirectory = setupDir;
        // After the cd, ask the OS for the canonical form. On macOS,
        // /var is a symlink to /private/var, so Path.GetTempPath() and
        // Environment.CurrentDirectory after cd return different strings
        // for the same directory. Path.GetRelativePath does string
        // comparison, not filesystem resolution, so the two forms must
        // match for McpPathGuard to accept paths constructed against
        // _tempDir as in-bounds.
        _tempDir = Environment.CurrentDirectory;
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
        // The MCP rollback tool binds `baseline` to .revitcli/ — the
        // fixture must therefore live under that subtree, not at the
        // bare temp-dir root.
        var revitCliDir = Path.Combine(_tempDir, ".revitcli");
        Directory.CreateDirectory(revitCliDir);
        var baselinePath = Path.Combine(revitCliDir, "baseline.json");
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
        // The path is under .revitcli/ (so it clears the path guard) but
        // the file itself does not exist; the failure surfaces from
        // RollbackCommand and is recorded as outcome=error, not refusal.
        var revitCliDir = Path.Combine(_tempDir, ".revitcli");
        Directory.CreateDirectory(revitCliDir);

        var text = await tool.ExecuteAsync(new JsonObject
        {
            ["baseline"] = Path.Combine(revitCliDir, "does-not-exist.json"),
            ["confirm"] = true,
            ["dryRun"] = true,
        }, CancellationToken.None);

        Assert.Contains("not found", text);
        var entry = ReadOnlyJournalEntry();
        Assert.Equal("error", entry.GetProperty("outcome").GetString());
        Assert.Equal(1, entry.GetProperty("exitCode").GetInt32());
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("../../etc/passwd")]
    [InlineData("baseline.json")] // not under .revitcli/
    public async Task BaselineOutsideRevitCli_AuditsRefusalAndDoesNotInvokeCommand(string baselineValue)
    {
        // Path-traversal defense for the LLM-driven transport: only paths
        // resolving inside .revitcli/ are accepted. Empty .revitcli is not
        // required; the guard works on path resolution, not file existence.
        var handler = new QueueHttpHandler();
        var tool = new RollbackTool(MakeClient(handler), allowWrites: true);

        var text = await tool.ExecuteAsync(new JsonObject
        {
            ["baseline"] = baselineValue,
            ["confirm"] = true,
            ["dryRun"] = true,
        }, CancellationToken.None);

        Assert.Contains("must be a path under .revitcli/", text);
        Assert.Empty(handler.Requests);

        var entry = ReadOnlyJournalEntry();
        Assert.Equal("refused-path-out-of-bounds", entry.GetProperty("outcome").GetString());
        // The original (unbound) baseline string IS logged so forensic
        // readers can see what the LLM tried to access.
        Assert.Equal(baselineValue, entry.GetProperty("baseline").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EmptyOrWhitespaceBaseline_AuditsRefusalAndDoesNotInvokeCommand(string baselineValue)
    {
        // Required-arg enforcement: the JSON schema declares `baseline` as
        // required, but the MCP dispatcher does not validate schemas — args
        // pass straight through to ExecuteAsync. The wrapper must therefore
        // (a) reject the call locally and (b) still emit an audit entry so
        // forensic readers see EVERY tool invocation, including malformed
        // ones. Both empty and whitespace-only inputs hit this branch.
        var handler = new QueueHttpHandler();
        var tool = new RollbackTool(MakeClient(handler), allowWrites: true);

        var text = await tool.ExecuteAsync(new JsonObject
        {
            ["baseline"] = baselineValue,
            ["confirm"] = true,
            ["dryRun"] = true,
        }, CancellationToken.None);

        Assert.Equal("Error: 'baseline' is required.", text);
        // The wrapper short-circuits before delegating to RollbackCommand,
        // so no HTTP traffic is generated.
        Assert.Empty(handler.Requests);

        var entry = ReadOnlyJournalEntry();
        Assert.Equal("rollback", entry.GetProperty("action").GetString());
        Assert.Equal("mcp", entry.GetProperty("transport").GetString());
        Assert.Equal("refused-missing-baseline", entry.GetProperty("outcome").GetString());
        // exitCode is null on refusal paths and is omitted by JournalLogger's
        // WhenWritingNull serializer policy — assert absence, not value.
        Assert.False(entry.TryGetProperty("exitCode", out _));
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
