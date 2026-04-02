using System;
using System.CommandLine;
using System.Reflection;
using System.Threading.Tasks;
using RevitCli.Client;
using RevitCli.Config;
using Spectre.Console;

namespace RevitCli.Commands;

public static class InteractiveCommand
{
    public static Command Create(RevitClient client, CliConfig config)
    {
        var command = new Command("interactive", "Enter interactive REPL mode");

        command.SetHandler(async () =>
        {
            var version = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion ?? "0.1.0";

            AnsiConsole.MarkupLine($"[bold green]RevitCli[/] v{version} - [cyan]Interactive Mode[/]");
            AnsiConsole.MarkupLine("[grey]Type 'help' for commands, 'exit' to quit.[/]");
            AnsiConsole.WriteLine();

            while (true)
            {
                AnsiConsole.Markup("[bold blue]revitcli>[/] ");
                var input = Console.ReadLine()?.Trim();

                if (string.IsNullOrEmpty(input))
                    continue;

                if (input is "exit" or "quit" or "q")
                {
                    AnsiConsole.MarkupLine("[grey]Goodbye.[/]");
                    break;
                }

                if (input == "help")
                {
                    PrintHelp();
                    continue;
                }

                if (input is "clear" or "cls")
                {
                    AnsiConsole.Clear();
                    continue;
                }

                // Parse input and dispatch to the appropriate command
                var args = SplitArgs(input);
                var rootCommand = BuildRootCommand(client, config);
                await rootCommand.InvokeAsync(args);
                AnsiConsole.WriteLine();
            }
        });

        return command;
    }

    private static void PrintHelp()
    {
        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("[bold]Command[/]");
        table.AddColumn("[bold]Description[/]");
        table.AddRow("status", "Check if Revit plugin is online");
        table.AddRow("query <category>", "Query elements (--filter, --id, --output)");
        table.AddRow("export --format <fmt>", "Export sheets (--sheets, --output-dir)");
        table.AddRow("set <category>", "Modify parameters (--param, --value, --dry-run)");
        table.AddRow("config show/set", "View or modify configuration");
        table.AddRow("audit", "Run model checking rules (--rules, --list)");
        table.AddRow("clear", "Clear the screen");
        table.AddRow("exit", "Exit interactive mode");
        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
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

    /// <summary>
    /// Split input string into args, respecting quoted strings.
    /// </summary>
    internal static string[] SplitArgs(string input)
    {
        var args = new System.Collections.Generic.List<string>();
        var current = new System.Text.StringBuilder();
        var inQuote = false;
        var quoteChar = '"';

        foreach (var c in input)
        {
            if (inQuote)
            {
                if (c == quoteChar)
                    inQuote = false;
                else
                    current.Append(c);
            }
            else if (c is '"' or '\'')
            {
                inQuote = true;
                quoteChar = c;
            }
            else if (c == ' ')
            {
                if (current.Length > 0)
                {
                    args.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
            args.Add(current.ToString());

        return args.ToArray();
    }
}
