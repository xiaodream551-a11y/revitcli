using System;
using System.IO;
using System.Text.Json.Nodes;

namespace RevitCli.Mcp;

/// <summary>
/// Knows where each supported MCP client keeps its server-config
/// JSON, and how to merge a <c>revitcli</c> entry into that file
/// without trampling existing servers.
///
/// The actual file IO is split off from path resolution so tests can
/// drive merge logic with hand-built strings without touching the
/// real Claude Desktop config in the developer's home dir.
/// </summary>
internal static class McpClientConfig
{
    /// <summary>
    /// Supported clients. Each maps to a default config-file location.
    /// </summary>
    public enum ClientKind
    {
        ClaudeDesktop,
    }

    /// <summary>
    /// Resolve the platform-default path for <paramref name="client"/>.
    /// Returns <c>null</c> if the platform isn't recognized — the caller
    /// then needs <c>--config-path</c> to point us at the right file.
    /// </summary>
    public static string? ResolveDefaultPath(ClientKind client)
    {
        return client switch
        {
            ClientKind.ClaudeDesktop => ResolveClaudeDesktopPath(),
            _ => null,
        };
    }

    private static string? ResolveClaudeDesktopPath()
    {
        if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetEnvironmentVariable("HOME");
            if (string.IsNullOrEmpty(home)) return null;
            return Path.Combine(home, "Library", "Application Support", "Claude", "claude_desktop_config.json");
        }
        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetEnvironmentVariable("APPDATA");
            if (string.IsNullOrEmpty(appData)) return null;
            return Path.Combine(appData, "Claude", "claude_desktop_config.json");
        }
        if (OperatingSystem.IsLinux())
        {
            // Linux isn't an officially-supported Claude Desktop platform,
            // but the convention some unofficial builds follow is XDG.
            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrEmpty(xdg))
            {
                var home = Environment.GetEnvironmentVariable("HOME");
                if (string.IsNullOrEmpty(home)) return null;
                xdg = Path.Combine(home, ".config");
            }
            return Path.Combine(xdg, "Claude", "claude_desktop_config.json");
        }
        return null;
    }

    /// <summary>
    /// Merge a <c>revitcli</c> server entry into <paramref name="existingJson"/>.
    /// Preserves any other servers already configured. If the input is
    /// null/blank, returns a fresh config containing only the revitcli
    /// entry. Throws <see cref="InvalidJsonException"/> on a corrupt
    /// existing file (clearer than letting JsonNode bubble up).
    /// </summary>
    public static string MergeRevitCliEntry(
        string? existingJson,
        string command,
        bool allowWrites,
        string serverName = "revitcli")
    {
        JsonNode root;
        if (string.IsNullOrWhiteSpace(existingJson))
        {
            root = new JsonObject();
        }
        else
        {
            try
            {
                root = JsonNode.Parse(existingJson) ?? new JsonObject();
            }
            catch (System.Text.Json.JsonException ex)
            {
                throw new InvalidJsonException(
                    $"Existing config is not valid JSON: {ex.Message}", ex);
            }
        }

        if (root is not JsonObject obj)
            throw new InvalidJsonException("Existing config root is not a JSON object.");

        // Get-or-create the mcpServers map.
        var mcpServers = obj["mcpServers"] as JsonObject;
        if (mcpServers is null)
        {
            mcpServers = new JsonObject();
            obj["mcpServers"] = mcpServers;
        }

        // Build the args array — same shape Claude Desktop expects.
        var args = new JsonArray { "mcp", "serve" };
        if (allowWrites) args.Add("--allow-writes");

        // Replace any prior entry under this key — the operator is asking
        // us to (re)init, so a stale entry pointing at a moved binary
        // should be overwritten rather than leaving an inconsistent mix.
        mcpServers[serverName] = new JsonObject
        {
            ["command"] = command,
            ["args"] = args,
        };

        // JsonNode.ToJsonString(options) requires a TypeInfoResolver in
        // .NET 8 — copying from Default carries it through.
        var indented = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerOptions.Default)
        {
            WriteIndented = true,
        };
        return obj.ToJsonString(indented);
    }
}

/// <summary>
/// Thrown when an existing MCP-client config file isn't valid JSON
/// or has the wrong root shape. The CLI catches this and surfaces a
/// readable diagnostic — the alternative (raw <c>JsonException</c>)
/// would dump a parser error that operators can't act on.
/// </summary>
internal sealed class InvalidJsonException : Exception
{
    public InvalidJsonException(string message) : base(message) { }
    public InvalidJsonException(string message, Exception inner) : base(message, inner) { }
}
