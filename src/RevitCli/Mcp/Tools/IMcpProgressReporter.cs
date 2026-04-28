using System.Threading;
using System.Threading.Tasks;

namespace RevitCli.Mcp.Tools;

/// <summary>
/// Allows a long-running MCP tool to emit progress events that the
/// client can render mid-call. Per MCP spec 2024-11-05, progress is
/// opt-in per request: the client supplies a <c>_meta.progressToken</c>
/// in <c>tools/call</c>, and the server writes
/// <c>notifications/progress</c> messages on the same stdio stream
/// while the response is still pending.
///
/// A no-op reporter is provided for callers that don't (or can't) emit
/// real progress — see <see cref="NullMcpProgressReporter"/>.
/// </summary>
internal interface IMcpProgressReporter
{
    /// <summary>
    /// Emit one progress event. <paramref name="progress"/> is a
    /// monotonically increasing value (any unit — bytes, items, %).
    /// <paramref name="total"/> is optional; supply it when the size of
    /// the work is known up front. <paramref name="message"/> is a
    /// short human-readable status line (e.g. "exporting preset 2/4").
    /// </summary>
    Task ReportAsync(double progress, double? total = null, string? message = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// Sink for callers that didn't ask for progress (no
/// <c>_meta.progressToken</c> in the request). Tools always have a
/// non-null reporter to call, regardless of whether the client subscribed.
/// </summary>
internal sealed class NullMcpProgressReporter : IMcpProgressReporter
{
    public static readonly NullMcpProgressReporter Instance = new();
    public Task ReportAsync(double progress, double? total = null, string? message = null, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}

/// <summary>
/// Marker for tools that can emit progress. Tools that don't implement
/// this still go through the regular <see cref="IMcpTool.ExecuteAsync"/>
/// path even when the request includes a progress token — the token is
/// silently ignored, matching the spec's "client may always request
/// progress; server may always omit it".
/// </summary>
internal interface IMcpProgressTool : IMcpTool
{
    /// <summary>
    /// Same contract as <see cref="IMcpTool.ExecuteAsync"/>, but with a
    /// reporter the implementation can call to push progress events to
    /// the client. Reporter is non-null even when the client did not
    /// subscribe (in which case it's the null sink).
    /// </summary>
    Task<string> ExecuteAsync(System.Text.Json.Nodes.JsonNode? arguments, CancellationToken cancellationToken, IMcpProgressReporter progress);
}
