using System.CommandLine;
using System.Reflection;
using RevitCli.Client;
using RevitCli.Commands;
using RevitCli.Config;

// --version: handled early before any other setup
if (args.Length == 1 && args[0] is "--version" or "-v")
{
    var version = typeof(Program).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
        ?.InformationalVersion ?? "0.1.0";
    Console.WriteLine($"revitcli {version}");
    return 0;
}

// -i: shortcut for interactive mode
if (args.Length == 1 && args[0] == "-i")
{
    args = new[] { "interactive" };
}

// --verbose: enable HTTP request logging
if (args.Contains("--verbose"))
{
    RevitClient.Verbose = true;
    args = args.Where(a => a != "--verbose").ToArray();
}

var config = CliConfig.Load();
var serverUrl = RevitClient.DiscoverServerUrl(config.ServerUrl);
var client = new RevitClient(serverUrl);
var rootCommand = new RootCommand("RevitCli - Command-line interface for Autodesk Revit");
rootCommand.AddCommand(StatusCommand.Create(client));
rootCommand.AddCommand(QueryCommand.Create(client, config));
rootCommand.AddCommand(ExportCommand.Create(client, config));
rootCommand.AddCommand(SetCommand.Create(client));
rootCommand.AddCommand(ConfigCommand.Create());
rootCommand.AddCommand(AuditCommand.Create(client));
rootCommand.AddCommand(CompletionsCommand.Create());
rootCommand.AddCommand(InteractiveCommand.Create(client, config));
return await rootCommand.InvokeAsync(args);
