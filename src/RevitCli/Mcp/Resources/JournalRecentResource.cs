using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using RevitCli.Commands;
using RevitCli.Journal;

namespace RevitCli.Mcp.Resources;

/// <summary>
/// <c>revitcli://journal/recent</c> — exposes the last
/// <see cref="DefaultLimit"/> audit entries to LLM clients so an
/// agent can review its own recent activity. Closes the
/// "agent has no memory of what it just did" gap: every MCP write
/// already lands in <c>.revitcli/journal.jsonl</c>; this resource
/// lets the same agent read its own trail back.
///
/// Bounded by design — we cap the slice rather than expose query
/// params on the URI. An LLM that wants more granular slicing should
/// call <c>journal tail</c> via the CLI in a future iteration; for
/// now, last-50 is enough context for "did the last fix succeed?
/// what did I attempt in the last hour?" decisions.
/// </summary>
internal sealed class JournalRecentResource : IMcpResource
{
    /// <summary>
    /// Maximum number of entries returned per read. Picked by feel:
    /// big enough to cover a normal session of write activity, small
    /// enough to fit an LLM's single-resource context budget.
    /// </summary>
    public const int DefaultLimit = 50;

    private static readonly JsonSerializerOptions PrettyJson = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Uri => "revitcli://journal/recent";

    public string Name => "Recent audit journal entries";

    public string Description =>
        $"Last {DefaultLimit} entries from .revitcli/journal.jsonl, " +
        "newest first. Use this to review what the agent (or operator) " +
        "has done — every set/fix/rollback/import/publish call lands " +
        "here, including refusals and errors.";

    public string MimeType => "application/json";

    public Task<string> ReadAsync(CancellationToken cancellationToken)
    {
        var path = JournalCommand.ResolveJournalPath(null);
        var entries = JournalReader.Read(path, limit: DefaultLimit);

        // Project to a JsonNode array preserving each entry's full
        // shape via Raw — tool-specific fields (param/category/baseline)
        // pass through unchanged so an agent reading this resource can
        // correlate by id, path, etc.
        var arr = new JsonArray();
        foreach (var entry in entries)
            arr.Add(JsonNode.Parse(entry.Raw.GetRawText()));

        var payload = new JsonObject
        {
            ["journalPath"] = path,
            ["count"] = entries.Count,
            ["limit"] = DefaultLimit,
            ["entries"] = arr,
        };
        return Task.FromResult(payload.ToJsonString(PrettyJson));
    }
}
