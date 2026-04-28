using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using RevitCli.Client;
using RevitCli.Mcp;
using RevitCli.Mcp.Tools;
using Xunit;

namespace RevitCli.Tests.Mcp;

/// <summary>
/// End-to-end coverage for the bidirectional MCP path that powers
/// <c>sampling/createMessage</c> (spec 2024-11-05). The client opts in
/// by advertising <c>capabilities.sampling</c> during <c>initialize</c>;
/// the server then issues <c>sampling/createMessage</c> requests
/// server-to-client. This file exercises the full round-trip with a
/// fake-stdin pattern: drive a tool call, wait for the synthesized
/// outbound request to land on the writer, then synthesize the
/// client's response back through <see cref="McpServer.HandleLineAsync"/>.
/// </summary>
public class McpSamplingTests
{
    /// <summary>
    /// A tool that asks the LLM to summarize one phrase and returns
    /// whatever the client said. Lets us drive the sampling round-trip
    /// without depending on any production tool implementing it yet.
    /// </summary>
    private sealed class EchoSamplingTool : IMcpSamplingTool
    {
        public string Name => "echo-sample";
        public string Description => "test-only sampling round-trip";
        public JsonNode InputSchema => new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["prompt"] = new JsonObject { ["type"] = "string" },
            },
        };

        public Task<string> ExecuteAsync(JsonNode? arguments, CancellationToken cancellationToken)
            => ExecuteAsync(arguments, cancellationToken, NullMcpSamplingClient.Instance);

        public async Task<string> ExecuteAsync(JsonNode? arguments, CancellationToken cancellationToken, IMcpSamplingClient sampling)
        {
            if (!sampling.IsSupported)
                return "sampling-not-supported";

            var prompt = (arguments as JsonObject)?["prompt"]?.GetValue<string>() ?? "(none)";
            var msg = new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonObject { ["type"] = "text", ["text"] = prompt },
            };
            var result = await sampling.CreateMessageAsync(
                new[] { msg },
                new SamplingOptions { MaxTokens = 32 },
                cancellationToken);
            return $"reply={result.Text}";
        }
    }

    [Fact]
    public async Task ClientWithoutSamplingCapability_IsSupportedFalse_ToolDegrades()
    {
        var (server, _) = BuildServer(out var output, sampling: false);

        // Client did NOT advertise sampling — the tool should see
        // IsSupported=false and not even attempt a CreateMessage call.
        await server.HandleLineAsync(Request(1, "tools/call", new JsonObject
        {
            ["name"] = "echo-sample",
            ["arguments"] = new JsonObject { ["prompt"] = "hi" },
        }), CancellationToken.None);

        var resp = ReadOneResponse(output);
        var text = resp["result"]!["content"]!.AsArray()[0]!["text"]!.GetValue<string>();
        Assert.Equal("sampling-not-supported", text);
    }

    [Fact]
    public async Task ClientWithSamplingCapability_RoundTripsThroughDispatcher()
    {
        var (server, _) = BuildServer(out var output, sampling: true);

        // Drive the tool call in the background; the server will emit
        // a sampling/createMessage request, then block awaiting the
        // matching response. We synthesize that response below.
        var toolTask = server.HandleLineAsync(Request(7, "tools/call", new JsonObject
        {
            ["name"] = "echo-sample",
            ["arguments"] = new JsonObject { ["prompt"] = "wave hello" },
        }), CancellationToken.None);

        // Wait for the outbound sampling request to appear on the writer.
        var outbound = await WaitForOutboundRequestAsync(output, "sampling/createMessage");
        Assert.Equal("sampling/createMessage", outbound["method"]!.GetValue<string>());
        var outboundId = outbound["id"]!.GetValue<string>();
        Assert.StartsWith("srv-", outboundId);
        Assert.Equal(32, outbound["params"]!["maxTokens"]!.GetValue<int>());
        Assert.Equal("wave hello", outbound["params"]!["messages"]![0]!["content"]!["text"]!.GetValue<string>());

        // Synthesize the client's response.
        await server.HandleLineAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = outboundId,
            ["result"] = new JsonObject
            {
                ["role"] = "assistant",
                ["model"] = "claude-sonnet-4-6",
                ["stopReason"] = "end_turn",
                ["content"] = new JsonObject { ["type"] = "text", ["text"] = "👋 hello back" },
            },
        }.ToJsonString(), CancellationToken.None);

        await toolTask;
        var toolResp = ReadResponseWithId(output, 7);
        var toolText = toolResp["result"]!["content"]!.AsArray()[0]!["text"]!.GetValue<string>();
        Assert.Equal("reply=👋 hello back", toolText);
    }

    [Fact]
    public async Task SamplingRequest_WithErrorResponse_SurfacesAsContentText()
    {
        var (server, _) = BuildServer(out var output, sampling: true);

        var toolTask = server.HandleLineAsync(Request(8, "tools/call", new JsonObject
        {
            ["name"] = "echo-sample",
            ["arguments"] = new JsonObject { ["prompt"] = "anything" },
        }), CancellationToken.None);

        var outbound = await WaitForOutboundRequestAsync(output, "sampling/createMessage");
        var id = outbound["id"]!.GetValue<string>();

        // Client refuses (e.g. user denied the prompt).
        await server.HandleLineAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["error"] = new JsonObject { ["code"] = -32603, ["message"] = "user refused" },
        }.ToJsonString(), CancellationToken.None);

        await toolTask;
        var toolResp = ReadResponseWithId(output, 8);
        // The dispatcher catches our SamplingNotSupportedException as an
        // unhandled tool exception (per HandleToolCallAsync's catch block),
        // which surfaces as content text + isError=true.
        Assert.True(toolResp["result"]!["isError"]!.GetValue<bool>());
        var toolText = toolResp["result"]!["content"]!.AsArray()[0]!["text"]!.GetValue<string>();
        Assert.Contains("user refused", toolText);
    }

    [Fact]
    public async Task StaleResponse_WithUnknownId_IsSilentlyDropped_NotEchoedAsError()
    {
        // A response with an id we never sent should be logged and
        // dropped — NOT bounced back as a JSON-RPC error. Echoing one
        // would violate JSON-RPC §6 (responses don't get responses).
        var (server, _) = BuildServer(out var output, sampling: true);

        await server.HandleLineAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = "srv-9999",
            ["result"] = new JsonObject { ["role"] = "assistant", ["content"] = new JsonObject { ["type"] = "text", ["text"] = "" } },
        }.ToJsonString(), CancellationToken.None);

        // Writer should remain empty: no error response sent back.
        Assert.Empty(output.ToString());
    }

    [Fact]
    public async Task CapabilityFromInitialize_IsCapturedBeforeFirstToolCall()
    {
        var (server, _) = BuildServer(out var output, sampling: false);

        // Client initialized WITHOUT sampling first.
        await server.HandleLineAsync(Request(1, "initialize", new JsonObject
        {
            ["protocolVersion"] = "2024-11-05",
            ["clientInfo"] = new JsonObject { ["name"] = "test", ["version"] = "0" },
            ["capabilities"] = new JsonObject(),
        }), CancellationToken.None);

        // Now re-initialize WITH sampling — capability state must reset
        // to support a re-handshake from a different client.
        await server.HandleLineAsync(Request(2, "initialize", new JsonObject
        {
            ["protocolVersion"] = "2024-11-05",
            ["clientInfo"] = new JsonObject { ["name"] = "test", ["version"] = "0" },
            ["capabilities"] = new JsonObject { ["sampling"] = new JsonObject() },
        }), CancellationToken.None);

        // Tool call now sees IsSupported=true and proceeds with sampling.
        var toolTask = server.HandleLineAsync(Request(3, "tools/call", new JsonObject
        {
            ["name"] = "echo-sample",
            ["arguments"] = new JsonObject { ["prompt"] = "ping" },
        }), CancellationToken.None);

        var outbound = await WaitForOutboundRequestAsync(output, "sampling/createMessage");
        var id = outbound["id"]!.GetValue<string>();
        await server.HandleLineAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["result"] = new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = new JsonObject { ["type"] = "text", ["text"] = "pong" },
            },
        }.ToJsonString(), CancellationToken.None);

        await toolTask;
        var resp = ReadResponseWithId(output, 3);
        Assert.Equal("reply=pong", resp["result"]!["content"]!.AsArray()[0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task SamplingRequest_HonorsCancellation_DropsPendingEntry()
    {
        var (server, _) = BuildServer(out var output, sampling: true);
        using var cts = new CancellationTokenSource();

        var toolTask = Task.Run(() => server.HandleLineAsync(Request(11, "tools/call", new JsonObject
        {
            ["name"] = "echo-sample",
            ["arguments"] = new JsonObject { ["prompt"] = "x" },
        }), cts.Token));

        await WaitForOutboundRequestAsync(output, "sampling/createMessage");
        cts.Cancel();

        // The tool task completes — the dispatcher catches the
        // OperationCanceledException as a tool failure and writes a
        // content-text response with isError=true.
        await toolTask;
        var resp = ReadResponseWithId(output, 11);
        Assert.True(resp["result"]!["isError"]!.GetValue<bool>());
    }

    [Fact]
    public async Task NullSamplingClient_AlwaysReportsUnsupported_AndThrowsOnCall()
    {
        Assert.False(NullMcpSamplingClient.Instance.IsSupported);
        await Assert.ThrowsAsync<SamplingNotSupportedException>(() =>
            NullMcpSamplingClient.Instance.CreateMessageAsync(
                new[] { new JsonObject { ["role"] = "user" } }));
    }

    // ─── helpers ──────────────────────────────────────────────────────────

    private (McpServer Server, EchoSamplingTool EchoTool) BuildServer(out StringWriter output, bool sampling)
    {
        var handler = new QueueHttpHandler();
        var client = new RevitClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:17839") });
        var input = new StringReader(string.Empty);
        output = new StringWriter();
        var echo = new EchoSamplingTool();
        var server = new McpServer(new IMcpTool[] { echo }, input, output, TextWriter.Null);

        // Pretend the client did the initialize handshake with the
        // requested capability shape. Synthesize the message rather
        // than calling the public CreateDefault path so each test runs
        // with exactly one tool registered.
        var initParams = new JsonObject
        {
            ["protocolVersion"] = "2024-11-05",
            ["clientInfo"] = new JsonObject { ["name"] = "test", ["version"] = "0" },
            ["capabilities"] = sampling
                ? new JsonObject { ["sampling"] = new JsonObject() }
                : new JsonObject(),
        };
        // Run the init synchronously so the capability flag is set
        // before any test method touches the server.
        server.HandleLineAsync(Request(0, "initialize", initParams), CancellationToken.None).GetAwaiter().GetResult();
        // Drain the init response so per-test reads see only the
        // messages they generate.
        output.GetStringBuilder().Clear();
        return (server, echo);
    }

    private static async Task<JsonNode> WaitForOutboundRequestAsync(StringWriter output, string method, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var lines = output.ToString().Split('\n');
            foreach (var line in lines)
            {
                var trimmed = line.TrimEnd('\r');
                if (string.IsNullOrEmpty(trimmed)) continue;
                JsonNode? node;
                try { node = JsonNode.Parse(trimmed); }
                catch { continue; }
                if (node?["method"]?.GetValue<string>() == method)
                    return node;
            }
            await Task.Delay(10);
        }
        throw new Xunit.Sdk.XunitException($"timed out waiting for outbound {method} on the writer; saw: {output}");
    }

    private static JsonNode ReadOneResponse(StringWriter output)
    {
        var line = output.ToString().Split('\n').Select(l => l.TrimEnd('\r')).First(l => !string.IsNullOrEmpty(l));
        return JsonNode.Parse(line)!;
    }

    private static JsonNode ReadResponseWithId(StringWriter output, int id)
    {
        // Match a RESPONSE (has result/error, no method) whose id is the
        // numeric we expect. Skip outbound requests on the same writer
        // (those have "method" and string "srv-N" ids).
        foreach (var raw in output.ToString().Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (string.IsNullOrEmpty(line)) continue;
            JsonNode? node;
            try { node = JsonNode.Parse(line); }
            catch { continue; }
            if (node is not JsonObject obj) continue;
            if (obj.ContainsKey("method")) continue; // outbound request, not a response
            if (obj["id"] is not JsonNode idNode) continue;
            if (idNode.GetValueKind() != System.Text.Json.JsonValueKind.Number) continue;
            if (idNode.GetValue<int>() == id) return obj;
        }
        throw new Xunit.Sdk.XunitException($"no response with id={id} found in writer; saw: {output}");
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
