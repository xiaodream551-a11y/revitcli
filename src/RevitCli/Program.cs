using System.CommandLine;

var rootCommand = new RootCommand("RevitCli - Command-line interface for Autodesk Revit");
await rootCommand.InvokeAsync(args);
