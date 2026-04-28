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
/// Verifies the MCP <c>import</c> tool — gate, audit log, path-binding,
/// and dispatcher wiring. The orchestration (CSV parse, plan, apply loop)
/// stays in <see cref="RevitCli.Commands.ImportCommand"/> and is covered
/// by its own tests; this file only proves the MCP wrapper.
/// </summary>
[Collection("CwdMutating")]
public class McpImportToolTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _originalCwd;

    public McpImportToolTests()
    {
        var setupDir = Path.Combine(Path.GetTempPath(), $"revitcli-mcp-import-{Guid.NewGuid():N}");
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
        var tool = new ImportTool(MakeClient(new QueueHttpHandler()), allowWrites: false);

        var text = await tool.ExecuteAsync(new JsonObject
        {
            ["file"] = ".revitcli/import.csv",
            ["category"] = "doors",
            ["matchBy"] = "Mark",
            ["confirm"] = true,
            ["dryRun"] = true,
        }, CancellationToken.None);

        Assert.Equal(ImportTool.DisabledMessage, text);
        var entry = ReadOnlyJournalEntry();
        Assert.Equal("import", entry.GetProperty("action").GetString());
        Assert.Equal("mcp", entry.GetProperty("transport").GetString());
        Assert.Equal("refused-writes-disabled", entry.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task NoConfirm_RefusesAndLogsAudit()
    {
        var tool = new ImportTool(MakeClient(new QueueHttpHandler()), allowWrites: true);

        var text = await tool.ExecuteAsync(new JsonObject
        {
            ["file"] = ".revitcli/import.csv",
            ["category"] = "doors",
            ["matchBy"] = "Mark",
            ["dryRun"] = true,
        }, CancellationToken.None);

        Assert.Equal(ImportTool.ConfirmMessage, text);
        var entry = ReadOnlyJournalEntry();
        Assert.Equal("refused-no-confirm", entry.GetProperty("outcome").GetString());
    }

    [Theory]
    [InlineData("file", "category", "Mark", "refused-missing-file")]
    [InlineData(".revitcli/x.csv", "category", "", "refused-missing-matchBy")]
    [InlineData(".revitcli/x.csv", "", "Mark", "refused-missing-category")]
    public async Task MissingRequiredArg_RefusesAndLogsSpecificOutcome(
        string file, string category, string matchBy, string expectedOutcome)
    {
        var handler = new QueueHttpHandler();
        var tool = new ImportTool(MakeClient(handler), allowWrites: true);

        // The empty-string sentinel is detected via TryGetString returning
        // null, which the tool then maps to its specific refusal outcome.
        // Use null (omit key) for the missing case to get an empty string
        // through the JsonObject API.
        var args = new JsonObject { ["confirm"] = true, ["dryRun"] = true };
        if (file == "file") { /* sentinel name == "file" means omit */ }
        else args["file"] = file;
        if (!string.IsNullOrEmpty(category)) args["category"] = category;
        if (!string.IsNullOrEmpty(matchBy)) args["matchBy"] = matchBy;

        await tool.ExecuteAsync(args, CancellationToken.None);
        Assert.Empty(handler.Requests);
        var entry = ReadOnlyJournalEntry();
        Assert.Equal(expectedOutcome, entry.GetProperty("outcome").GetString());
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("../../etc/passwd")]
    [InlineData("import.csv")] // not under .revitcli/
    public async Task FileOutsideRevitCli_RefusedAndLogsBoundsViolation(string file)
    {
        var handler = new QueueHttpHandler();
        var tool = new ImportTool(MakeClient(handler), allowWrites: true);

        var text = await tool.ExecuteAsync(new JsonObject
        {
            ["file"] = file,
            ["category"] = "doors",
            ["matchBy"] = "Mark",
            ["confirm"] = true,
            ["dryRun"] = true,
        }, CancellationToken.None);

        Assert.Contains("must be a path under .revitcli/", text);
        Assert.Empty(handler.Requests);
        var entry = ReadOnlyJournalEntry();
        Assert.Equal("refused-path-out-of-bounds", entry.GetProperty("outcome").GetString());
        Assert.Equal(file, entry.GetProperty("file").GetString());
    }

    [Fact]
    public async Task DryRun_WithStagedCsv_LogsOk_NoSetCalls()
    {
        // Stage a tiny CSV that the import planner will accept. In dry-run
        // mode the tool calls /api/elements (query) but not /api/elements/set.
        var csvPath = Path.Combine(_tempDir, ".revitcli", "import.csv");
        File.WriteAllText(csvPath, "Mark,Comments\nD-1,hello\nD-2,world\n");

        var handler = new QueueHttpHandler();
        // Empty element list -> all rows become "miss"; with onMissing=warn
        // (default) the plan still completes with exitCode 0. The handler
        // matches request path only (query string is dropped at line 281).
        handler.Enqueue("/api/elements", ApiResponse<ElementInfo[]>.Ok(Array.Empty<ElementInfo>()));
        var tool = new ImportTool(MakeClient(handler), allowWrites: true);

        var text = await tool.ExecuteAsync(new JsonObject
        {
            ["file"] = csvPath,
            ["category"] = "doors",
            ["matchBy"] = "Mark",
            ["confirm"] = true,
            ["dryRun"] = true,
        }, CancellationToken.None);

        var entry = ReadOnlyJournalEntry();
        Assert.Equal("ok", entry.GetProperty("outcome").GetString());
        Assert.True(entry.GetProperty("dryRun").GetBoolean());
        Assert.Equal(0, entry.GetProperty("exitCode").GetInt32());
        Assert.DoesNotContain(handler.Requests, r => r.EndsWith("/api/elements/set", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Dry run", text);
    }

    [Fact]
    public void Schema_DeclaresRequiredFields()
    {
        var tool = new ImportTool(MakeClient(new QueueHttpHandler()), allowWrites: true);
        var schema = tool.InputSchema.AsObject();
        var required = schema["required"]!.AsArray();
        var names = new HashSet<string>();
        foreach (var n in required) names.Add(n!.GetValue<string>());
        Assert.Contains("file", names);
        Assert.Contains("category", names);
        Assert.Contains("matchBy", names);
        Assert.Contains("confirm", names);
    }

    [Fact]
    public async Task ToolsList_IncludesImport_EvenWhenWritesDisabled()
    {
        var handler = new QueueHttpHandler();
        var input = new StringReader(string.Empty);
        var output = new StringWriter();
        var server = McpServer.CreateDefault(MakeClient(handler), input, output, TextWriter.Null, allowWrites: false);

        await server.HandleLineAsync(Request(1, "tools/list"), CancellationToken.None);

        var response = JsonNode.Parse(output.ToString().Trim().Split('\n', 2)[0].TrimEnd('\r'))!;
        var tools = response["result"]!["tools"]!.AsArray();
        var names = new HashSet<string>();
        foreach (var t in tools) names.Add(t!["name"]!.GetValue<string>());
        Assert.Contains("import", names);
    }

    [Fact]
    public async Task ToolsCall_Import_AllowWritesOff_ReturnsRefusalAsContent()
    {
        var handler = new QueueHttpHandler();
        var input = new StringReader(string.Empty);
        var output = new StringWriter();
        var server = McpServer.CreateDefault(MakeClient(handler), input, output, TextWriter.Null, allowWrites: false);

        await server.HandleLineAsync(Request(2, "tools/call", new JsonObject
        {
            ["name"] = "import",
            ["arguments"] = new JsonObject
            {
                ["file"] = ".revitcli/x.csv",
                ["category"] = "doors",
                ["matchBy"] = "Mark",
                ["confirm"] = true,
            },
        }), CancellationToken.None);

        var response = JsonNode.Parse(output.ToString().Trim().Split('\n', 2)[0].TrimEnd('\r'))!;
        var text = response["result"]!["content"]!.AsArray()[0]!["text"]!.GetValue<string>();
        Assert.Equal(ImportTool.DisabledMessage, text);
        Assert.Empty(handler.Requests);
    }

    private JsonElement ReadOnlyJournalEntry()
    {
        // Filter by action="import" — see comment in McpFixToolTests.
        var journalPath = Path.Combine(_tempDir, ".revitcli", "journal.jsonl");
        Assert.True(File.Exists(journalPath), $"expected journal at {journalPath}");
        foreach (var line in File.ReadAllLines(journalPath))
        {
            var entry = JsonDocument.Parse(line).RootElement.Clone();
            if (entry.TryGetProperty("action", out var a) && a.GetString() == "import")
                return entry;
        }
        throw new Xunit.Sdk.XunitException($"no action=import entry in {journalPath}");
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
