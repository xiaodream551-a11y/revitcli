using System;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using RevitCli.Client;
using RevitCli.Output;
using RevitCli.Shared;
using Spectre.Console;

namespace RevitCli.Commands;

public static class AuditCommand
{
    internal static readonly string[] AvailableRules = { "naming", "clash", "room-bounds", "level-consistency", "unplaced-rooms" };

    public static Command Create(RevitClient client)
    {
        var rulesOpt = new Option<string?>("--rules", "Comma-separated list of rules to run (e.g. \"naming,clash\")");
        var listOpt = new Option<bool>("--list", "List all available audit rules");

        var command = new Command("audit", "Run model checking rules against the Revit model")
        {
            rulesOpt, listOpt
        };

        command.SetHandler(async (rules, list) =>
        {
            if (list)
            {
                if (!ConsoleHelper.IsInteractive)
                {
                    Console.WriteLine("Available rules: naming, clash, room-bounds, level-consistency, unplaced-rooms");
                    return;
                }

                var table = new Table().Border(TableBorder.Rounded);
                table.AddColumn("[bold]Rule[/]");
                table.AddColumn("[bold]Description[/]");
                table.AddRow("naming", "Check element naming conventions");
                table.AddRow("clash", "Detect element clashes/intersections");
                table.AddRow("room-bounds", "Verify all rooms are properly bounded");
                table.AddRow("level-consistency", "Check level naming and elevation consistency");
                table.AddRow("unplaced-rooms", "Find unplaced room elements");
                AnsiConsole.Write(table);
                return;
            }

            Environment.ExitCode = await ExecuteAsync(client, rules, Console.Out);
        }, rulesOpt, listOpt);

        return command;
    }

    public static async Task<int> ExecuteAsync(RevitClient client, string? rules, TextWriter output)
    {
        var ruleList = rules?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                           .ToList() ?? AvailableRules.ToList();

        var invalidRules = ruleList.Where(r => !AvailableRules.Contains(r)).ToList();
        if (invalidRules.Count > 0)
        {
            await output.WriteLineAsync($"Error: unknown rule(s): {string.Join(", ", invalidRules)}");
            await output.WriteLineAsync($"Available rules: {string.Join(", ", AvailableRules)}");
            return 1;
        }

        var request = new AuditRequest { Rules = ruleList };
        var result = await client.AuditAsync(request);

        if (!result.Success)
        {
            await output.WriteLineAsync($"Error: {result.Error}");
            return 1;
        }

        var data = result.Data!;
        await output.WriteLineAsync($"Audit complete: {data.Passed} passed, {data.Failed} failed");

        foreach (var issue in data.Issues)
        {
            var prefix = issue.Severity == "error" ? "ERROR" : issue.Severity == "warning" ? "WARN" : "INFO";
            var elementRef = issue.ElementId.HasValue ? $" [Element {issue.ElementId}]" : "";
            await output.WriteLineAsync($"  [{prefix}] {issue.Rule}: {issue.Message}{elementRef}");
        }
        return 0;
    }
}
