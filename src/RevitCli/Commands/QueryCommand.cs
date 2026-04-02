using System;
using System.CommandLine;
using System.IO;
using System.Threading.Tasks;
using RevitCli.Client;
using RevitCli.Config;
using RevitCli.Output;
using RevitCli.Shared;
using Spectre.Console;

namespace RevitCli.Commands;

public static class QueryCommand
{
    public static Command Create(RevitClient client, CliConfig config)
    {
        var categoryArg = new Argument<string?>("category", () => null, "Element category (e.g. walls, doors, windows)");
        var filterOpt = new Option<string?>("--filter", "Filter expression (e.g. \"height > 3000\")");
        var idOpt = new Option<int?>("--id", "Query a specific element by ID");
        var outputOpt = new Option<string>("--output", () => config.DefaultOutput, "Output format: table, json, csv");

        var command = new Command("query", "Query elements from the Revit model")
        {
            categoryArg, filterOpt, idOpt, outputOpt
        };

        command.SetHandler(async (category, filter, id, output) =>
        {
            if (!ConsoleHelper.IsInteractive)
            {
                Environment.ExitCode = await ExecuteAsync(client, category, filter, id, output, Console.Out);
                return;
            }

            if (id.HasValue)
            {
                var result = await client.QueryElementByIdAsync(id.Value);
                if (!result.Success)
                {
                    AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(result.Error ?? "Unknown error")}");
                    Environment.ExitCode = 1;
                    return;
                }
                OutputFormatter.WriteElementsToConsole(new[] { result.Data! }, output);
                return;
            }

            if (string.IsNullOrWhiteSpace(category))
            {
                AnsiConsole.MarkupLine("[red]Error:[/] provide a category or --id");
                Environment.ExitCode = 1;
                return;
            }

            var queryResult = await client.QueryElementsAsync(category, filter);
            if (!queryResult.Success)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(queryResult.Error ?? "Unknown error")}");
                Environment.ExitCode = 1;
                return;
            }

            OutputFormatter.WriteElementsToConsole(queryResult.Data!, output);
        }, categoryArg, filterOpt, idOpt, outputOpt);

        return command;
    }

    public static async Task<int> ExecuteAsync(RevitClient client, string? category, string? filter, int? id, string outputFormat, TextWriter output)
    {
        if (id.HasValue)
        {
            var result = await client.QueryElementByIdAsync(id.Value);
            if (!result.Success)
            {
                await output.WriteLineAsync($"Error: {result.Error}");
                return 1;
            }
            var formatted = OutputFormatter.FormatElements(new[] { result.Data! }, outputFormat);
            await output.WriteLineAsync(formatted);
            return 0;
        }

        if (string.IsNullOrWhiteSpace(category))
        {
            await output.WriteLineAsync("Error: provide a category or --id");
            return 1;
        }

        var queryResult = await client.QueryElementsAsync(category, filter);
        if (!queryResult.Success)
        {
            await output.WriteLineAsync($"Error: {queryResult.Error}");
            return 1;
        }

        var text = OutputFormatter.FormatElements(queryResult.Data!, outputFormat);
        await output.WriteLineAsync(text);
        return 0;
    }
}
