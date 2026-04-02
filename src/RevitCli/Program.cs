using System.CommandLine;
using RevitCli.Client;
using RevitCli.Commands;
using RevitCli.Config;

var config = CliConfig.Load();
var serverUrl = RevitClient.DiscoverServerUrl(config.ServerUrl);
var client = new RevitClient(serverUrl);
var rootCommand = new RootCommand("RevitCli - Command-line interface for Autodesk Revit");
rootCommand.AddCommand(StatusCommand.Create(client));
rootCommand.AddCommand(QueryCommand.Create(client));
rootCommand.AddCommand(ExportCommand.Create(client));
rootCommand.AddCommand(SetCommand.Create(client));
rootCommand.AddCommand(ConfigCommand.Create());
return await rootCommand.InvokeAsync(args);
