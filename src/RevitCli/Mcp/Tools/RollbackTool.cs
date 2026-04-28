using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using RevitCli.Client;
using RevitCli.Commands;
using RevitCli.Output;

namespace RevitCli.Mcp.Tools;

/// <summary>
/// MCP wrapper around <see cref="RollbackCommand.ExecuteAsync"/>. Restores
/// parameters from a baseline snapshot written by a previous <c>fix --apply</c>
/// (or the <see cref="FixTool"/> equivalent). Mutating — same two-layer gate
/// as <see cref="SetTool"/>:
///
/// <list type="number">
///   <item><c>--allow-writes</c> required server-side.</item>
///   <item><c>confirm: true</c> required per call.</item>
/// </list>
///
/// The caller-supplied <c>baseline</c> path is bound to <c>.revitcli/</c>
/// via <see cref="McpPathGuard.ResolveUnderRevitCli"/>. An LLM that passes
/// <c>/etc/passwd</c>, a relative <c>..</c>-traversal, or a path on a
/// different drive is refused before the CLI ever sees the value, with an
/// audit entry tagged <c>refused-path-out-of-bounds</c>. Operators who
/// need to roll back from a baseline outside <c>.revitcli/</c> should use
/// the CLI directly; the MCP transport intentionally trades flexibility
/// for a tighter trust boundary.
/// </summary>
internal sealed class RollbackTool : IMcpTool
{
    public const string DisabledMessage =
        "Write tools are disabled. Restart `mcp serve` with --allow-writes.";

    public const string ConfirmMessage =
        "This is a write operation. Re-issue with confirm: true to proceed.";

    private readonly RevitClient _client;
    private readonly bool _allowWrites;

    public RollbackTool(RevitClient client, bool allowWrites)
    {
        _client = client;
        _allowWrites = allowWrites;
    }

    public string Name => "rollback";

    public string Description =>
        "Restore parameters from a baseline snapshot written by a previous " +
        "`fix` apply. Requires --allow-writes AND confirm: true. Set " +
        "`dryRun: true` to preview which elements would be restored.";

    public JsonNode InputSchema => new JsonObject
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["baseline"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Path to a baseline snapshot file written by `fix --apply` " +
                                  "(typically under .revitcli/fix-baseline-<timestamp>.json).",
            },
            ["dryRun"] = new JsonObject
            {
                ["type"] = "boolean",
                ["description"] = "Preview which elements would be restored without writing.",
                ["default"] = false,
            },
            ["maxChanges"] = new JsonObject
            {
                ["type"] = "integer",
                ["description"] = "Cap the number of restore writes (default 50).",
                ["default"] = 50,
            },
            ["confirm"] = new JsonObject
            {
                ["type"] = "boolean",
                ["description"] = "Required safety gate — must be true to execute.",
                ["default"] = false,
            },
        },
        ["required"] = new JsonArray("baseline", "confirm"),
        ["additionalProperties"] = false,
    };

    public async Task<string> ExecuteAsync(JsonNode? arguments, CancellationToken cancellationToken)
    {
        var args = arguments as JsonObject ?? new JsonObject();
        var baseline = TryGetString(args, "baseline");
        var dryRun = TryGetBool(args, "dryRun") ?? false;
        var maxChanges = TryGetInt(args, "maxChanges") ?? 50;

        if (!_allowWrites)
        {
            LogAudit("refused-writes-disabled", baseline, dryRun, maxChanges, exitCode: null);
            return DisabledMessage;
        }

        if (TryGetBool(args, "confirm") != true)
        {
            LogAudit("refused-no-confirm", baseline, dryRun, maxChanges, exitCode: null);
            return ConfirmMessage;
        }

        if (string.IsNullOrWhiteSpace(baseline))
        {
            // The MCP dispatcher does NOT enforce JSON Schema, so we
            // replicate the required-arg check here. Logging the refusal
            // keeps the journal's "every outcome is audited" invariant
            // intact — without this, a caller that forgets the field
            // produces a silent gap in journal.jsonl.
            LogAudit("refused-missing-baseline", baseline, dryRun, maxChanges, exitCode: null);
            return "Error: 'baseline' is required.";
        }

        // The baseline path comes from an LLM-driven caller. Bind it to
        // .revitcli/ so the tool cannot be coaxed into resolving paths
        // anywhere on the operator's filesystem (e.g. probing for the
        // existence of /etc/* via the "not found" / parse-error messages).
        var boundedBaseline = McpPathGuard.ResolveUnderRevitCli(baseline);
        if (boundedBaseline is null)
        {
            LogAudit("refused-path-out-of-bounds", baseline, dryRun, maxChanges, exitCode: null);
            return "Error: 'baseline' must be a path under .revitcli/.";
        }

        var output = new StringWriter();
        var exitCode = await RollbackCommand.ExecuteAsync(
            _client,
            boundedBaseline,
            dryRun: dryRun,
            yes: true,
            maxChanges: maxChanges,
            output);

        LogAudit(exitCode == 0 ? "ok" : "error", baseline, dryRun, maxChanges, exitCode);
        return output.ToString().TrimEnd();
    }

    /// <summary>
    /// Append an audit entry to <c>.revitcli/journal.jsonl</c>. The baseline
    /// path IS recorded — it is a server-side filesystem path supplied by the
    /// caller and is essential for forensics ("which baseline was rolled
    /// back?"). It is not a user-data field, so leakage concerns do not apply.
    /// </summary>
    private static void LogAudit(string outcome, string? baseline, bool dryRun, int maxChanges, int? exitCode)
    {
        var profileDir = Profile.ProfileLoader.Discover() is { } p
            ? Path.GetDirectoryName(Path.GetFullPath(p))
            : null;

        JournalLogger.Log(profileDir, new
        {
            action = "rollback",
            transport = "mcp",
            outcome,
            baseline,
            dryRun,
            maxChanges,
            exitCode,
            timestamp = DateTime.UtcNow.ToString("o"),
            user = Environment.UserName,
        });
    }

    private static string? TryGetString(JsonObject args, string key)
    {
        if (!args.TryGetPropertyValue(key, out var node) || node is null) return null;
        if (node is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrEmpty(s))
            return s;
        return null;
    }

    private static bool? TryGetBool(JsonObject args, string key)
    {
        if (!args.TryGetPropertyValue(key, out var node) || node is null) return null;
        if (node is JsonValue v && v.TryGetValue<bool>(out var b)) return b;
        return null;
    }

    private static int? TryGetInt(JsonObject args, string key)
    {
        if (!args.TryGetPropertyValue(key, out var node) || node is null) return null;
        if (node is JsonValue v)
        {
            if (v.TryGetValue<int>(out var i)) return i;
            if (v.TryGetValue<long>(out var l)) return (int)l;
            if (v.TryGetValue<string>(out var s) && int.TryParse(s, out var parsed)) return parsed;
        }
        return null;
    }
}
