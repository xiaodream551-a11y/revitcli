using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace RevitCli.Shared;

public static class SnapshotHasher
{
    private const int HashLength = 16;

    public static string HashElement(SnapshotElement e)
    {
        var sb = new StringBuilder();
        sb.Append("id=").Append(e.Id).Append('\n');
        sb.Append("name=").Append(Escape(e.Name ?? "")).Append('\n');
        sb.Append("typeName=").Append(Escape(e.TypeName ?? "")).Append('\n');
        foreach (var kv in (e.Parameters ?? new Dictionary<string, string>())
                 .OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            sb.Append(Escape(kv.Key)).Append('=').Append(Escape(kv.Value ?? "")).Append('\n');
        }
        return Sha256Short(sb.ToString());
    }

    public static string HashSheetMeta(SnapshotSheet s)
    {
        var sb = new StringBuilder();
        sb.Append("number=").Append(Escape(s.Number ?? "")).Append('\n');
        sb.Append("name=").Append(Escape(s.Name ?? "")).Append('\n');
        sb.Append("viewId=").Append(s.ViewId).Append('\n');
        foreach (var kv in (s.Parameters ?? new Dictionary<string, string>())
                 .OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            sb.Append(Escape(kv.Key)).Append('=').Append(Escape(kv.Value ?? "")).Append('\n');
        }
        return Sha256Short(sb.ToString());
    }

    public static string HashSchedule(
        string category,
        string name,
        List<string> columns,
        List<Dictionary<string, string>> rows)
    {
        var sb = new StringBuilder();
        sb.Append("category=").Append(Escape(category ?? "")).Append('\n');
        sb.Append("name=").Append(Escape(name ?? "")).Append('\n');
        sb.Append("columns=").Append(string.Join("|", (columns ?? new List<string>()).Select(Escape))).Append('\n');
        foreach (var row in rows ?? new List<Dictionary<string, string>>())
        {
            var line = string.Join("|",
                (columns ?? new List<string>()).Select(c =>
                    row.TryGetValue(c, out var v) ? Escape(v ?? "") : ""));
            sb.Append(line).Append('\n');
        }
        return Sha256Short(sb.ToString());
    }

    private static string Escape(string s) =>
        s.Replace("\\", "\\\\").Replace("\n", "\\n").Replace("|", "\\|");

    private static string Sha256Short(string content)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(content));
        var sb = new StringBuilder(HashLength);
        for (int i = 0; i < HashLength / 2; i++)
            sb.Append(bytes[i].ToString("x2"));
        return sb.ToString();
    }
}
