using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using RevitCli.Shared;
using Spectre.Console;

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
            _ => FormatPlainTable(elements),
        };
    }

    /// <summary>
    /// Write elements to console with Spectre.Console rendering (colors, borders).
    /// Call this for interactive terminal output instead of FormatElements.
    /// </summary>
    public static void WriteElementsToConsole(ElementInfo[] elements, string format)
    {
        if (elements.Length == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No elements matched.[/]");
            return;
        }

        if (format.ToLower() != "table")
        {
            // json/csv still go through string formatting
            Console.WriteLine(FormatElements(elements, format));
            return;
        }

        var allParamKeys = elements
            .SelectMany(e => e.Parameters.Keys)
            .Distinct()
            .OrderBy(k => k)
            .ToList();

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn(new TableColumn("[bold]Id[/]").RightAligned());
        table.AddColumn("[bold]Name[/]");
        table.AddColumn("[bold]Category[/]");
        table.AddColumn("[bold]Type[/]");
        foreach (var key in allParamKeys)
            table.AddColumn($"[bold]{Markup.Escape(key)}[/]");

        foreach (var el in elements)
        {
            var row = new List<string>
            {
                el.Id.ToString(),
                $"[cyan]{Markup.Escape(el.Name)}[/]",
                Markup.Escape(el.Category),
                $"[green]{Markup.Escape(el.TypeName)}[/]"
            };
            foreach (var key in allParamKeys)
                row.Add(Markup.Escape(el.Parameters.TryGetValue(key, out var v) ? v : ""));
            table.AddRow(row.ToArray());
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"[grey]({elements.Length} element(s))[/]");
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
            sb.Append($"{el.Id},{CsvEscape(el.Name)},{CsvEscape(el.Category)},{CsvEscape(el.TypeName)}");
            foreach (var key in allParamKeys)
                sb.Append($",{CsvEscape(el.Parameters.TryGetValue(key, out var v) ? v : "")}");
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    private static string CsvEscape(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }

    private static string FormatPlainTable(ElementInfo[] elements)
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
