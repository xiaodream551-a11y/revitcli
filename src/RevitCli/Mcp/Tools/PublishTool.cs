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
/// MCP wrapper around <see cref="PublishCommand.ExecuteAsync"/>. Runs the
/// profile-defined publish pipeline (precheck -> exports -> receipt -> webhook).
/// Mutating — same two-layer gate as the rest of the write-tool family:
///
/// <list type="number">
///   <item><c>--allow-writes</c> required server-side.</item>
///   <item><c>confirm: true</c> required per call.</item>
/// </list>
///
/// Path-safety surface: the publish pipeline writes exports to a directory
/// declared in the profile (<c>preset.outputDir</c>) — that path is operator-
/// controlled, not LLM-controlled, so it is trusted. The only LLM-controlled
/// filesystem path on this tool is <c>since</c> (the optional incremental
/// baseline), which is bound to <c>.revitcli/</c> via
/// <see cref="McpPathGuard.ResolveUnderRevitCli"/> before being passed
/// through.
///
/// Long-running: a publish call can take minutes (sheet exports, webhook
/// I/O). MCP has no progress notification on this tool — the LLM client
/// blocks until the call returns. If the client times out, the publish
/// continues server-side; that's acceptable since the receipt + journal
/// land regardless.
/// </summary>
internal sealed class PublishTool : IMcpProgressTool
{
    public const string DisabledMessage =
        "Write tools are disabled. Restart `mcp serve` with --allow-writes.";

    public const string ConfirmMessage =
        "This is a write operation. Re-issue with confirm: true to proceed.";

    private readonly RevitClient _client;
    private readonly bool _allowWrites;

    public PublishTool(RevitClient client, bool allowWrites)
    {
        _client = client;
        _allowWrites = allowWrites;
    }

    public string Name => "publish";

    public string Description =>
        "Run a publish pipeline declared in .revitcli.yml: precheck (audit) " +
        "-> export presets (DWG/PDF/schedule) -> receipt -> optional webhook. " +
        "Requires the server to have been started with --allow-writes AND the " +
        "request to include `confirm: true`. Set `dryRun: true` to preview " +
        "what would be exported without writing files. Output paths come from " +
        "the profile, not the caller.";

    public JsonNode InputSchema => new JsonObject
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["pipeline"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Publish pipeline name from the profile (default: \"default\").",
            },
            ["dryRun"] = new JsonObject
            {
                ["type"] = "boolean",
                ["description"] = "Preview the plan without writing exports or firing the webhook.",
                ["default"] = false,
            },
            ["since"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Optional baseline snapshot path for incremental publish. " +
                                  "Must be under .revitcli/. Only sheets that changed since " +
                                  "this baseline are re-exported.",
            },
            ["sinceMode"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Diff mode for `since`: content|meta. Default: content " +
                                  "(or whatever the pipeline declares).",
            },
            ["updateBaseline"] = new JsonObject
            {
                ["type"] = "boolean",
                ["description"] = "After a successful publish, write the current snapshot back " +
                                  "to the `since` path so the next call diffs against it.",
                ["default"] = false,
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

    public Task<string> ExecuteAsync(JsonNode? arguments, CancellationToken cancellationToken)
        => ExecuteAsync(arguments, cancellationToken, NullMcpProgressReporter.Instance);

    public async Task<string> ExecuteAsync(JsonNode? arguments, CancellationToken cancellationToken, IMcpProgressReporter progress)
    {
        var args = arguments as JsonObject ?? new JsonObject();
        var pipeline = JsonArgs.TryGetString(args, "pipeline");
        var dryRun = JsonArgs.TryGetBool(args, "dryRun") ?? false;
        var since = JsonArgs.TryGetString(args, "since");
        var sinceMode = JsonArgs.TryGetString(args, "sinceMode");
        var updateBaseline = JsonArgs.TryGetBool(args, "updateBaseline") ?? false;

        if (!_allowWrites)
        {
            LogAudit("refused-writes-disabled", pipeline, dryRun, since, sinceMode, updateBaseline, exitCode: null);
            return DisabledMessage;
        }

        if (JsonArgs.TryGetBool(args, "confirm") != true)
        {
            LogAudit("refused-no-confirm", pipeline, dryRun, since, sinceMode, updateBaseline, exitCode: null);
            return ConfirmMessage;
        }

        // The `since` arg is optional. When supplied, bind it to .revitcli/
        // so an LLM can't point publish at an arbitrary baseline file (and
        // — via --update-baseline — write back to one).
        string? boundedSince = null;
        if (!string.IsNullOrWhiteSpace(since))
        {
            boundedSince = McpPathGuard.ResolveUnderRevitCli(since);
            if (boundedSince is null)
            {
                LogAudit("refused-path-out-of-bounds", pipeline, dryRun, since, sinceMode, updateBaseline, exitCode: null);
                return "Error: 'since' must be a path under .revitcli/.";
            }
        }

        // From here on: real work begins. Emit a "starting" notification so
        // a client subscribed to progress knows the call has cleared the
        // gates and is now hitting the export pipeline. We don't know the
        // total step count up front (depends on profile pipeline shape), so
        // total stays null and progress just monotonically advances.
        await progress.ReportAsync(
            progress: 0,
            total: null,
            message: $"publish: starting pipeline '{pipeline ?? "default"}'" + (dryRun ? " (dry-run)" : ""),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var output = new StringWriter();
        var exitCode = await PublishCommand.ExecuteAsync(
            _client,
            name: pipeline,
            profilePath: null,
            dryRun: dryRun,
            since: boundedSince,
            sinceMode: sinceMode,
            updateBaseline: updateBaseline,
            output);

        await progress.ReportAsync(
            progress: 1,
            total: 1,
            message: $"publish: complete (exit={exitCode})",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        LogAudit(exitCode == 0 ? "ok" : "error", pipeline, dryRun, since, sinceMode, updateBaseline, exitCode);
        return output.ToString().TrimEnd();
    }

    /// <summary>
    /// Append an audit entry to <c>.revitcli/journal.jsonl</c>. The
    /// pipeline name and the (original) `since` value are recorded for
    /// forensics — neither is user-data, both are profile/path identifiers.
    /// </summary>
    private static void LogAudit(string outcome, string? pipeline, bool dryRun, string? since,
        string? sinceMode, bool updateBaseline, int? exitCode)
    {
        var profileDir = Profile.ProfileLoader.Discover() is { } p
            ? Path.GetDirectoryName(Path.GetFullPath(p))
            : null;

        JournalLogger.Log(profileDir, new
        {
            action = "publish",
            transport = "mcp",
            outcome,
            pipeline,
            dryRun,
            since,
            sinceMode,
            updateBaseline,
            exitCode,
            timestamp = DateTime.UtcNow.ToString("o"),
            user = Environment.UserName,
        });
    }

}
