using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using RevitCli.Client;
using RevitCli.Mcp.Resources;
using RevitCli.Mcp.Tools;

namespace RevitCli.Mcp;

/// <summary>
/// stdio JSON-RPC 2.0 loop implementing the MCP 2024-11-05 server protocol.
///
/// Wire format: newline-delimited JSON, one message per line. This is the
/// transport baseline used by Claude Desktop / Cursor / Continue.
///
/// Scope:
/// <list type="bullet">
///   <item>Phase 1: <c>initialize</c>, <c>initialized</c>, <c>tools/list</c>,
///         <c>tools/call</c>, <c>ping</c>.</item>
///   <item>Phase 2: <c>resources/list</c>, <c>resources/read</c>, plus a
///         write-capable <c>set</c> tool gated by an <c>--allow-writes</c>
///         server flag combined with a per-call <c>confirm: true</c>
///         argument.</item>
/// </list>
/// Prompts and sampling remain out of scope (see roadmap §8 / §9.6).
/// </summary>
internal sealed class McpServer
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Dictionary<string, IMcpTool> _tools;
    private readonly Dictionary<string, IMcpResource> _resources;
    private readonly TextReader _input;
    private readonly TextWriter _output;
    /// <summary>
    /// Serializes writes to <see cref="_output"/>. The dispatch loop only
    /// emits one response per client request, but progress notifications
    /// AND server-initiated sampling requests fire from worker tasks
    /// <em>during</em> a tool call — they share the stdio stream with
    /// the eventual response. Without this gate, lines could interleave.
    /// </summary>
    private readonly System.Threading.SemaphoreSlim _outputLock = new(1, 1);

    /// <summary>
    /// Pending server-initiated requests (sampling/createMessage). Key
    /// is the id we sent; value resolves when the matching response
    /// shows up on the input stream. Without this map the dispatcher
    /// would treat the response as a malformed inbound request and
    /// echo back an error to the client.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<JsonNode>> _pendingOutboundRequests = new();
    private long _nextOutboundRequestId;

    /// <summary>
    /// True iff the client advertised <c>capabilities.sampling</c> in
    /// its <c>initialize</c> request. Set by HandleInitialize before
    /// any tool runs; checked by <see cref="McpSamplingClient.IsSupported"/>.
    /// </summary>
    private bool _clientSupportsSampling;

    private readonly TextWriter _logger;
    private readonly string _serverVersion;

    public McpServer(IEnumerable<IMcpTool> tools, TextReader input, TextWriter output, TextWriter? logger = null, string? serverVersion = null)
        : this(tools, Array.Empty<IMcpResource>(), input, output, logger, serverVersion)
    {
    }

    public McpServer(
        IEnumerable<IMcpTool> tools,
        IEnumerable<IMcpResource> resources,
        TextReader input,
        TextWriter output,
        TextWriter? logger = null,
        string? serverVersion = null)
    {
        _tools = new Dictionary<string, IMcpTool>(StringComparer.Ordinal);
        foreach (var tool in tools)
            _tools[tool.Name] = tool;
        _resources = new Dictionary<string, IMcpResource>(StringComparer.Ordinal);
        foreach (var resource in resources)
            _resources[resource.Uri] = resource;
        _input = input;
        _output = output;
        _logger = logger ?? TextWriter.Null;
        _serverVersion = serverVersion ?? GetCliVersion();
    }

    /// <summary>
    /// Convenience wrapper that pulls the same RevitClient the rest of the
    /// CLI uses and binds the three read-only tools.
    /// </summary>
    public static McpServer CreateDefault(RevitClient client, TextReader input, TextWriter output, TextWriter? logger = null)
        => CreateDefault(client, input, output, logger, allowWrites: false);

    /// <summary>
    /// Same as <see cref="CreateDefault(RevitClient, TextReader, TextWriter, TextWriter?)"/>
    /// but lets the caller opt into write tools (gated server-side by
    /// <see cref="SetTool"/>'s confirm flag — see PR #N for the safety model).
    /// </summary>
    public static McpServer CreateDefault(RevitClient client, TextReader input, TextWriter output, TextWriter? logger, bool allowWrites)
    {
        var tools = new List<IMcpTool>
        {
            new StatusTool(client),
            new QueryTool(client),
            new AuditTool(client),
            new SnapshotTool(client),
            new SetTool(client, allowWrites),
            new FixTool(client, allowWrites),
            new RollbackTool(client, allowWrites),
            new ImportTool(client, allowWrites),
            new PublishTool(client, allowWrites),
        };

        var resources = new List<IMcpResource>
        {
            new SnapshotLatestResource(client),
            HistoryListResource.ForCurrentDirectory(),
            new ProfileResource(),
            new CategoriesResource(client),
            new JournalRecentResource(),
        };

        return new McpServer(tools, resources, input, output, logger);
    }

    /// <summary>
    /// Run the loop until EOF on the input stream or cancellation.
    /// Always returns 0 — fatal errors should be logged, not crash the host
    /// (Claude Desktop will respawn us anyway).
    /// </summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await _input.ReadLineAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (line is null) break; // EOF
            if (string.IsNullOrWhiteSpace(line)) continue;

            await HandleLineAsync(line, cancellationToken).ConfigureAwait(false);
        }
        return 0;
    }

    /// <summary>
    /// Handle a single inbound message. Exposed for tests so they can drive
    /// the dispatcher without standing up a background loop.
    /// </summary>
    internal async Task HandleLineAsync(string line, CancellationToken cancellationToken)
    {
        JsonRpcRequest? request;
        JsonNode? rawId = null;
        JsonNode? rawObj = null;
        try
        {
            // Pull the id out first so even a partially-malformed request can
            // reply with the right id (per JSON-RPC §5.1).
            rawObj = JsonNode.Parse(line);
            if (rawObj is JsonObject obj && obj.TryGetPropertyValue("id", out var idNode))
                rawId = idNode?.DeepClone();

            // Bidirectional dispatch: if this line is a RESPONSE to a
            // server-initiated request (sampling/createMessage), it has
            // an `id` we sent and a `result`/`error` field but NO
            // `method`. Route it to the awaiting TCS rather than
            // treating it as a new client request.
            if (rawObj is JsonObject env
                && (env.ContainsKey("result") || env.ContainsKey("error"))
                && !env.ContainsKey("method"))
            {
                if (env["id"] is { } responseId
                    && responseId.GetValueKind() is JsonValueKind.String or JsonValueKind.Number
                    && TryCompletePending(responseId, env))
                {
                    return;
                }
                // A response with an id we don't know about — stale from
                // a cancelled or timed-out sampling request. Silently
                // log; do NOT echo back an error as if this were a
                // malformed inbound request (that would be a protocol
                // violation per JSON-RPC §6).
                _logger.WriteLine($"[mcp] dropped stale response (id={env["id"]?.ToJsonString() ?? "null"})");
                return;
            }

            request = JsonSerializer.Deserialize<JsonRpcRequest>(line);
        }
        catch (JsonException ex)
        {
            await WriteResponseAsync(JsonRpcResponse.Failure(null, McpProtocol.ErrorParseError, $"Parse error: {ex.Message}")).ConfigureAwait(false);
            return;
        }

        if (request is null || string.IsNullOrEmpty(request.Method))
        {
            await WriteResponseAsync(JsonRpcResponse.Failure(rawId, McpProtocol.ErrorInvalidRequest, "Invalid Request")).ConfigureAwait(false);
            return;
        }

        // Notifications (no id) get no response, by spec.
        if (request.IsNotification)
        {
            await HandleNotificationAsync(request).ConfigureAwait(false);
            return;
        }

        try
        {
            var response = await DispatchAsync(request, cancellationToken).ConfigureAwait(false);
            await WriteResponseAsync(response).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.WriteLine($"[mcp] internal error handling {request.Method}: {ex}");
            await WriteResponseAsync(JsonRpcResponse.Failure(request.Id, McpProtocol.ErrorInternalError, ex.Message)).ConfigureAwait(false);
        }
    }

    private Task HandleNotificationAsync(JsonRpcRequest request)
    {
        // We don't act on any notifications today. `notifications/initialized`
        // is the canonical one and a silent ack is correct per spec.
        _logger.WriteLine($"[mcp] notification: {request.Method}");
        return Task.CompletedTask;
    }

    private async Task<JsonRpcResponse> DispatchAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        switch (request.Method)
        {
            case "initialize":
                CaptureClientCapabilities(request.Params);
                return JsonRpcResponse.Success(request.Id, BuildInitializeResult());

            case "initialized": // some clients send as a request rather than notification
            case "ping":
                return JsonRpcResponse.Success(request.Id, new JsonObject());

            case "tools/list":
                return JsonRpcResponse.Success(request.Id, BuildToolsList());

            case "tools/call":
                return await HandleToolCallAsync(request, cancellationToken).ConfigureAwait(false);

            case "resources/list":
                return JsonRpcResponse.Success(request.Id, BuildResourcesList());

            case "resources/read":
                return await HandleResourceReadAsync(request, cancellationToken).ConfigureAwait(false);

            default:
                return JsonRpcResponse.Failure(request.Id, McpProtocol.ErrorMethodNotFound, $"Method not found: {request.Method}");
        }
    }

    private JsonNode BuildInitializeResult()
    {
        var capabilities = new McpServerCapabilities
        {
            Tools = new McpToolsCapability { ListChanged = false },
        };
        if (_resources.Count > 0)
        {
            capabilities.Resources = new McpResourcesCapability { Subscribe = false, ListChanged = false };
        }

        var payload = new McpInitializeResult
        {
            ProtocolVersion = McpProtocol.ProtocolVersion,
            Capabilities = capabilities,
            ServerInfo = new McpServerInfo { Name = "revitcli", Version = _serverVersion },
        };
        return JsonSerializer.SerializeToNode(payload, WriteOptions)!;
    }

    private JsonNode BuildToolsList()
    {
        var arr = new JsonArray();
        foreach (var tool in _tools.Values)
        {
            arr.Add(new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["inputSchema"] = tool.InputSchema.DeepClone(),
            });
        }
        return new JsonObject { ["tools"] = arr };
    }

    private async Task<JsonRpcResponse> HandleToolCallAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        if (request.Params is not JsonObject paramsObj)
            return JsonRpcResponse.Failure(request.Id, McpProtocol.ErrorInvalidParams, "tools/call: missing params object");

        var toolName = paramsObj["name"]?.GetValue<string>();
        if (string.IsNullOrEmpty(toolName))
            return JsonRpcResponse.Failure(request.Id, McpProtocol.ErrorInvalidParams, "tools/call: missing 'name'");

        if (!_tools.TryGetValue(toolName, out var tool))
            return JsonRpcResponse.Failure(request.Id, McpProtocol.ErrorInvalidParams, $"tools/call: unknown tool '{toolName}'");

        var arguments = paramsObj["arguments"];

        // Progress notifications (MCP spec 2024-11-05): the client opts in
        // by passing _meta.progressToken in the request. If both the client
        // requested it AND the tool implements IMcpProgressTool, route
        // through the progress-aware overload; otherwise the token is
        // silently ignored, which is spec-compliant.
        var progressToken = ExtractProgressToken(paramsObj);
        var progress = (progressToken is not null && tool is IMcpProgressTool)
            ? new McpProgressReporter(this, progressToken, cancellationToken)
            : (IMcpProgressReporter)NullMcpProgressReporter.Instance;

        // Sampling (MCP spec 2024-11-05, server -> client): tools that
        // opt in via IMcpSamplingTool always get a non-null client, but
        // its IsSupported flag reflects whether the connected client
        // actually advertised capabilities.sampling. The tool decides
        // how to degrade if not supported.
        var sampling = tool is IMcpSamplingTool
            ? new McpSamplingClient(this, _clientSupportsSampling)
            : (IMcpSamplingClient)NullMcpSamplingClient.Instance;

        string text;
        bool isError = false;
        try
        {
            text = tool switch
            {
                IMcpSamplingTool samplingTool =>
                    await samplingTool.ExecuteAsync(arguments, cancellationToken, sampling).ConfigureAwait(false),
                IMcpProgressTool progressTool when progressToken is not null =>
                    await progressTool.ExecuteAsync(arguments, cancellationToken, progress).ConfigureAwait(false),
                _ =>
                    await tool.ExecuteAsync(arguments, cancellationToken).ConfigureAwait(false),
            };
        }
        catch (Exception ex)
        {
            // Tool-level failure: surface to LLM as content with isError, not
            // as JSON-RPC error (per MCP spec — RPC errors mean protocol bugs).
            _logger.WriteLine($"[mcp] tool '{toolName}' threw: {ex}");
            text = $"Tool '{toolName}' failed: {ex.Message}";
            isError = true;
        }

        var result = new McpToolCallResult
        {
            Content = new List<McpContentBlock> { McpContentBlock.TextBlock(text) },
            IsError = isError,
        };
        return JsonRpcResponse.Success(request.Id, JsonSerializer.SerializeToNode(result, WriteOptions)!);
    }

    private JsonNode BuildResourcesList()
    {
        var arr = new JsonArray();
        foreach (var resource in _resources.Values)
        {
            arr.Add(new JsonObject
            {
                ["uri"] = resource.Uri,
                ["name"] = resource.Name,
                ["description"] = resource.Description,
                ["mimeType"] = resource.MimeType,
            });
        }
        return new JsonObject { ["resources"] = arr };
    }

    private async Task<JsonRpcResponse> HandleResourceReadAsync(JsonRpcRequest request, CancellationToken cancellationToken)
    {
        if (request.Params is not JsonObject paramsObj)
            return JsonRpcResponse.Failure(request.Id, McpProtocol.ErrorInvalidParams, "resources/read: missing params object");

        var uri = paramsObj["uri"]?.GetValue<string>();
        if (string.IsNullOrEmpty(uri))
            return JsonRpcResponse.Failure(request.Id, McpProtocol.ErrorInvalidParams, "resources/read: missing 'uri'");

        if (!_resources.TryGetValue(uri, out var resource))
            return JsonRpcResponse.Failure(request.Id, McpProtocol.ErrorInvalidParams, $"resources/read: unknown URI '{uri}'");

        string text;
        try
        {
            text = await resource.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Resource backends touch I/O (Revit HTTP, filesystem, profile loader).
            // Surface failures as JSON-RPC internal-error so the client can show
            // a real reason rather than an empty payload.
            _logger.WriteLine($"[mcp] resource '{uri}' threw: {ex}");
            return JsonRpcResponse.Failure(request.Id, McpProtocol.ErrorInternalError, ex.Message);
        }

        var result = new McpResourceReadResult
        {
            Contents = new List<McpResourceContents>
            {
                new()
                {
                    Uri = resource.Uri,
                    MimeType = resource.MimeType,
                    Text = text,
                },
            },
        };
        return JsonRpcResponse.Success(request.Id, JsonSerializer.SerializeToNode(result, WriteOptions)!);
    }

    private async Task WriteResponseAsync(JsonRpcResponse response)
    {
        var json = JsonSerializer.Serialize(response, WriteOptions);
        await _outputLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await _output.WriteLineAsync(json).ConfigureAwait(false);
            await _output.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            _outputLock.Release();
        }
    }

    /// <summary>
    /// Emit a JSON-RPC notification (no <c>id</c>, no response expected)
    /// while another request is still in flight. Used for
    /// <c>notifications/progress</c>; the lock keeps it from interleaving
    /// with the eventual response.
    /// </summary>
    internal async Task WriteNotificationAsync(string method, JsonNode @params, CancellationToken cancellationToken)
    {
        var obj = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = @params,
        };
        var json = obj.ToJsonString();
        await _outputLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _output.WriteLineAsync(json).ConfigureAwait(false);
            await _output.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            _outputLock.Release();
        }
    }

    private static string GetCliVersion()
    {
        var attr = typeof(McpServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        return attr?.InformationalVersion ?? "0.0.0";
    }

    /// <summary>
    /// Read the client's advertised capabilities from the <c>initialize</c>
    /// request and remember the bits we care about (currently just
    /// sampling). Per spec, missing keys are equivalent to "not advertised";
    /// we never throw on a malformed shape.
    /// </summary>
    private void CaptureClientCapabilities(JsonNode? @params)
    {
        // Reset on every initialize so a re-handshake from a different
        // client with different caps doesn't keep stale state.
        _clientSupportsSampling = false;
        if (@params is not JsonObject paramsObj) return;
        if (paramsObj["capabilities"] is not JsonObject caps) return;
        // The presence of any object under `sampling` (even empty) means
        // the client supports it. Per spec, an absent key means no.
        if (caps["sampling"] is JsonObject)
            _clientSupportsSampling = true;
    }

    /// <summary>
    /// Try to route an inbound message to a pending server-initiated
    /// request. Returns true if it matched (caller must skip normal
    /// dispatch), false if the id is unknown to us.
    /// </summary>
    private bool TryCompletePending(JsonNode responseId, JsonObject envelope)
    {
        var key = responseId.ToJsonString();
        if (!_pendingOutboundRequests.TryRemove(key, out var tcs))
            return false;
        try
        {
            tcs.TrySetResult(envelope.DeepClone()!);
        }
        catch (Exception ex)
        {
            tcs.TrySetException(ex);
        }
        return true;
    }

    /// <summary>
    /// Send a server-initiated request and await the matching response.
    /// Used by <see cref="McpSamplingClient.CreateMessageAsync"/>; could
    /// in principle back any future server-to-client method (the spec
    /// also defines roots, etc., though we don't expose those yet).
    /// </summary>
    internal async Task<JsonNode> SendRequestAsync(string method, JsonNode @params, CancellationToken cancellationToken)
    {
        // Distinct id namespace: prefix our ids with "srv-" so they
        // never collide with the client's. We use string ids; spec
        // allows string or int for the same field, but a string keeps
        // the namespacing trivially obvious in logs.
        var id = $"srv-{System.Threading.Interlocked.Increment(ref _nextOutboundRequestId)}";
        var tcs = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        var idNode = JsonValue.Create(id)!;
        _pendingOutboundRequests[idNode.ToJsonString()] = tcs;

        var envelope = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
            ["params"] = @params,
        };

        // Use the same locked write path as responses so we don't
        // interleave with notifications/responses already in flight.
        await _outputLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _output.WriteLineAsync(envelope.ToJsonString()).ConfigureAwait(false);
            await _output.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            _outputLock.Release();
        }

        // Honor cancellation: drop the pending entry if the caller bailed.
        using var reg = cancellationToken.Register(() =>
        {
            if (_pendingOutboundRequests.TryRemove(idNode.ToJsonString(), out var dropped))
                dropped.TrySetCanceled(cancellationToken);
        });
        return await tcs.Task.ConfigureAwait(false);
    }

    /// <summary>
    /// Pull <c>_meta.progressToken</c> out of the <c>tools/call</c>
    /// params per MCP spec 2024-11-05. The token may be a string or
    /// integer; we preserve it as a <see cref="JsonNode"/> so the same
    /// wire form roundtrips into the progress notifications.
    /// </summary>
    private static JsonNode? ExtractProgressToken(JsonObject paramsObj)
    {
        if (paramsObj["_meta"] is not JsonObject meta) return null;
        if (!meta.TryGetPropertyValue("progressToken", out var tok) || tok is null) return null;
        return tok.DeepClone();
    }

    /// <summary>
    /// Sampling client handed to <see cref="IMcpSamplingTool"/>
    /// implementations. Wraps <see cref="SendRequestAsync"/> with the
    /// spec-shape for <c>sampling/createMessage</c> and parses the
    /// response back into a typed <see cref="SamplingResult"/>.
    /// </summary>
    private sealed class McpSamplingClient : IMcpSamplingClient
    {
        private readonly McpServer _server;
        private readonly bool _supported;

        internal McpSamplingClient(McpServer server, bool supported)
        {
            _server = server;
            _supported = supported;
        }

        public bool IsSupported => _supported;

        public async Task<SamplingResult> CreateMessageAsync(
            IReadOnlyList<JsonObject> messages,
            SamplingOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            if (!_supported)
                throw new SamplingNotSupportedException(
                    "Connected client did not advertise capabilities.sampling. " +
                    "Check IsSupported before calling.");

            var msgArray = new JsonArray();
            foreach (var m in messages)
                msgArray.Add(m.DeepClone());

            var p = new JsonObject
            {
                ["messages"] = msgArray,
                ["maxTokens"] = options?.MaxTokens ?? 1024,
            };
            if (!string.IsNullOrEmpty(options?.SystemPrompt))
                p["systemPrompt"] = options!.SystemPrompt;
            if (!string.IsNullOrEmpty(options?.IncludeContext))
                p["includeContext"] = options!.IncludeContext;
            if (options?.Temperature is double t)
                p["temperature"] = t;
            if (options?.ModelPreferences is { } mp)
                p["modelPreferences"] = mp.DeepClone();

            var envelope = await _server.SendRequestAsync(
                "sampling/createMessage", p, cancellationToken).ConfigureAwait(false);

            if (envelope is not JsonObject env)
                throw new SamplingNotSupportedException("Client response was not a JSON object.");

            if (env["error"] is JsonObject error)
            {
                var msg = error["message"]?.GetValue<string>() ?? "(unspecified)";
                throw new SamplingNotSupportedException($"Client returned error: {msg}");
            }

            if (env["result"] is not JsonObject result)
                throw new SamplingNotSupportedException("Client response had no result.");

            return new SamplingResult
            {
                Role = result["role"]?.GetValue<string>() ?? "assistant",
                Model = result["model"]?.GetValue<string>(),
                StopReason = result["stopReason"]?.GetValue<string>(),
                Text = ExtractText(result["content"]),
                Raw = result.DeepClone(),
            };
        }

        /// <summary>
        /// Project the spec's <c>content</c> field down to a single
        /// string. The 2024-11-05 spec defines content as one object
        /// (<c>{type:"text", text:"..."}</c>) but later drafts allow an
        /// array of blocks. We handle both so a future spec bump
        /// doesn't break us; non-text blocks are skipped.
        /// </summary>
        private static string ExtractText(JsonNode? content)
        {
            if (content is null) return "";
            if (content is JsonObject single
                && single["type"]?.GetValue<string>() == "text")
            {
                return single["text"]?.GetValue<string>() ?? "";
            }
            if (content is JsonArray arr)
            {
                var sb = new System.Text.StringBuilder();
                foreach (var b in arr)
                {
                    if (b is JsonObject ob && ob["type"]?.GetValue<string>() == "text")
                        sb.Append(ob["text"]?.GetValue<string>() ?? "");
                }
                return sb.ToString();
            }
            return "";
        }
    }

    /// <summary>
    /// Reporter handed to <see cref="IMcpProgressTool"/> implementations.
    /// Each <see cref="ReportAsync"/> call writes a JSON-RPC notification
    /// to the same stdio stream as the eventual response, serialized via
    /// <see cref="WriteNotificationAsync"/>'s lock so notifications and
    /// the final response do not interleave.
    /// </summary>
    private sealed class McpProgressReporter : IMcpProgressReporter
    {
        private readonly McpServer _server;
        private readonly JsonNode _progressToken;
        private readonly CancellationToken _baseCt;

        internal McpProgressReporter(McpServer server, JsonNode progressToken, CancellationToken baseCt)
        {
            _server = server;
            _progressToken = progressToken;
            _baseCt = baseCt;
        }

        public async Task ReportAsync(double progress, double? total = null, string? message = null, CancellationToken cancellationToken = default)
        {
            // Honor BOTH the per-call CT (so a slow notification can be
            // cut short) and the dispatch-loop CT (so a server shutdown
            // doesn't get blocked on a notification).
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_baseCt, cancellationToken);

            var p = new JsonObject
            {
                ["progressToken"] = _progressToken.DeepClone(),
                ["progress"] = progress,
            };
            if (total.HasValue) p["total"] = total.Value;
            if (!string.IsNullOrEmpty(message)) p["message"] = message;

            await _server.WriteNotificationAsync("notifications/progress", p, linked.Token).ConfigureAwait(false);
        }
    }
}
