using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using RevitCli.Client;
using RevitCli.Commands;
using RevitCli.Output;

namespace RevitCli.Mcp.Tools;

/// <summary>
/// MCP wrapper around <see cref="FixCommand.ExecuteAsync"/>. Mutating —
/// gated behind the same two layers as <see cref="SetTool"/>:
///
/// <list type="number">
///   <item>The CLI must have been started with <c>mcp serve --allow-writes</c>.
///         Without that flag the tool returns a refusal text — even for
///         <c>dryRun</c> previews. Failing closed is the safer default for an
///         LLM-driven caller: the tool name itself is "fix", which signals
///         intent to mutate.</item>
///   <item>Each invocation must include <c>confirm: true</c> in its arguments.</item>
/// </list>
///
/// The orchestration (CheckRunner -> FixPlanner -> baseline snapshot -> loop
/// SetParameter) is delegated to the existing CLI <see cref="FixCommand"/>
/// implementation; this tool only maps MCP args, captures the text output,
/// and emits an audit entry.
/// </summary>
internal sealed class FixTool : IMcpTool
{
    /// <summary>Refusal text when the server is in read-only mode.</summary>
    public const string DisabledMessage =
        "Write tools are disabled. Restart `mcp serve` with --allow-writes.";

    /// <summary>Refusal text when the caller forgot to pass <c>confirm: true</c>.</summary>
    public const string ConfirmMessage =
        "This is a write operation. Re-issue with confirm: true to proceed.";

    private readonly RevitClient _client;
    private readonly bool _allowWrites;

    public FixTool(RevitClient client, bool allowWrites)
    {
        _client = client;
        _allowWrites = allowWrites;
    }

    public string Name => "fix";

    public string Description =>
        "Plan or apply profile-driven autofixes for issues surfaced by `audit`. " +
        "Requires the server to have been started with --allow-writes AND the " +
        "request to include `confirm: true`. Set `dryRun: true` to preview the " +
        "plan without modifying the model. Captures a baseline snapshot before " +
        "applying so the change can be undone via the `rollback` tool.";

    public JsonNode InputSchema => new JsonObject
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["checkName"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Check set name from the profile (default: \"default\").",
            },
            ["rules"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "string" },
                ["description"] = "Filter to specific rule names; empty/omitted = all rules.",
            },
            ["severity"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Filter by severity: error|warning|info.",
            },
            ["dryRun"] = new JsonObject
            {
                ["type"] = "boolean",
                ["description"] = "Preview the plan without writing to the model.",
                ["default"] = false,
            },
            ["allowInferred"] = new JsonObject
            {
                ["type"] = "boolean",
                ["description"] = "Allow lower-confidence inferred fixes.",
                ["default"] = false,
            },
            ["maxChanges"] = new JsonObject
            {
                ["type"] = "integer",
                ["description"] = "Cap the number of fixes applied (default 50).",
                ["default"] = 50,
            },
            ["confirm"] = new JsonObject
            {
                ["type"] = "boolean",
                ["description"] = "Required safety gate — must be true to execute.",
                ["default"] = false,
            },
        },
        ["required"] = new JsonArray("confirm"),
        ["additionalProperties"] = false,
    };

    public async Task<string> ExecuteAsync(JsonNode? arguments, CancellationToken cancellationToken)
    {
        var args = arguments as JsonObject ?? new JsonObject();
        var checkName = TryGetString(args, "checkName");
        var rules = TryGetStringArray(args, "rules");
        var severity = TryGetString(args, "severity");
        var dryRun = TryGetBool(args, "dryRun") ?? false;
        var allowInferred = TryGetBool(args, "allowInferred") ?? false;
        var maxChanges = TryGetInt(args, "maxChanges") ?? 50;

        if (!_allowWrites)
        {
            LogAudit("refused-writes-disabled", checkName, rules?.Count ?? 0, severity, dryRun, allowInferred, maxChanges, exitCode: null);
            return DisabledMessage;
        }

        if (TryGetBool(args, "confirm") != true)
        {
            LogAudit("refused-no-confirm", checkName, rules?.Count ?? 0, severity, dryRun, allowInferred, maxChanges, exitCode: null);
            return ConfirmMessage;
        }

        // Map MCP intent -> CLI flag combo. We never pass dryRun=true together
        // with apply=true (CLI rejects that); instead, dryRun=true forces
        // apply=false (preview the plan), and dryRun=false forces apply=true.
        var output = new StringWriter();
        var exitCode = await FixCommand.ExecuteAsync(
            _client,
            checkName,
            profilePath: null,
            rules,
            severity,
            dryRun: dryRun,
            apply: !dryRun,
            yes: true,
            allowInferred: allowInferred,
            maxChanges: maxChanges,
            baselineOutput: null,
            noSnapshot: false,
            output);

        LogAudit(exitCode == 0 ? "ok" : "error", checkName, rules?.Count ?? 0, severity, dryRun, allowInferred, maxChanges, exitCode);
        return output.ToString().TrimEnd();
    }

    /// <summary>
    /// Append an audit entry to <c>.revitcli/journal.jsonl</c>. We log just
    /// enough for attribution — never the rule names themselves (they are
    /// profile config, not user data, but logging counts is sufficient and
    /// keeps the entry compact).
    /// </summary>
    private static void LogAudit(string outcome, string? checkName, int ruleCount,
        string? severity, bool dryRun, bool allowInferred, int maxChanges, int? exitCode)
    {
        var profileDir = Profile.ProfileLoader.Discover() is { } p
            ? Path.GetDirectoryName(Path.GetFullPath(p))
            : null;

        JournalLogger.Log(profileDir, new
        {
            action = "fix",
            transport = "mcp",
            outcome,
            checkName,
            ruleCount,
            severity,
            dryRun,
            allowInferred,
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

    private static List<string>? TryGetStringArray(JsonObject args, string key)
    {
        if (!args.TryGetPropertyValue(key, out var node) || node is null) return null;
        if (node is not JsonArray arr) return null;
        var list = new List<string>();
        foreach (var entry in arr)
        {
            if (entry is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrEmpty(s))
                list.Add(s);
        }
        return list.Count == 0 ? null : list;
    }
}
