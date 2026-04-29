using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace RevitCli.Mcp.Tools;

/// <summary>
/// Tolerant readers for the <see cref="JsonObject"/> argument bag that
/// MCP <c>tools/call</c> hands to <see cref="IMcpTool.ExecuteAsync"/>.
///
/// Each helper:
///   - Returns the appropriate "missing" sentinel when the key is absent
///     or the value is JSON <c>null</c> — never throws.
///   - Treats an empty string the same as missing for <see cref="TryGetString"/>,
///     so tools can use <c>?? "default"</c> uniformly.
///   - Accepts a few benign type widenings (string-encoded numbers,
///     <c>int</c> for <c>long</c>) since LLM-emitted JSON sometimes
///     stringifies numbers.
///
/// Until this helper existed, every write tool (<see cref="FixTool"/>,
/// <see cref="RollbackTool"/>, <see cref="ImportTool"/>, <see cref="PublishTool"/>)
/// carried its own private copies. Drift across copies caused subtle
/// behavior diffs (e.g. <see cref="TryGetInt"/> only existed in some);
/// this module is the single source of truth.
/// </summary>
internal static class JsonArgs
{
    public static string? TryGetString(JsonObject args, string key)
    {
        if (!args.TryGetPropertyValue(key, out var node) || node is null) return null;
        if (node is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrEmpty(s))
            return s;
        return null;
    }

    public static bool? TryGetBool(JsonObject args, string key)
    {
        if (!args.TryGetPropertyValue(key, out var node) || node is null) return null;
        if (node is JsonValue v && v.TryGetValue<bool>(out var b)) return b;
        return null;
    }

    public static int? TryGetInt(JsonObject args, string key)
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

    public static long? TryGetLong(JsonObject args, string key)
    {
        if (!args.TryGetPropertyValue(key, out var node) || node is null) return null;
        if (node is JsonValue v)
        {
            if (v.TryGetValue<long>(out var l)) return l;
            if (v.TryGetValue<int>(out var i)) return i;
            if (v.TryGetValue<string>(out var s) && long.TryParse(s, out var parsed)) return parsed;
        }
        return null;
    }

    public static List<long>? TryGetLongArray(JsonObject args, string key)
    {
        if (!args.TryGetPropertyValue(key, out var node) || node is null) return null;
        if (node is not JsonArray arr) return null;
        var list = new List<long>();
        foreach (var entry in arr)
        {
            if (entry is JsonValue v)
            {
                if (v.TryGetValue<long>(out var l)) list.Add(l);
                else if (v.TryGetValue<int>(out var i)) list.Add(i);
            }
        }
        return list.Count == 0 ? null : list;
    }

    public static List<string>? TryGetStringArray(JsonObject args, string key)
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
