using System.CommandLine;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using RevitCli.Client;
using RevitCli.Output;
using RevitCli.Shared;

namespace RevitCli.Commands;

public static class QueryCommand
{
    private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };

    public static Command Create(RevitClient client)
    {
        var categoryArg = new Argument<string?>("category", () => null, "Element category (e.g. walls, doors, windows)");
        var filterOpt = new Option<string?>("--filter", "Filter expression (e.g. \"height > 3000\")");
        var idOpt = new Option<int?>("--id", "Query a specific element by ID");
        var outputOpt = new Option<string>("--output", () => "table", "Output format: table, json, csv");

        var command = new Command("query", "Query elements from the Revit model")
        {
            categoryArg, filterOpt, idOpt, outputOpt
        };

        command.SetHandler(async (category, filter, id, output) =>
        {
            await ExecuteAsync(client, category, filter, id, output, Console.Out);
        }, categoryArg, filterOpt, idOpt, outputOpt);

        return command;
    }

    public static async Task ExecuteAsync(RevitClient client, string? category, string? filter, int? id, string outputFormat, TextWriter output)
    {
        if (id.HasValue)
        {
            var result = await client.QueryElementByIdAsync(id.Value);
            if (!result.Success)
            {
                await output.WriteLineAsync($"Error: {result.Error}");
                return;
            }
            var formatted = OutputFormatter.FormatElements(new[] { result.Data! }, outputFormat);
            await output.WriteLineAsync(formatted);
            return;
        }

        if (category == null)
        {
            await output.WriteLineAsync("Error: provide a category or --id");
            return;
        }

        var queryResult = await client.QueryElementsAsync(category, filter);
        if (!queryResult.Success)
        {
            await output.WriteLineAsync($"Error: {queryResult.Error}");
            return;
        }

        var text = OutputFormatter.FormatElements(queryResult.Data!, outputFormat);
        await output.WriteLineAsync(text);
    }
}
