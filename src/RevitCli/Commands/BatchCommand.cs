using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using RevitCli.Client;
using RevitCli.Config;
using RevitCli.Output;
using Spectre.Console;

namespace RevitCli.Commands;

public class BatchItem
{
    [JsonPropertyName("command")]
    public string Command { get; set; } = "";

    [JsonPropertyName("args")]
    public List<string> Args { get; set; } = new();
}

public static class BatchCommand
{
    public static Command Create(RevitClient client, CliConfig config)
    {
        var fileArg = new Argument<string>("file", "Path to JSON batch file");
        var command = new Command("batch", "Execute commands from a JSON batch file")
        {
            fileArg
        };

        command.SetHandler(async (file) =>
        {
            if (!ConsoleHelper.IsInteractive)
            {
                var exitCode = await ExecuteAsync(client, config, file, Console.Out);
                Environment.ExitCode = exitCode;
                return;
            }

            await ExecuteSpectre(client, config, file);
        }, fileArg);

        return command;
    }

    public static async Task<int> ExecuteAsync(RevitClient client, CliConfig config, string file, TextWriter output)
    {
        if (!File.Exists(file))
        {
            await output.WriteLineAsync($"Error: file not found: {file}");
            return 1;
        }

        List<BatchItem> items;
        try
        {
            var json = await File.ReadAllTextAsync(file);
            items = JsonSerializer.Deserialize<List<BatchItem>>(json) ?? new();
        }
        catch (JsonException ex)
        {
            await output.WriteLineAsync($"Error: invalid JSON: {ex.Message}");
            return 1;
        }

        if (items.Count == 0)
        {
            await output.WriteLineAsync("No commands to execute.");
            return 0;
        }

        var hasError = false;
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            await output.WriteLineAsync($"[{i + 1}/{items.Count}] {item.Command} {string.Join(" ", item.Args)}");

            var exitCode = await DispatchCommand(client, config, item, output);
            if (exitCode != 0) hasError = true;

            await output.WriteLineAsync();
        }

        return hasError ? 1 : 0;
    }

    private static async Task ExecuteSpectre(RevitClient client, CliConfig config, string file)
    {
        if (!File.Exists(file))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] file not found: {Markup.Escape(file)}");
            Environment.ExitCode = 1;
            return;
        }

        List<BatchItem> items;
        try
        {
            var json = await File.ReadAllTextAsync(file);
            items = JsonSerializer.Deserialize<List<BatchItem>>(json) ?? new();
        }
        catch (JsonException ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] invalid JSON: {Markup.Escape(ex.Message)}");
            Environment.ExitCode = 1;
            return;
        }

        if (items.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No commands to execute.[/]");
            return;
        }

        var hasError = false;
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            AnsiConsole.MarkupLine($"[bold cyan][{i + 1}/{items.Count}][/] {Markup.Escape(item.Command)} {Markup.Escape(string.Join(" ", item.Args))}");

            var exitCode = await DispatchCommand(client, config, item, Console.Out);
            if (exitCode != 0) hasError = true;

            AnsiConsole.WriteLine();
        }

        if (hasError)
            Environment.ExitCode = 1;
    }

    private static async Task<int> DispatchCommand(RevitClient client, CliConfig config, BatchItem item, TextWriter output)
    {
        return item.Command.ToLower() switch
        {
            "status" => await StatusCommand.ExecuteAsync(client, output),
            "query" => await DispatchQuery(client, config, item.Args, output),
            "export" => await DispatchExport(client, config, item.Args, output),
            "set" => await DispatchSet(client, item.Args, output),
            "audit" => await AuditCommand.ExecuteAsync(client, ParseNamedArg(item.Args, "--rules"), output),
            "doctor" => await DoctorCommand.ExecuteAsync(client, config, output),
            _ => await WriteError(output, $"Unknown command: {item.Command}")
        };
    }

    private static async Task<int> DispatchQuery(RevitClient client, CliConfig config, List<string> args, TextWriter output)
    {
        string? category = args.Count > 0 && !args[0].StartsWith("-") ? args[0] : null;
        string? filter = ParseNamedArg(args, "--filter");
        int? id = int.TryParse(ParseNamedArg(args, "--id"), out var parsedId) ? parsedId : null;
        string outputFormat = ParseNamedArg(args, "--output") ?? config.DefaultOutput;

        return await QueryCommand.ExecuteAsync(client, category, filter, id, outputFormat, output);
    }

    private static async Task<int> DispatchExport(RevitClient client, CliConfig config, List<string> args, TextWriter output)
    {
        string format = ParseNamedArg(args, "--format") ?? "";
        string outputDir = ParseNamedArg(args, "--output-dir") ?? config.ExportDir;

        var sheets = new List<string>();
        var sheetsArg = ParseNamedArg(args, "--sheets");
        if (sheetsArg != null) sheets.Add(sheetsArg);

        return await ExportCommand.ExecuteAsync(client, format, sheets.ToArray(), outputDir, output);
    }

    private static async Task<int> DispatchSet(RevitClient client, List<string> args, TextWriter output)
    {
        string? category = args.Count > 0 && !args[0].StartsWith("-") ? args[0] : null;
        string? filter = ParseNamedArg(args, "--filter");
        int? id = int.TryParse(ParseNamedArg(args, "--id"), out var parsedId) ? parsedId : null;
        string param = ParseNamedArg(args, "--param") ?? "";
        string value = ParseNamedArg(args, "--value") ?? "";
        bool dryRun = args.Contains("--dry-run");

        return await SetCommand.ExecuteAsync(client, category, filter, id, param, value, dryRun, output);
    }

    private static string? ParseNamedArg(List<string> args, string name)
    {
        var idx = args.IndexOf(name);
        if (idx >= 0 && idx + 1 < args.Count)
            return args[idx + 1];
        return null;
    }

    private static async Task<int> WriteError(TextWriter output, string message)
    {
        await output.WriteLineAsync($"Error: {message}");
        return 1;
    }
}
