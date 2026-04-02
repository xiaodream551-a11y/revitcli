using System.CommandLine;
using RevitCli.Client;
using RevitCli.Commands;

var client = new RevitClient();
var rootCommand = new RootCommand("RevitCli - Command-line interface for Autodesk Revit");
rootCommand.AddCommand(StatusCommand.Create(client));
await rootCommand.InvokeAsync(args);
