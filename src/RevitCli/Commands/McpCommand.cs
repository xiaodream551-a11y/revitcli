using System;
using System.CommandLine;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using RevitCli.Client;
using RevitCli.Mcp;

namespace RevitCli.Commands;

/// <summary>
/// `revitcli mcp` — Model Context Protocol adapter. Subcommands:
///
/// <list type="bullet">
///   <item><c>serve</c>     — run the stdio MCP server (production path
///                            used by Claude Desktop / Cursor / etc.).</item>
///   <item><c>list</c>      — list registered tools + resources without
///                            standing up an MCP client.</item>
///   <item><c>test</c>      — synthesize a <c>tools/call</c> request
///                            from CLI args and print the response.
///                            Smoke-test the addin endpoint, reproduce
///                            what an LLM would do, and inspect refusal
///                            text without booting a chat client.</item>
///   <item><c>resource</c>  — read an MCP resource (<c>resources/read</c>)
///                            by URI. Same convenience as <c>test</c>
///                            for the resource side.</item>
/// </list>
///
/// All four spin up the same <see cref="McpServer.CreateDefault"/>
/// graph the production transport uses, so what passes here is what
/// an LLM client would see.
/// </summary>
public static class McpCommand
{
    public static Command Create(RevitClient client)
    {
        var mcp = new Command("mcp", "Model Context Protocol adapter (stdio + ad-hoc test commands).");
        mcp.AddCommand(CreateServeCommand(client));
        mcp.AddCommand(CreateListCommand(client));
        mcp.AddCommand(CreateTestCommand(client));
        mcp.AddCommand(CreateResourceCommand(client));
        return mcp;
    }

    // ─── serve ────────────────────────────────────────────────────────────

    private static Command CreateServeCommand(RevitClient client)
    {
        var serve = new Command("serve", "Run the MCP server on stdio (newline-delimited JSON-RPC 2.0).");
        var allowWrites = new Option<bool>(
            "--allow-writes",
            description: "Enable mutating tools (set/fix/rollback/import/publish). Each call still needs `confirm: true`.",
            getDefaultValue: () => false);
        serve.AddOption(allowWrites);

        serve.SetHandler(async (bool writes) =>
        {
            // stderr is the only safe log channel: stdout is reserved for protocol bytes.
            var server = McpServer.CreateDefault(client, Console.In, Console.Out, Console.Error, writes);
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };
            Environment.ExitCode = await server.RunAsync(cts.Token).ConfigureAwait(false);
        }, allowWrites);

        return serve;
    }

    // ─── list ─────────────────────────────────────────────────────────────

    private static Command CreateListCommand(RevitClient client)
    {
        var allowWrites = new Option<bool>(
            name: "--allow-writes",
            description: "List as if --allow-writes were set on `mcp serve`. The tool set is the same; write tools are always registered.",
            getDefaultValue: () => false);
        var outputOpt = new Option<string>("--output", () => "table", "Output format: table, json");

        var cmd = new Command("list", "List the MCP tools and resources this binary would expose")
        {
            allowWrites, outputOpt
        };

        cmd.SetHandler(async (bool writes, string output) =>
        {
            Environment.ExitCode = await ExecuteListAsync(client, writes, output, Console.Out);
        }, allowWrites, outputOpt);

        return cmd;
    }

    public static async Task<int> ExecuteListAsync(RevitClient client, bool allowWrites, string outputFormat, TextWriter output)
    {
        var (toolsResponse, _) = await DispatchAsync(client, allowWrites, "tools/list", new JsonObject());
        var (resourcesResponse, _) = await DispatchAsync(client, allowWrites, "resources/list", new JsonObject());

        var tools = toolsResponse?["result"]?["tools"]?.AsArray() ?? new JsonArray();
        var resources = resourcesResponse?["result"]?["resources"]?.AsArray() ?? new JsonArray();

        if (string.Equals(outputFormat, "json", StringComparison.OrdinalIgnoreCase))
        {
            var combined = new JsonObject
            {
                ["allowWrites"] = allowWrites,
                ["tools"] = tools.DeepClone(),
                ["resources"] = resources.DeepClone(),
            };
            await output.WriteLineAsync(combined.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }

        await output.WriteLineAsync($"Tools ({tools.Count})  --allow-writes={allowWrites}");
        await output.WriteLineAsync(new string('-', 60));
        foreach (var t in tools)
        {
            var name = t?["name"]?.GetValue<string>() ?? "(none)";
            var desc = t?["description"]?.GetValue<string>() ?? "";
            await output.WriteLineAsync($"  {name,-12}  {Truncate(desc, 80)}");
        }
        await output.WriteLineAsync();
        await output.WriteLineAsync($"Resources ({resources.Count})");
        await output.WriteLineAsync(new string('-', 60));
        foreach (var r in resources)
        {
            var uri = r?["uri"]?.GetValue<string>() ?? "(none)";
            var desc = r?["description"]?.GetValue<string>() ?? "";
            await output.WriteLineAsync($"  {uri,-32}  {Truncate(desc, 60)}");
        }
        return 0;
    }

    // ─── test ─────────────────────────────────────────────────────────────

    private static Command CreateTestCommand(RevitClient client)
    {
        var nameArg = new Argument<string>("tool", "Tool name (e.g. status, query, set, publish)");
        var argsOpt = new Option<string?>("--args",
            "JSON object passed as the tool's `arguments` (e.g. '{\"category\":\"doors\"}'). Default: {}.");
        var allowWrites = new Option<bool>(
            name: "--allow-writes",
            description: "Enable write tools for this call (same gate as `mcp serve --allow-writes`).",
            getDefaultValue: () => false);
        var verboseOpt = new Option<bool>("--verbose",
            "Print the full JSON-RPC response, not just the content text.");

        var cmd = new Command("test", "Hand-invoke a single MCP tool and print its response")
        {
            nameArg, argsOpt, allowWrites, verboseOpt
        };

        cmd.SetHandler(async (string name, string? argsJson, bool writes, bool verbose) =>
        {
            Environment.ExitCode = await ExecuteTestAsync(client, name, argsJson, writes, verbose, Console.Out, Console.Error);
        }, nameArg, argsOpt, allowWrites, verboseOpt);

        return cmd;
    }

    public static async Task<int> ExecuteTestAsync(
        RevitClient client, string toolName, string? argsJson, bool allowWrites, bool verbose,
        TextWriter stdout, TextWriter stderr)
    {
        JsonNode? toolArgs;
        try
        {
            toolArgs = string.IsNullOrWhiteSpace(argsJson) ? new JsonObject() : JsonNode.Parse(argsJson);
        }
        catch (System.Text.Json.JsonException ex)
        {
            await stderr.WriteLineAsync($"Error: --args is not valid JSON: {ex.Message}");
            return 1;
        }

        var requestParams = new JsonObject
        {
            ["name"] = toolName,
            ["arguments"] = toolArgs,
        };
        var (response, _) = await DispatchAsync(client, allowWrites, "tools/call", requestParams);
        if (response is null)
        {
            await stderr.WriteLineAsync("Error: dispatcher returned no response");
            return 1;
        }

        if (verbose)
        {
            await stdout.WriteLineAsync(response.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return response["error"] is null ? 0 : 1;
        }

        if (response["error"] is JsonObject error)
        {
            var msg = error["message"]?.GetValue<string>() ?? "(unknown error)";
            await stderr.WriteLineAsync($"Error: {msg}");
            return 1;
        }

        // Standard tool response shape: result.content[0].text + result.isError.
        var content = response["result"]?["content"]?.AsArray();
        if (content is null || content.Count == 0)
        {
            await stdout.WriteLineAsync("(empty content)");
            return 0;
        }
        foreach (var block in content)
        {
            var text = block?["text"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(text))
                await stdout.WriteLineAsync(text);
        }

        var isError = response["result"]?["isError"]?.GetValue<bool>() ?? false;
        return isError ? 1 : 0;
    }

    // ─── resource ─────────────────────────────────────────────────────────

    private static Command CreateResourceCommand(RevitClient client)
    {
        var uriArg = new Argument<string>("uri", "Resource URI (e.g. revitcli://snapshot/latest)");
        var verboseOpt = new Option<bool>("--verbose",
            "Print the full JSON-RPC response, not just the resource body.");

        var cmd = new Command("resource", "Read an MCP resource by URI and print its body")
        {
            uriArg, verboseOpt
        };

        cmd.SetHandler(async (string uri, bool verbose) =>
        {
            Environment.ExitCode = await ExecuteResourceAsync(client, uri, verbose, Console.Out, Console.Error);
        }, uriArg, verboseOpt);

        return cmd;
    }

    public static async Task<int> ExecuteResourceAsync(
        RevitClient client, string uri, bool verbose, TextWriter stdout, TextWriter stderr)
    {
        // Resources are always read-only — `--allow-writes` doesn't matter
        // for the resources/read path. Pass false to keep the test surface
        // identical to a default `mcp serve`.
        var (response, _) = await DispatchAsync(client, allowWrites: false, "resources/read",
            new JsonObject { ["uri"] = uri });

        if (response is null)
        {
            await stderr.WriteLineAsync("Error: dispatcher returned no response");
            return 1;
        }

        if (verbose)
        {
            await stdout.WriteLineAsync(response.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            return response["error"] is null ? 0 : 1;
        }

        if (response["error"] is JsonObject error)
        {
            var msg = error["message"]?.GetValue<string>() ?? "(unknown error)";
            await stderr.WriteLineAsync($"Error: {msg}");
            return 1;
        }

        var contents = response["result"]?["contents"]?.AsArray();
        if (contents is null || contents.Count == 0)
        {
            await stdout.WriteLineAsync("(empty resource)");
            return 0;
        }
        foreach (var item in contents)
        {
            var text = item?["text"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(text))
                await stdout.WriteLineAsync(text);
        }
        return 0;
    }

    // ─── shared dispatch ──────────────────────────────────────────────────

    /// <summary>
    /// Spin up an in-process <see cref="McpServer"/>, dispatch one
    /// JSON-RPC request, and return the parsed response. All three
    /// ad-hoc subcommands route through this helper so they exercise
    /// the SAME server graph production runs (CreateDefault), not a
    /// fake — what passes here is what an LLM client would see.
    /// </summary>
    internal static async Task<(JsonNode? Response, string RawOutput)> DispatchAsync(
        RevitClient client, bool allowWrites, string method, JsonNode @params)
    {
        var input = new StringReader(string.Empty);
        var output = new StringWriter();
        var logger = TextWriter.Null;
        var server = McpServer.CreateDefault(client, input, output, logger, allowWrites);

        var request = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = 1,
            ["method"] = method,
            ["params"] = @params,
        };
        await server.HandleLineAsync(request.ToJsonString(), CancellationToken.None);

        var raw = output.ToString().Trim();
        // Take only the first line carrying an `id` — that's the response.
        // Notifications (e.g. notifications/progress) carry no `id` and
        // would otherwise be misread as the response.
        foreach (var line in raw.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (string.IsNullOrEmpty(trimmed)) continue;
            try
            {
                var node = JsonNode.Parse(trimmed);
                if (node?["id"] is not null) return (node, raw);
            }
            catch (System.Text.Json.JsonException) { /* skip malformed */ }
        }
        return (null, raw);
    }

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s.Substring(0, Math.Max(0, max - 1)) + "…";
    }
}
