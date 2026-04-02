using System.CommandLine;
using RevitCli.Client;
using RevitCli.Commands;

var client = new RevitClient();
var rootCommand = new RootCommand("RevitCli - Command-line interface for Autodesk Revit");
rootCommand.AddCommand(StatusCommand.Create(client));
rootCommand.AddCommand(QueryCommand.Create(client));
rootCommand.AddCommand(ExportCommand.Create(client));
rootCommand.AddCommand(SetCommand.Create(client));
return await rootCommand.InvokeAsync(args);
