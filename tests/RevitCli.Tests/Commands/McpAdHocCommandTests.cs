using System;
using System.Collections.Generic;
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
/// Tests for the ad-hoc <c>mcp list</c>, <c>mcp test</c>, and
/// <c>mcp resource</c> subcommands. These commands spin up an
/// in-process <see cref="RevitCli.Mcp.McpServer"/> via
/// <c>CreateDefault</c> — the same graph production runs — so what
/// passes here is what an LLM client would see.
///
/// We use a queue handler to feed canned responses for any HTTP
/// calls the tools/resources make, exactly matching how
/// <c>McpServerTests</c> mocks the addin layer.
/// </summary>
public class McpAdHocCommandTests
{
    // ─── list ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task List_Table_IncludesAllRegisteredToolsAndResources()
    {
        var (client, _) = MakeClientAndHandler();
        var output = new StringWriter();

        var exit = await McpCommand.ExecuteListAsync(client, allowWrites: false, "table", output);

        Assert.Equal(0, exit);
        var text = output.ToString();
        // 9 tools end-to-end (4 read + 5 write). Spot-check the boundary
        // ones rather than full-listing — the registry order isn't a
        // stability contract worth pinning.
        Assert.Contains("status", text);
        Assert.Contains("query", text);
        Assert.Contains("set", text);
        Assert.Contains("publish", text);
        // 5 resources.
        Assert.Contains("revitcli://snapshot/latest", text);
        Assert.Contains("revitcli://categories", text);
        Assert.Contains("revitcli://journal/recent", text);
    }

    [Fact]
    public async Task List_Json_IsParseableAndCarriesAllowWritesFlag()
    {
        var (client, _) = MakeClientAndHandler();
        var output = new StringWriter();

        await McpCommand.ExecuteListAsync(client, allowWrites: true, "json", output);

        using var doc = JsonDocument.Parse(output.ToString());
        Assert.True(doc.RootElement.GetProperty("allowWrites").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("tools").GetArrayLength() >= 9);
        Assert.True(doc.RootElement.GetProperty("resources").GetArrayLength() >= 5);
    }

    // ─── test ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Test_Status_ReturnsContentText_ExitsZero()
    {
        var (client, handler) = MakeClientAndHandler();
        handler.Enqueue("/api/status", ApiResponse<StatusInfo>.Ok(new StatusInfo
        {
            RevitVersion = "2026",
            DocumentName = "Project1.rvt",
            DocumentPath = @"C:\models\Project1.rvt",
        }));
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await McpCommand.ExecuteTestAsync(client, "status",
            argsJson: null, allowWrites: false, verbose: false, stdout, stderr);

        Assert.Equal(0, exit);
        Assert.Contains("2026", stdout.ToString());
        Assert.Empty(stderr.ToString());
    }

    [Fact]
    public async Task Test_SetWithoutAllowWrites_ReturnsRefusalText_ExitsZero()
    {
        // The MCP server returns the refusal as content-text + isError=false
        // (refusal is a soft no, not a protocol error). Exit code stays 0
        // because the dispatch succeeded; the LLM/operator reads the text
        // for the actual outcome.
        var (client, _) = MakeClientAndHandler();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await McpCommand.ExecuteTestAsync(client, "set",
            argsJson: """{"param":"Mark","value":"x","confirm":true,"elementId":1}""",
            allowWrites: false, verbose: false, stdout, stderr);

        Assert.Equal(0, exit);
        Assert.Contains("Write tools are disabled", stdout.ToString());
    }

    [Fact]
    public async Task Test_InvalidJsonArgs_ExitsOneWithStderrDiagnostic()
    {
        var (client, _) = MakeClientAndHandler();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await McpCommand.ExecuteTestAsync(client, "status",
            argsJson: "not-json{{", allowWrites: false, verbose: false, stdout, stderr);

        Assert.Equal(1, exit);
        Assert.Contains("not valid JSON", stderr.ToString());
        Assert.Empty(stdout.ToString());
    }

    [Fact]
    public async Task Test_UnknownTool_SurfacesProtocolError()
    {
        var (client, _) = MakeClientAndHandler();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await McpCommand.ExecuteTestAsync(client, "no-such-tool",
            argsJson: null, allowWrites: false, verbose: false, stdout, stderr);

        Assert.Equal(1, exit);
        Assert.Contains("unknown tool", stderr.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Test_Verbose_PrintsFullJsonRpcResponse()
    {
        var (client, handler) = MakeClientAndHandler();
        handler.Enqueue("/api/status", ApiResponse<StatusInfo>.Ok(new StatusInfo
        {
            RevitVersion = "2026", DocumentName = "x.rvt", DocumentPath = "x"
        }));
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        await McpCommand.ExecuteTestAsync(client, "status",
            argsJson: null, allowWrites: false, verbose: true, stdout, stderr);

        // Verbose mode emits the JSON-RPC envelope, not just content text.
        using var doc = JsonDocument.Parse(stdout.ToString());
        Assert.Equal(1, doc.RootElement.GetProperty("id").GetInt32());
        Assert.True(doc.RootElement.TryGetProperty("result", out _));
    }

    // ─── resource ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Resource_ReturnsBodyText()
    {
        var (client, handler) = MakeClientAndHandler();
        handler.Enqueue("/api/snapshot", ApiResponse<ModelSnapshot>.Ok(new ModelSnapshot
        {
            Revit = new SnapshotRevit { Document = "Demo.rvt" },
            Summary = new SnapshotSummary
            {
                ElementCounts = new Dictionary<string, int> { ["Walls"] = 5 }
            },
        }));
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await McpCommand.ExecuteResourceAsync(client,
            "revitcli://categories", verbose: false, stdout, stderr);

        Assert.Equal(0, exit);
        // The categories resource emits a JSON projection — Walls 5 must
        // appear somewhere in the printed body.
        Assert.Contains("Walls", stdout.ToString());
    }

    [Fact]
    public async Task Resource_UnknownUri_ExitsOneWithStderrDiagnostic()
    {
        var (client, _) = MakeClientAndHandler();
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await McpCommand.ExecuteResourceAsync(client,
            "revitcli://no-such/uri", verbose: false, stdout, stderr);

        Assert.Equal(1, exit);
        Assert.Contains("unknown URI", stderr.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Resource_Verbose_PrintsFullJsonRpcResponse()
    {
        var (client, handler) = MakeClientAndHandler();
        handler.Enqueue("/api/snapshot", ApiResponse<ModelSnapshot>.Ok(new ModelSnapshot
        {
            Summary = new SnapshotSummary
            {
                ElementCounts = new Dictionary<string, int> { ["Walls"] = 1 }
            },
        }));
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        await McpCommand.ExecuteResourceAsync(client,
            "revitcli://categories", verbose: true, stdout, stderr);

        using var doc = JsonDocument.Parse(stdout.ToString());
        Assert.True(doc.RootElement.TryGetProperty("result", out _));
        var contents = doc.RootElement.GetProperty("result").GetProperty("contents");
        Assert.True(contents.GetArrayLength() >= 1);
    }

    // ─── helper ───────────────────────────────────────────────────────────

    private static (RevitClient Client, RevitCli.Tests.Mcp.QueueHttpHandler Handler) MakeClientAndHandler()
    {
        var handler = new RevitCli.Tests.Mcp.QueueHttpHandler();
        var client = new RevitClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:17839") });
        return (client, handler);
    }
}
