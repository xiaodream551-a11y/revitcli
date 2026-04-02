using System.CommandLine;
using System.IO;
using System.Threading.Tasks;
using RevitCli.Client;
using Spectre.Console;

namespace RevitCli.Commands;

public static class StatusCommand
{
    public static Command Create(RevitClient client)
    {
        var command = new Command("status", "Check if Revit plugin is online");
        command.SetHandler(async () =>
        {
            var result = await client.GetStatusAsync();
            if (!result.Success)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(result.Error ?? "Unknown error")}");
                return;
            }

            var status = result.Data!;
            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("Property");
            table.AddColumn("Value");
            table.AddRow("Revit Version", $"[green]{Markup.Escape(status.RevitVersion)}[/]");
            table.AddRow("Document", status.DocumentName != null
                ? $"[cyan]{Markup.Escape(status.DocumentName)}[/]"
                : "[grey](none open)[/]");
            if (status.DocumentName != null && status.DocumentPath != null)
                table.AddRow("Path", Markup.Escape(status.DocumentPath));
            AnsiConsole.Write(table);
        });
        return command;
    }

    public static async Task ExecuteAsync(RevitClient client, TextWriter output)
    {
        var result = await client.GetStatusAsync();

        if (!result.Success)
        {
            await output.WriteLineAsync($"Error: {result.Error}");
            return;
        }

        var status = result.Data!;
        await output.WriteLineAsync($"Revit version: {status.RevitVersion}");
        if (status.DocumentName != null)
        {
            await output.WriteLineAsync($"Document:      {status.DocumentName}");
            if (status.DocumentPath != null)
                await output.WriteLineAsync($"Path:          {status.DocumentPath}");
        }
        else
        {
            await output.WriteLineAsync("Document:      (none open)");
        }
    }
}
