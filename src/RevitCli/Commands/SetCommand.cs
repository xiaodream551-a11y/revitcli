using System.CommandLine;
using System.IO;
using System.Threading.Tasks;
using RevitCli.Client;
using RevitCli.Shared;

namespace RevitCli.Commands;

public static class SetCommand
{
    public static Command Create(RevitClient client)
    {
        var categoryArg = new Argument<string?>("category", () => null, "Element category (e.g. doors, walls)");
        var filterOpt = new Option<string?>("--filter", "Filter expression (e.g. \"height > 3000\")");
        var idOpt = new Option<int?>("--id", "Target a specific element by ID");
        var paramOpt = new Option<string>("--param", "Parameter name to modify") { IsRequired = true };
        var valueOpt = new Option<string>("--value", "New parameter value") { IsRequired = true };
        var dryRunOpt = new Option<bool>("--dry-run", "Preview changes without applying");

        var command = new Command("set", "Modify element parameters in the Revit model")
        {
            categoryArg, filterOpt, idOpt, paramOpt, valueOpt, dryRunOpt
        };

        command.SetHandler(async (category, filter, id, param, value, dryRun) =>
        {
            await ExecuteAsync(client, category, filter, id, param, value, dryRun, Console.Out);
        }, categoryArg, filterOpt, idOpt, paramOpt, valueOpt, dryRunOpt);

        return command;
    }

    public static async Task ExecuteAsync(RevitClient client, string? category, string? filter, int? id, string param, string value, bool dryRun, TextWriter output)
    {
        if (string.IsNullOrEmpty(param))
        {
            await output.WriteLineAsync("Error: --param is required.");
            return;
        }

        if (category == null && !id.HasValue)
        {
            await output.WriteLineAsync("Error: provide a category or --id to target elements.");
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
            await output.WriteLineAsync($"Error: {result.Error}");
            return;
        }

        var data = result.Data!;

        if (dryRun)
        {
            await output.WriteLineAsync($"Dry run: {data.Affected} element(s) would be modified.");
            foreach (var item in data.Preview)
                await output.WriteLineAsync($"  [{item.Id}] {item.Name}: \"{item.OldValue}\" -> \"{item.NewValue}\"");
            return;
        }

        await output.WriteLineAsync($"Modified {data.Affected} element(s).");
    }
}
