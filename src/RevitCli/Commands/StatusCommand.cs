using System.CommandLine;
using System.IO;
using System.Threading.Tasks;
using RevitCli.Client;

namespace RevitCli.Commands;

public static class StatusCommand
{
    public static Command Create(RevitClient client)
    {
        var command = new Command("status", "Check if Revit plugin is online");
        command.SetHandler(async () => await ExecuteAsync(client, Console.Out));
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
