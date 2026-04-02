using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using RevitCli.Shared;

namespace RevitCli.Output;

public static class OutputFormatter
{
    private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };

    public static string FormatElements(ElementInfo[] elements, string format)
    {
        if (elements.Length == 0)
            return "No elements matched.";

        return format.ToLower() switch
        {
            "json" => JsonSerializer.Serialize(elements, PrettyJson),
            "csv" => FormatCsv(elements),
            _ => FormatTable(elements),
        };
    }

    private static string FormatCsv(ElementInfo[] elements)
    {
        var sb = new StringBuilder();
        var allParamKeys = elements
            .SelectMany(e => e.Parameters.Keys)
            .Distinct()
            .OrderBy(k => k)
            .ToList();

        sb.Append("Id,Name,Category,TypeName");
        foreach (var key in allParamKeys)
            sb.Append($",{key}");
        sb.AppendLine();

        foreach (var el in elements)
        {
            sb.Append($"{el.Id},{el.Name},{el.Category},{el.TypeName}");
            foreach (var key in allParamKeys)
                sb.Append($",{(el.Parameters.TryGetValue(key, out var v) ? v : "")}");
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    private static string FormatTable(ElementInfo[] elements)
    {
        var sb = new StringBuilder();
        var allParamKeys = elements
            .SelectMany(e => e.Parameters.Keys)
            .Distinct()
            .OrderBy(k => k)
            .ToList();

        var headers = new[] { "Id", "Name", "Category", "Type" }.Concat(allParamKeys).ToArray();
        var widths = new int[headers.Length];
        for (int i = 0; i < headers.Length; i++)
            widths[i] = headers[i].Length;

        var rows = elements.Select(el =>
        {
            var baseFields = new[] { el.Id.ToString(), el.Name, el.Category, el.TypeName };
            var paramFields = allParamKeys.Select(k => el.Parameters.TryGetValue(k, out var v) ? v : "").ToArray();
            return baseFields.Concat(paramFields).ToArray();
        }).ToList();

        foreach (var row in rows)
            for (int i = 0; i < row.Length && i < widths.Length; i++)
                widths[i] = Math.Max(widths[i], row[i].Length);

        for (int i = 0; i < headers.Length; i++)
            sb.Append(headers[i].PadRight(widths[i] + 2));
        sb.AppendLine();

        for (int i = 0; i < headers.Length; i++)
            sb.Append(new string('-', widths[i] + 2));
        sb.AppendLine();

        foreach (var row in rows)
        {
            for (int i = 0; i < row.Length && i < widths.Length; i++)
                sb.Append(row[i].PadRight(widths[i] + 2));
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }
}
