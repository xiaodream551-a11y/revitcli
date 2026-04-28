using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using RevitCli.Client;
using RevitCli.Mcp;
using RevitCli.Shared;
using Xunit;

namespace RevitCli.Tests.Mcp;

/// <summary>
/// End-to-end coverage for MCP progress notifications (spec 2024-11-05).
/// The client opts in by passing <c>_meta.progressToken</c> in the
/// <c>tools/call</c> request; the server emits one or more
/// <c>notifications/progress</c> messages on the same stdio stream
/// before the final response, then writes the response.
///
/// PublishTool is the concrete <see cref="RevitCli.Mcp.Tools.IMcpProgressTool"/>
/// implementor. The tests below verify the protocol plumbing without
/// retesting the publish pipeline itself; the dryRun-with-no-profile
/// path is enough to drive both notifications (start + finish) plus the
/// final response.
/// </summary>
[Collection("CwdMutating")]
public class McpProgressTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _originalCwd;

    public McpProgressTests()
    {
        var setupDir = Path.Combine(Path.GetTempPath(), $"revitcli-mcp-progress-{Guid.NewGuid():N}");
        Directory.CreateDirectory(setupDir);
        _originalCwd = Environment.CurrentDirectory;
        Environment.CurrentDirectory = setupDir;
        _tempDir = Environment.CurrentDirectory;
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
    public async Task PublishWithProgressToken_EmitsStartAndFinishNotifications()
    {
        // No profile written -> publish errors with "no .revitcli.yml"
        // and exits 1. That's enough to drive both progress events
        // (start emits before the work, finish emits after) without
        // needing a full export pipeline mock.
        var (server, _) = BuildServer(allowWrites: true, out var output);

        await server.HandleLineAsync(Request(7, "tools/call", new JsonObject
        {
            ["name"] = "publish",
            ["arguments"] = new JsonObject { ["confirm"] = true, ["dryRun"] = true },
            ["_meta"] = new JsonObject { ["progressToken"] = "tok-abc" },
        }), CancellationToken.None);

        var lines = SplitOutput(output);
        var notifications = lines.Where(IsProgressNotification).ToList();
        Assert.Equal(2, notifications.Count);

        // First: progress=0 with a "starting" message
        var first = JsonNode.Parse(notifications[0])!;
        Assert.Equal("notifications/progress", first["method"]!.GetValue<string>());
        Assert.Equal("tok-abc", first["params"]!["progressToken"]!.GetValue<string>());
        Assert.Equal(0d, first["params"]!["progress"]!.GetValue<double>());
        Assert.Contains("starting", first["params"]!["message"]!.GetValue<string>());

        // Second: progress=1, total=1, "complete (exit=...)"
        var second = JsonNode.Parse(notifications[1])!;
        Assert.Equal("notifications/progress", second["method"]!.GetValue<string>());
        Assert.Equal(1d, second["params"]!["progress"]!.GetValue<double>());
        Assert.Equal(1d, second["params"]!["total"]!.GetValue<double>());
        Assert.Contains("complete", second["params"]!["message"]!.GetValue<string>());

        // Notifications come before the response — the order matters
        // because clients ingest the stream linearly.
        var responseIdx = lines.FindIndex(IsResponse);
        Assert.True(responseIdx >= 0, "expected a JSON-RPC response on the stream");
        var firstNotificationIdx = lines.FindIndex(IsProgressNotification);
        Assert.True(firstNotificationIdx < responseIdx,
            "progress notifications must precede the final response");
    }

    [Fact]
    public async Task PublishWithoutProgressToken_EmitsNoNotifications()
    {
        // Same call, no _meta.progressToken -> the server must NOT emit
        // any notifications (spec: client opts in; absence of token
        // means "I don't want progress").
        var (server, _) = BuildServer(allowWrites: true, out var output);

        await server.HandleLineAsync(Request(8, "tools/call", new JsonObject
        {
            ["name"] = "publish",
            ["arguments"] = new JsonObject { ["confirm"] = true, ["dryRun"] = true },
        }), CancellationToken.None);

        var lines = SplitOutput(output);
        Assert.DoesNotContain(lines, IsProgressNotification);
        Assert.Contains(lines, IsResponse);
    }

    [Fact]
    public async Task NonProgressTool_WithProgressToken_StillSucceedsButEmitsNothing()
    {
        // StatusTool is plain IMcpTool, NOT IMcpProgressTool. Per spec
        // the server must accept the token gracefully — no error — and
        // simply not emit notifications. Passing the token is the
        // client's prerogative, even when the tool doesn't support it.
        var handler = new QueueHttpHandler();
        handler.Enqueue("/api/status", ApiResponse<StatusInfo>.Ok(new StatusInfo
        {
            RevitVersion = "2026",
            DocumentName = "test.rvt",
        }));
        var (server, _) = BuildServer(allowWrites: false, handler, out var output);

        await server.HandleLineAsync(Request(9, "tools/call", new JsonObject
        {
            ["name"] = "status",
            ["arguments"] = new JsonObject(),
            ["_meta"] = new JsonObject { ["progressToken"] = 42 },
        }), CancellationToken.None);

        var lines = SplitOutput(output);
        Assert.DoesNotContain(lines, IsProgressNotification);
        // The response is still produced (status returned its summary text).
        Assert.Contains(lines, IsResponse);
    }

    [Fact]
    public async Task NumericProgressToken_RoundTripsAsNumber()
    {
        // Per spec, progressToken can be a string OR an integer. We
        // preserve whatever the client sent in the notification's
        // `progressToken` field. Test the integer path.
        var (server, _) = BuildServer(allowWrites: true, out var output);

        await server.HandleLineAsync(Request(10, "tools/call", new JsonObject
        {
            ["name"] = "publish",
            ["arguments"] = new JsonObject { ["confirm"] = true, ["dryRun"] = true },
            ["_meta"] = new JsonObject { ["progressToken"] = 42 },
        }), CancellationToken.None);

        var notifications = SplitOutput(output).Where(IsProgressNotification).ToList();
        Assert.NotEmpty(notifications);
        var token = JsonNode.Parse(notifications[0])!["params"]!["progressToken"]!;
        Assert.Equal(42, token.GetValue<int>());
    }

    // -- helpers ------------------------------------------------------------

    private static bool IsProgressNotification(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        try
        {
            var node = JsonNode.Parse(line);
            return node?["method"]?.GetValue<string>() == "notifications/progress";
        }
        catch { return false; }
    }

    private static bool IsResponse(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        try
        {
            var node = JsonNode.Parse(line);
            // A response has an "id" field (notifications do not).
            return node?["id"] is not null && (node["result"] is not null || node["error"] is not null);
        }
        catch { return false; }
    }

    private static System.Collections.Generic.List<string> SplitOutput(StringWriter output)
        => output.ToString().Split('\n').Select(l => l.TrimEnd('\r')).ToList();

    private (McpServer Server, QueueHttpHandler Handler) BuildServer(bool allowWrites, out StringWriter output)
        => BuildServer(allowWrites, new QueueHttpHandler(), out output);

    private (McpServer Server, QueueHttpHandler Handler) BuildServer(bool allowWrites, QueueHttpHandler handler, out StringWriter output)
    {
        var client = new RevitClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:17839") });
        var input = new StringReader(string.Empty);
        output = new StringWriter();
        var server = McpServer.CreateDefault(client, input, output, TextWriter.Null, allowWrites);
        return (server, handler);
    }

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
