using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using RevitCli.Client;
using RevitCli.Output;
using RevitCli.Shared;
using Spectre.Console;

namespace RevitCli.Commands;

public static class CoverageCommand
{
    private static readonly string[] DefaultCategories = { "walls", "doors", "windows", "rooms", "floors" };

    public static Command Create(RevitClient client)
    {
        var categoriesOpt = new Option<string?>("--categories",
            $"Comma-separated categories to check (default: {string.Join(", ", DefaultCategories)})");
        var outputOpt = new Option<string>("--output", () => "table", "Output format: table, json");

        var command = new Command("coverage", "Show parameter fill rates by category")
        {
            categoriesOpt, outputOpt
        };

        command.SetHandler(async (categories, outputFormat) =>
        {
            Environment.ExitCode = await ExecuteAsync(client, categories, outputFormat, Console.Out);
        }, categoriesOpt, outputOpt);

        return command;
    }

    public static async Task<int> ExecuteAsync(RevitClient client, string? categories, string outputFormat, TextWriter output)
    {
        var categoryList = !string.IsNullOrEmpty(categories)
            ? categories.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
            : DefaultCategories.ToList();

        var results = new List<CategoryCoverage>();

        foreach (var category in categoryList)
        {
            var queryResult = await client.QueryElementsAsync(category, null);
            if (!queryResult.Success)
            {
                results.Add(new CategoryCoverage { Category = category, Error = queryResult.Error });
                continue;
            }

            var elements = queryResult.Data!;
            if (elements.Length == 0)
            {
                results.Add(new CategoryCoverage { Category = category, ElementCount = 0 });
                continue;
            }

            // Collect all parameter names across elements
            var paramNames = elements
                .SelectMany(e => e.Parameters.Keys)
                .Distinct()
                .OrderBy(k => k)
                .ToList();

            var paramStats = new Dictionary<string, (int filled, int total)>();
            foreach (var param in paramNames)
            {
                var filled = elements.Count(e =>
                    e.Parameters.TryGetValue(param, out var v) && !string.IsNullOrWhiteSpace(v));
                paramStats[param] = (filled, elements.Length);
            }

            results.Add(new CategoryCoverage
            {
                Category = category,
                ElementCount = elements.Length,
                Parameters = paramStats.ToDictionary(
                    kvp => kvp.Key,
                    kvp => (double)kvp.Value.filled / kvp.Value.total * 100)
            });
        }

        if (outputFormat.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            var json = System.Text.Json.JsonSerializer.Serialize(results.Select(r => new
            {
                category = r.Category,
                elements = r.ElementCount,
                error = r.Error,
                parameters = r.Parameters?.ToDictionary(k => k.Key, k => Math.Round(k.Value, 1))
            }), new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            await output.WriteLineAsync(json);
        }
        else
        {
            foreach (var r in results)
            {
                if (r.Error != null)
                {
                    await output.WriteLineAsync($"{r.Category}: error - {r.Error}");
                    continue;
                }
                if (r.ElementCount == 0)
                {
                    await output.WriteLineAsync($"{r.Category}: no elements");
                    continue;
                }

                await output.WriteLineAsync($"{r.Category} ({r.ElementCount} elements):");
                if (r.Parameters != null)
                {
                    foreach (var (param, pct) in r.Parameters.OrderByDescending(p => p.Value))
                    {
                        var bar = new string('#', (int)(pct / 5));
                        var pad = new string('.', 20 - bar.Length);
                        await output.WriteLineAsync($"  {param,-30} [{bar}{pad}] {pct:F0}%");
                    }
                }
                await output.WriteLineAsync("");
            }
        }

        if (!outputFormat.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            await output.WriteLineAsync("Note: only parameters with at least one value are shown.");
            await output.WriteLineAsync("Parameters absent from all elements are not listed.");
        }

        return 0;
    }

    private class CategoryCoverage
    {
        public string Category { get; set; } = "";
        public int ElementCount { get; set; }
        public string? Error { get; set; }
        public Dictionary<string, double>? Parameters { get; set; }
    }
}
