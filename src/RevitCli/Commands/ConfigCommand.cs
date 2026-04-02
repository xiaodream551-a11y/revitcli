using System.CommandLine;
using System.Linq;
using RevitCli.Config;
using Spectre.Console;

namespace RevitCli.Commands;

public static class ConfigCommand
{
    public static Command Create()
    {
        var command = new Command("config", "View or modify CLI configuration");

        var showCommand = new Command("show", "Show current configuration");
        showCommand.SetHandler(() =>
        {
            var config = CliConfig.Load();
            var table = new Table().Border(TableBorder.Rounded);
            table.AddColumn("[bold]Setting[/]");
            table.AddColumn("[bold]Value[/]");
            table.AddRow("serverUrl", $"[cyan]{Markup.Escape(config.ServerUrl)}[/]");
            table.AddRow("defaultOutput", $"[green]{Markup.Escape(config.DefaultOutput)}[/]");
            table.AddRow("exportDir", Markup.Escape(config.ExportDir));
            AnsiConsole.Write(table);
        });

        var setCommand = new Command("set", "Set a configuration value");
        var keyArg = new Argument<string>("key", "Setting name (serverUrl, defaultOutput, exportDir)");
        var valueArg = new Argument<string>("value", "New value");
        setCommand.AddArgument(keyArg);
        setCommand.AddArgument(valueArg);
        setCommand.SetHandler((key, value) =>
        {
            var config = CliConfig.Load();
            switch (key.ToLower())
            {
                case "serverurl":
                    if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
                        (uri.Scheme != "http" && uri.Scheme != "https"))
                    {
                        AnsiConsole.MarkupLine($"[red]Invalid URL:[/] {Markup.Escape(value)}");
                        return;
                    }
                    config.ServerUrl = value;
                    break;
                case "defaultoutput":
                    var validFormats = new[] { "table", "json", "csv" };
                    if (!validFormats.Contains(value.ToLower()))
                    {
                        AnsiConsole.MarkupLine($"[red]Invalid format:[/] must be one of: {string.Join(", ", validFormats)}");
                        return;
                    }
                    config.DefaultOutput = value.ToLower();
                    break;
                case "exportdir":
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        AnsiConsole.MarkupLine("[red]Invalid directory:[/] cannot be empty");
                        return;
                    }
                    config.ExportDir = value;
                    break;
                default:
                    AnsiConsole.MarkupLine($"[red]Unknown setting:[/] {Markup.Escape(key)}");
                    return;
            }
            config.Save();
            AnsiConsole.MarkupLine($"[green]Set[/] {Markup.Escape(key)} = {Markup.Escape(value)}");
        }, keyArg, valueArg);

        command.AddCommand(showCommand);
        command.AddCommand(setCommand);
        return command;
    }
}
