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

            var exitCode = await DispatchViaParser(client, config, item);
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

            var exitCode = await DispatchViaParser(client, config, item);
            if (exitCode != 0) hasError = true;

            AnsiConsole.WriteLine();
        }

        if (hasError)
            Environment.ExitCode = 1;
    }

    /// <summary>
    /// Dispatch batch items through the same System.CommandLine parser/handler as the real CLI.
    /// This guarantees batch and interactive CLI have identical validation, type binding, and error behavior.
    /// </summary>
    private static async Task<int> DispatchViaParser(RevitClient client, CliConfig config, BatchItem item)
    {
        var root = BuildRootCommand(client, config);

        // Prepend the command name to the args array, same as if typed on the CLI
        var fullArgs = new List<string> { item.Command };
        fullArgs.AddRange(item.Args);

        return await root.InvokeAsync(fullArgs.ToArray());
    }

    private static RootCommand BuildRootCommand(RevitClient client, CliConfig config)
    {
        var root = new RootCommand();
        root.AddCommand(StatusCommand.Create(client));
        root.AddCommand(QueryCommand.Create(client, config));
        root.AddCommand(ExportCommand.Create(client, config));
        root.AddCommand(SetCommand.Create(client));
        root.AddCommand(ConfigCommand.Create());
        root.AddCommand(AuditCommand.Create(client));
        root.AddCommand(DoctorCommand.Create(client, config));
        return root;
    }
}
