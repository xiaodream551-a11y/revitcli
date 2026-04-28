using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace RevitCli.Mcp.Tools;

/// <summary>
/// Lets a server-side tool ask the LLM client to perform an inference,
/// per the MCP <c>sampling/createMessage</c> spec (2024-11-05). This is
/// the <em>reverse</em> direction of the protocol: client → server is
/// the normal flow; sampling is server → client.
///
/// Trust model: the operator-side LLM client (Claude Desktop, Cursor,
/// etc.) decides whether to honor the request. Servers MUST be prepared
/// for the request to fail with <c>method-not-found</c> when the client
/// did not advertise <c>capabilities.sampling</c>; we surface that as
/// <see cref="SamplingNotSupportedException"/> so callers can degrade
/// gracefully (fall back to a non-sampling code path) instead of
/// returning a confusing protocol error to the original tool caller.
/// </summary>
internal interface IMcpSamplingClient
{
    /// <summary>
    /// True when the connected client advertised <c>capabilities.sampling</c>
    /// during the <c>initialize</c> handshake. Tools should check this
    /// before issuing a request — otherwise <see cref="CreateMessageAsync"/>
    /// will throw <see cref="SamplingNotSupportedException"/>.
    /// </summary>
    bool IsSupported { get; }

    /// <summary>
    /// Issue a <c>sampling/createMessage</c> request and await the
    /// client's response. The <paramref name="messages"/> array carries
    /// the conversation; each entry is a JSON object matching the spec
    /// shape (<c>role</c>, <c>content</c>). Optional knobs land in
    /// <paramref name="options"/>.
    /// </summary>
    Task<SamplingResult> CreateMessageAsync(
        IReadOnlyList<JsonObject> messages,
        SamplingOptions? options = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Marker on tools that can opt into using sampling. Mirrors the shape
/// of <see cref="IMcpProgressTool"/>: dispatcher routes through the
/// sampling-aware overload only when the tool implements this AND the
/// connected client advertised sampling capability. Tools that don't
/// implement this still go through plain <see cref="IMcpTool.ExecuteAsync"/>.
/// </summary>
internal interface IMcpSamplingTool : IMcpTool
{
    Task<string> ExecuteAsync(
        JsonNode? arguments,
        CancellationToken cancellationToken,
        IMcpSamplingClient sampling);
}

/// <summary>Optional knobs accepted by <see cref="IMcpSamplingClient.CreateMessageAsync"/>.</summary>
internal sealed class SamplingOptions
{
    /// <summary>Optional system prompt prepended client-side.</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>How much MCP-side context the client should include: <c>none</c>|<c>thisServer</c>|<c>allServers</c>.</summary>
    public string? IncludeContext { get; init; }

    /// <summary>Maximum tokens the response may consume. Spec requires this.</summary>
    public int MaxTokens { get; init; } = 1024;

    /// <summary>Optional temperature passthrough (0.0–1.0).</summary>
    public double? Temperature { get; init; }

    /// <summary>Optional model preference hints (cost / speed / intelligence priorities or a name list).</summary>
    public JsonObject? ModelPreferences { get; init; }
}

/// <summary>One sampling response.</summary>
internal sealed class SamplingResult
{
    public string Role { get; init; } = "assistant";
    public string? Model { get; init; }
    public string? StopReason { get; init; }
    /// <summary>The content text. Non-text content blocks are projected to a single concatenated string.</summary>
    public string Text { get; init; } = "";
    /// <summary>Original response for callers that need the full envelope.</summary>
    public JsonNode? Raw { get; init; }
}

/// <summary>
/// Sink used when the connected client did not advertise sampling. Always
/// reports <see cref="IsSupported"/> = false; <see cref="CreateMessageAsync"/>
/// throws <see cref="SamplingNotSupportedException"/>.
/// </summary>
internal sealed class NullMcpSamplingClient : IMcpSamplingClient
{
    public static readonly NullMcpSamplingClient Instance = new();
    public bool IsSupported => false;
    public Task<SamplingResult> CreateMessageAsync(
        IReadOnlyList<JsonObject> messages, SamplingOptions? options = null, CancellationToken cancellationToken = default)
        => throw new SamplingNotSupportedException(
            "The connected MCP client did not advertise `capabilities.sampling`. " +
            "Re-check IsSupported before calling, or fall back to a non-sampling code path.");
}

/// <summary>
/// Thrown by <see cref="IMcpSamplingClient.CreateMessageAsync"/> when
/// the client did not advertise sampling capability or refused the
/// request. Tools should catch this and degrade rather than bubbling.
/// </summary>
internal sealed class SamplingNotSupportedException : Exception
{
    public SamplingNotSupportedException(string message) : base(message) { }
}
