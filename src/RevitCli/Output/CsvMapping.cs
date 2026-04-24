using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitCli.Output;

public static class CsvMapping
{
    /// <summary>
    /// Build CSV-column → Revit-parameter mapping.
    /// - matchBy column is excluded (it is the lookup key, not a writable target).
    /// - Columns mentioned in rawMap get the explicit Revit-parameter name.
    /// - Columns not in rawMap default to identity (csv column name == Revit param name).
    /// </summary>
    public static Dictionary<string, string> Build(
        string? rawMap,
        IReadOnlyList<string> headers,
        string matchBy)
    {
        if (!headers.Contains(matchBy))
            throw new InvalidOperationException(
                $"--match-by column '{matchBy}' not found in CSV headers: {string.Join(", ", headers)}");

        var explicitMap = ParseRawMap(rawMap, headers);

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var header in headers)
        {
            if (header == matchBy) continue;
            result[header] = explicitMap.TryGetValue(header, out var revitParam) ? revitParam : header;
        }
        return result;
    }

    private static Dictionary<string, string> ParseRawMap(string? raw, IReadOnlyList<string> headers)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(raw))
            return map;

        var headerSet = new HashSet<string>(headers, StringComparer.Ordinal);
        foreach (var pair in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = pair.Trim();
            var idx = trimmed.IndexOf(':');
            if (idx <= 0 || idx == trimmed.Length - 1)
                throw new InvalidOperationException(
                    $"--map: invalid pair '{trimmed}'. Expected 'csvColumn:revitParam'.");

            var csvCol = trimmed.Substring(0, idx).Trim();
            var revitParam = trimmed.Substring(idx + 1).Trim();

            if (!headerSet.Contains(csvCol))
                throw new InvalidOperationException(
                    $"--map: CSV column '{csvCol}' not found in headers: {string.Join(", ", headers)}");

            map[csvCol] = revitParam;
        }
        return map;
    }
}
