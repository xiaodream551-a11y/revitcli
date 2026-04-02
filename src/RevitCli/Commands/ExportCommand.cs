using System.CommandLine;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using RevitCli.Client;
using RevitCli.Shared;
using Spectre.Console;

namespace RevitCli.Commands;

public static class ExportCommand
{
    private static readonly string[] ValidFormats = { "dwg", "pdf", "ifc" };

    public static Command Create(RevitClient client)
    {
        var formatOpt = new Option<string>("--format", "Export format: dwg, pdf, ifc") { IsRequired = true };
        var sheetsOpt = new Option<string[]>("--sheets", () => System.Array.Empty<string>(), "Sheet name patterns (e.g. \"A1*\", \"all\")");
        var outputDirOpt = new Option<string>("--output-dir", () => ".", "Output directory for exported files");

        var command = new Command("export", "Export sheets or views from the Revit model")
        {
            formatOpt, sheetsOpt, outputDirOpt
        };

        command.SetHandler(async (format, sheets, outputDir) =>
        {
            if (string.IsNullOrEmpty(format) || !ValidFormats.Contains(format.ToLower()))
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] --format must be one of: {string.Join(", ", ValidFormats)}");
                return;
            }

            var request = new ExportRequest
            {
                Format = format.ToLower(),
                Sheets = sheets.ToList(),
                OutputDir = Path.GetFullPath(outputDir)
            };

            var result = await client.ExportAsync(request);
            if (!result.Success)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(result.Error ?? "Unknown error")}");
                return;
            }

            var progress = result.Data!;
            if (progress.Status == "completed")
            {
                AnsiConsole.MarkupLine($"[green]Export completed.[/] Task ID: {progress.TaskId}");
                return;
            }

            await AnsiConsole.Progress()
                .AutoClear(false)
                .Columns(new ProgressColumn[]
                {
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new SpinnerColumn(),
                })
                .StartAsync(async ctx =>
                {
                    var task = ctx.AddTask($"[cyan]Exporting {format.ToUpper()}[/]", maxValue: 100);
                    task.Value = progress.Progress;

                    while (progress.Status != "completed" && progress.Status != "failed")
                    {
                        await Task.Delay(1000);
                        var pollResult = await client.GetExportProgressAsync(progress.TaskId);
                        if (!pollResult.Success) break;
                        progress = pollResult.Data!;
                        task.Value = progress.Progress;
                    }

                    task.Value = 100;
                });

            if (progress.Status == "completed")
                AnsiConsole.MarkupLine("[green]Export completed.[/]");
            else if (progress.Status == "failed")
                AnsiConsole.MarkupLine($"[red]Export failed:[/] {Markup.Escape(progress.Message ?? "Unknown error")}");
        }, formatOpt, sheetsOpt, outputDirOpt);

        return command;
    }

    public static async Task ExecuteAsync(RevitClient client, string format, string[] sheets, string outputDir, TextWriter output)
    {
        if (string.IsNullOrEmpty(format) || !ValidFormats.Contains(format.ToLower()))
        {
            await output.WriteLineAsync($"Error: --format must be one of: {string.Join(", ", ValidFormats)}");
            return;
        }

        var request = new ExportRequest
        {
            Format = format.ToLower(),
            Sheets = sheets.ToList(),
            OutputDir = Path.GetFullPath(outputDir)
        };

        var result = await client.ExportAsync(request);

        if (!result.Success)
        {
            await output.WriteLineAsync($"Error: {result.Error}");
            return;
        }

        var progress = result.Data!;
        if (progress.Status == "completed")
        {
            await output.WriteLineAsync($"Export completed. Task ID: {progress.TaskId}");
            return;
        }

        await output.WriteLineAsync($"Export started. Task ID: {progress.TaskId}");
        await output.WriteLineAsync($"Status: {progress.Status}, Progress: {progress.Progress}%");

        while (progress.Status != "completed" && progress.Status != "failed")
        {
            await Task.Delay(1000);
            var pollResult = await client.GetExportProgressAsync(progress.TaskId);
            if (!pollResult.Success) break;
            progress = pollResult.Data!;
            await output.WriteLineAsync($"Progress: {progress.Progress}%");
        }

        if (progress.Status == "completed")
            await output.WriteLineAsync("Export completed.");
        else if (progress.Status == "failed")
            await output.WriteLineAsync($"Export failed: {progress.Message}");
    }
}
