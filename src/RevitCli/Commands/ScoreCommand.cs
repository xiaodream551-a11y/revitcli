using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using RevitCli.Client;
using RevitCli.Output;
using RevitCli.Shared;
using Spectre.Console;

namespace RevitCli.Commands;

public static class ScoreCommand
{
    private static readonly Dictionary<string, int> RuleWeights = new(StringComparer.OrdinalIgnoreCase)
    {
        ["room-bounds"] = 15,
        ["unplaced-rooms"] = 10,
        ["duplicate-room-numbers"] = 15,
        ["room-metadata"] = 10,
        ["level-consistency"] = 10,
        ["naming"] = 5,
        ["views-not-on-sheets"] = 10,
        ["sheets-missing-info"] = 10,
        ["imported-dwg"] = 10,
        ["in-place-families"] = 5,
    };

    public static Command Create(RevitClient client)
    {
        var command = new Command("score", "Calculate model health score (0-100)");

        command.SetHandler(async () =>
        {
            if (!ConsoleHelper.IsInteractive)
            {
                Environment.ExitCode = await ExecuteAsync(client, Console.Out);
                return;
            }

            var result = await RunScore(client);
            if (result < 0)
            {
                AnsiConsole.MarkupLine("[red]Error: could not calculate score.[/]");
                Environment.ExitCode = 1;
                return;
            }

            var color = result >= 80 ? "green" : result >= 60 ? "yellow" : "red";
            var grade = result >= 90 ? "A" : result >= 80 ? "B" : result >= 70 ? "C" : result >= 60 ? "D" : "F";

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"  Model Health Score: [{color} bold]{result}[/] / 100  [{color}]({grade})[/]");
            AnsiConsole.WriteLine();
        });

        return command;
    }

    public static async Task<int> ExecuteAsync(RevitClient client, TextWriter output)
    {
        var score = await RunScore(client);
        if (score < 0)
        {
            await output.WriteLineAsync("Error: could not calculate score. Is Revit connected?");
            return 1;
        }

        var grade = score >= 90 ? "A" : score >= 80 ? "B" : score >= 70 ? "C" : score >= 60 ? "D" : "F";
        await output.WriteLineAsync($"Model Health Score: {score}/100 ({grade})");
        return 0;
    }

    private static async Task<int> RunScore(RevitClient client)
    {
        var ruleNames = AuditCommand.AvailableRules.ToList();
        var request = new AuditRequest { Rules = ruleNames };

        var result = await client.AuditAsync(request);
        if (!result.Success)
            return -1;

        var data = result.Data!;
        var totalWeight = 0;
        var earnedWeight = 0;

        var issuesByRule = data.Issues.GroupBy(i => i.Rule)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        foreach (var rule in AuditCommand.AvailableRules)
        {
            var weight = RuleWeights.GetValueOrDefault(rule, 5);
            totalWeight += weight;

            var issueCount = issuesByRule.GetValueOrDefault(rule, 0);
            if (issueCount == 0)
            {
                earnedWeight += weight;
            }
            else if (issueCount <= 3)
            {
                earnedWeight += weight / 2;
            }
        }

        return totalWeight == 0 ? 100 : (int)Math.Round(100.0 * earnedWeight / totalWeight);
    }
}
