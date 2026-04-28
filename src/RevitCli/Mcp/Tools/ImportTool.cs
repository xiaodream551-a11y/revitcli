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
/// MCP wrapper around <see cref="ImportCommand.ExecuteAsync"/>. Bulk-writes
/// element parameters from a CSV file. Mutating — same two-layer gate as
/// <see cref="SetTool"/>, <see cref="FixTool"/>, and <see cref="RollbackTool"/>:
///
/// <list type="number">
///   <item><c>--allow-writes</c> required server-side.</item>
///   <item><c>confirm: true</c> required per call.</item>
/// </list>
///
/// The <c>file</c> argument is the only filesystem path the LLM controls.
/// It is bound to <c>.revitcli/</c> via <see cref="McpPathGuard.ResolveUnderRevitCli"/>:
/// operators stage import CSVs there, and the tool refuses anything outside.
/// Closes the import side of the agent loop alongside <see cref="FixTool"/>
/// (rule-driven autofix) and <see cref="RollbackTool"/> (recovery).
/// </summary>
internal sealed class ImportTool : IMcpTool
{
    public const string DisabledMessage =
        "Write tools are disabled. Restart `mcp serve` with --allow-writes.";

    public const string ConfirmMessage =
        "This is a write operation. Re-issue with confirm: true to proceed.";

    private readonly RevitClient _client;
    private readonly bool _allowWrites;

    public ImportTool(RevitClient client, bool allowWrites)
    {
        _client = client;
        _allowWrites = allowWrites;
    }

    public string Name => "import";

    public string Description =>
        "Batch-write Revit element parameters from a CSV file. Requires the " +
        "server to have been started with --allow-writes AND the request to " +
        "include `confirm: true`. The CSV path must lie under .revitcli/ " +
        "(stage imports there). Set `dryRun: true` to preview the plan " +
        "without writing.";

    public JsonNode InputSchema => new JsonObject
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["file"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Path to the CSV file, relative to or under .revitcli/.",
            },
            ["category"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Revit category (e.g. \"walls\", \"doors\").",
            },
            ["matchBy"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Parameter name linking each CSV row to a Revit element (e.g. \"Mark\").",
            },
            ["map"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Optional explicit column->param mapping (e.g. \"col:Param,col2:Param2\"). Default: every non-matchBy column maps to a same-named parameter.",
            },
            ["dryRun"] = new JsonObject
            {
                ["type"] = "boolean",
                ["description"] = "Preview the plan without writing.",
                ["default"] = false,
            },
            ["onMissing"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Behavior when a CSV row has no Revit match: error|warn|skip.",
                ["default"] = "warn",
            },
            ["onDuplicate"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Behavior when a row matches multiple Revit elements: error|first|all.",
                ["default"] = "error",
            },
            ["encoding"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "CSV encoding: utf-8|gbk|auto.",
                ["default"] = "auto",
            },
            ["batchSize"] = new JsonObject
            {
                ["type"] = "integer",
                ["description"] = "Max element IDs per /api/elements/set call (1..1000).",
                ["default"] = 100,
            },
            ["confirm"] = new JsonObject
            {
                ["type"] = "boolean",
                ["description"] = "Required safety gate — must be true to execute.",
                ["default"] = false,
            },
        },
        ["required"] = new JsonArray("file", "category", "matchBy", "confirm"),
        ["additionalProperties"] = false,
    };

    public async Task<string> ExecuteAsync(JsonNode? arguments, CancellationToken cancellationToken)
    {
        var args = arguments as JsonObject ?? new JsonObject();
        var file = TryGetString(args, "file");
        var category = TryGetString(args, "category");
        var matchBy = TryGetString(args, "matchBy");
        var dryRun = TryGetBool(args, "dryRun") ?? false;
        var onMissing = TryGetString(args, "onMissing") ?? "warn";
        var onDuplicate = TryGetString(args, "onDuplicate") ?? "error";
        var encoding = TryGetString(args, "encoding") ?? "auto";
        var batchSize = TryGetInt(args, "batchSize") ?? 100;
        var rawMap = TryGetString(args, "map");

        if (!_allowWrites)
        {
            LogAudit("refused-writes-disabled", file, category, matchBy, dryRun, batchSize, exitCode: null);
            return DisabledMessage;
        }

        if (TryGetBool(args, "confirm") != true)
        {
            LogAudit("refused-no-confirm", file, category, matchBy, dryRun, batchSize, exitCode: null);
            return ConfirmMessage;
        }

        // Required-arg checks. The MCP dispatcher does not enforce JSON
        // Schema, so the schema's `required` list is advisory; we replicate
        // the checks here and audit-log each refusal separately.
        if (string.IsNullOrWhiteSpace(file))
        {
            LogAudit("refused-missing-file", file, category, matchBy, dryRun, batchSize, exitCode: null);
            return "Error: 'file' is required.";
        }

        if (string.IsNullOrWhiteSpace(category))
        {
            LogAudit("refused-missing-category", file, category, matchBy, dryRun, batchSize, exitCode: null);
            return "Error: 'category' is required.";
        }

        if (string.IsNullOrWhiteSpace(matchBy))
        {
            LogAudit("refused-missing-matchBy", file, category, matchBy, dryRun, batchSize, exitCode: null);
            return "Error: 'matchBy' is required.";
        }

        // Path-bound: the LLM may only point at files staged under
        // .revitcli/. This prevents the tool from being coaxed into reading
        // arbitrary files (the parser surfaces useful error text from any
        // file it touches, which is enough to leak file existence).
        var boundedFile = McpPathGuard.ResolveUnderRevitCli(file);
        if (boundedFile is null)
        {
            LogAudit("refused-path-out-of-bounds", file, category, matchBy, dryRun, batchSize, exitCode: null);
            return "Error: 'file' must be a path under .revitcli/.";
        }

        var output = new StringWriter();
        var exitCode = await ImportCommand.ExecuteAsync(
            _client,
            file: boundedFile,
            category: category!,
            matchBy: matchBy!,
            rawMap: rawMap,
            dryRun: dryRun,
            onMissing: onMissing,
            onDuplicate: onDuplicate,
            encodingHint: encoding,
            batchSize: batchSize,
            output);

        LogAudit(exitCode == 0 ? "ok" : "error", file, category, matchBy, dryRun, batchSize, exitCode);
        return output.ToString().TrimEnd();
    }

    /// <summary>
    /// Append an audit entry to <c>.revitcli/journal.jsonl</c>. We log
    /// non-sensitive identifiers (file path, category, match-by parameter
    /// name) but NOT the CSV contents, the column mapping body, or any
    /// per-row data — that channel is intended only for attribution and
    /// gate-state forensics.
    /// </summary>
    private static void LogAudit(string outcome, string? file, string? category,
        string? matchBy, bool dryRun, int batchSize, int? exitCode)
    {
        var profileDir = Profile.ProfileLoader.Discover() is { } p
            ? Path.GetDirectoryName(Path.GetFullPath(p))
            : null;

        JournalLogger.Log(profileDir, new
        {
            action = "import",
            transport = "mcp",
            outcome,
            file,
            category,
            matchBy,
            dryRun,
            batchSize,
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
