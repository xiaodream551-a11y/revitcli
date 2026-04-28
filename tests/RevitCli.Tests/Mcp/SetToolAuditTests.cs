using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using RevitCli.Client;
using RevitCli.Mcp.Tools;
using RevitCli.Shared;
using Xunit;

namespace RevitCli.Tests.Mcp;

/// <summary>
/// Verifies that MCP <c>set</c> invocations leave an audit entry in
/// <c>.revitcli/journal.jsonl</c> for every outcome (success, refusal, error)
/// and — critically — that the entry never contains the new parameter
/// <c>value</c> nor the <c>filter</c> expression body. An LLM-driven caller
/// could be coaxed into echoing secrets through those fields, so they are
/// excluded by design.
///
/// Tests in this class mutate the process-global <see cref="Environment.CurrentDirectory"/>
/// (because <see cref="RevitCli.Output.JournalLogger"/> resolves the journal
/// path relative to cwd when no profile is discoverable). They are placed in a
/// non-parallel collection to keep that mutation safe.
/// </summary>
[Collection("CwdMutating")]
public class SetToolAuditTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _originalCwd;

    public SetToolAuditTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"revitcli-set-audit-{Guid.NewGuid():N}");
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
    public async Task DisabledRefusal_WritesAuditEntry_WithoutValueOrFilter()
    {
        var tool = new SetTool(MakeClient(new QueueHttpHandler()), allowWrites: false);

        await tool.ExecuteAsync(new JsonObject
        {
            ["param"] = "Comments",
            ["value"] = "secret-token-shh",
            ["filter"] = "ProjectNumber == 'PRJ-001'",
            ["confirm"] = true,
            ["elementId"] = 1,
        }, CancellationToken.None);

        var entry = ReadOnlyJournalEntry();
        Assert.Equal("set", entry.GetProperty("action").GetString());
        Assert.Equal("mcp", entry.GetProperty("transport").GetString());
        Assert.Equal("refused-writes-disabled", entry.GetProperty("outcome").GetString());
        Assert.Equal("Comments", entry.GetProperty("param").GetString());
        Assert.Equal(1, entry.GetProperty("elementCount").GetInt32());
        AssertNoLeakage(entry);
    }

    [Fact]
    public async Task MissingConfirmRefusal_WritesAuditEntry_WithoutValueOrFilter()
    {
        var tool = new SetTool(MakeClient(new QueueHttpHandler()), allowWrites: true);

        await tool.ExecuteAsync(new JsonObject
        {
            ["param"] = "Mark",
            ["value"] = "secret-token-shh",
            ["filter"] = "Type == 'Sensitive'",
            ["elementId"] = 7,
        }, CancellationToken.None);

        var entry = ReadOnlyJournalEntry();
        Assert.Equal("refused-no-confirm", entry.GetProperty("outcome").GetString());
        Assert.Equal("Mark", entry.GetProperty("param").GetString());
        Assert.Equal(1, entry.GetProperty("elementCount").GetInt32());
        AssertNoLeakage(entry);
    }

    [Fact]
    public async Task SuccessfulWrite_WritesAuditEntry_WithAffectedCount_AndNoValue()
    {
        var handler = new QueueHttpHandler();
        handler.Enqueue("/api/elements/set", ApiResponse<SetResult>.Ok(new SetResult
        {
            Affected = 2,
            Preview = new List<SetPreviewItem>(),
        }));
        var tool = new SetTool(MakeClient(handler), allowWrites: true);

        await tool.ExecuteAsync(new JsonObject
        {
            ["param"] = "Comments",
            ["value"] = "secret-token-shh",
            ["filter"] = "ProjectNumber == 'PRJ-001'",
            ["confirm"] = true,
            ["category"] = "walls",
            ["elementIds"] = new JsonArray(11, 22, 33),
        }, CancellationToken.None);

        var entry = ReadOnlyJournalEntry();
        Assert.Equal("ok", entry.GetProperty("outcome").GetString());
        Assert.Equal("Comments", entry.GetProperty("param").GetString());
        Assert.Equal("walls", entry.GetProperty("category").GetString());
        Assert.Equal(3, entry.GetProperty("elementCount").GetInt32());
        Assert.Equal(2, entry.GetProperty("affected").GetInt32());
        Assert.False(entry.GetProperty("dryRun").GetBoolean());
        AssertNoLeakage(entry);
    }

    [Fact]
    public async Task ServerError_WritesErrorAuditEntry_WithoutValueLeakage()
    {
        var handler = new QueueHttpHandler();
        handler.Enqueue("/api/elements/set", ApiResponse<SetResult>.Fail("Parameter not found"));
        var tool = new SetTool(MakeClient(handler), allowWrites: true);

        await tool.ExecuteAsync(new JsonObject
        {
            ["param"] = "DoesNotExist",
            ["value"] = "secret-token-shh",
            ["confirm"] = true,
            ["elementId"] = 1,
        }, CancellationToken.None);

        var entry = ReadOnlyJournalEntry();
        Assert.Equal("error", entry.GetProperty("outcome").GetString());
        Assert.Equal("DoesNotExist", entry.GetProperty("param").GetString());
        Assert.Equal("Parameter not found", entry.GetProperty("error").GetString());
        AssertNoLeakage(entry);
    }

    private JsonElement ReadOnlyJournalEntry()
    {
        var journalPath = Path.Combine(_tempDir, ".revitcli", "journal.jsonl");
        Assert.True(File.Exists(journalPath), $"expected journal at {journalPath}");
        var lines = File.ReadAllLines(journalPath);
        var line = Assert.Single(lines);
        return JsonDocument.Parse(line).RootElement.Clone();
    }

    private static void AssertNoLeakage(JsonElement entry)
    {
        // Round-trip back to a string so we can assert against the raw bytes
        // that hit disk — exactly what an attacker reading the journal would see.
        var raw = entry.GetRawText();
        Assert.DoesNotContain("secret-token-shh", raw);
        Assert.DoesNotContain("ProjectNumber", raw);
        Assert.DoesNotContain("Sensitive", raw);
    }

    private static RevitClient MakeClient(QueueHttpHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:17839") });
}
