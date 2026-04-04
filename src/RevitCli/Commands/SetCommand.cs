using System;
using System.CommandLine;
using System.IO;
using System.Threading.Tasks;
using RevitCli.Client;
using RevitCli.Output;
using RevitCli.Shared;
using Spectre.Console;

namespace RevitCli.Commands;

public static class SetCommand
{
    public static Command Create(RevitClient client)
    {
        var categoryArg = new Argument<string?>("category", () => null, "Element category (e.g. doors, walls)");
        var filterOpt = new Option<string?>("--filter", "Filter expression (e.g. \"height > 3000\")");
        var idOpt = new Option<long?>("--id", "Target a specific element by ID");
        var paramOpt = new Option<string>("--param", "Parameter name to modify") { IsRequired = true };
        var valueOpt = new Option<string>("--value", "New parameter value") { IsRequired = true };
        var dryRunOpt = new Option<bool>("--dry-run", "Preview changes without applying");

        var command = new Command("set", "Modify element parameters in the Revit model")
        {
            categoryArg, filterOpt, idOpt, paramOpt, valueOpt, dryRunOpt
        };

        command.SetHandler(async (category, filter, id, param, value, dryRun) =>
        {
            if (!ConsoleHelper.IsInteractive)
            {
                Environment.ExitCode = await ExecuteAsync(client, category, filter, id, param, value, dryRun, Console.Out);
                return;
            }

            if (string.IsNullOrEmpty(param))
            {
                AnsiConsole.MarkupLine("[red]Error:[/] --param is required.");
                Environment.ExitCode = 1;
                return;
            }

            if (category == null && !id.HasValue)
            {
                AnsiConsole.MarkupLine("[red]Error:[/] provide a category or --id to target elements.");
                Environment.ExitCode = 1;
                return;
            }

            var request = new SetRequest
            {
                Category = category,
                ElementId = id,
                Filter = filter,
                Param = param,
                Value = value,
                DryRun = dryRun
            };

            var result = await client.SetParameterAsync(request);

            if (!result.Success)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(result.Error ?? "Unknown error")}");
                Environment.ExitCode = 1;
                return;
            }

            var data = result.Data!;

            if (dryRun)
            {
                AnsiConsole.MarkupLine($"[yellow]Dry run:[/] {data.Affected} element(s) would be modified.");
                if (data.Preview.Count > 0)
                {
                    var previewTable = new Table().Border(TableBorder.Rounded);
                    previewTable.AddColumn("[bold]Id[/]");
                    previewTable.AddColumn("[bold]Name[/]");
                    previewTable.AddColumn("[bold]Old Value[/]");
                    previewTable.AddColumn("[bold]New Value[/]");
                    foreach (var item in data.Preview)
                        previewTable.AddRow(
                            item.Id.ToString(),
                            Markup.Escape(item.Name),
                            $"[red]{Markup.Escape(item.OldValue ?? "")}[/]",
                            $"[green]{Markup.Escape(item.NewValue)}[/]");
                    AnsiConsole.Write(previewTable);
                }
                return;
            }

            AnsiConsole.MarkupLine($"Modified [green]{data.Affected}[/] element(s).");
        }, categoryArg, filterOpt, idOpt, paramOpt, valueOpt, dryRunOpt);

        return command;
    }

    public static async Task<int> ExecuteAsync(RevitClient client, string? category, string? filter, long? id, string param, string value, bool dryRun, TextWriter output)
    {
        if (string.IsNullOrEmpty(param))
        {
            await output.WriteLineAsync("Error: --param is required.");
            return 1;
        }

        if (category == null && !id.HasValue)
        {
            await output.WriteLineAsync("Error: provide a category or --id to target elements.");
            return 1;
        }

        var request = new SetRequest
        {
            Category = category,
            ElementId = id,
            Filter = filter,
            Param = param,
            Value = value,
            DryRun = dryRun
        };

        var result = await client.SetParameterAsync(request);

        if (!result.Success)
        {
            await output.WriteLineAsync($"Error: {result.Error}");
            return 1;
        }

        var data = result.Data!;

        if (dryRun)
        {
            await output.WriteLineAsync($"Dry run: {data.Affected} element(s) would be modified.");
            foreach (var item in data.Preview)
                await output.WriteLineAsync($"  [{item.Id}] {item.Name}: \"{item.OldValue}\" -> \"{item.NewValue}\"");
            return 0;
        }

        await output.WriteLineAsync($"Modified {data.Affected} element(s).");
        return 0;
    }
}
