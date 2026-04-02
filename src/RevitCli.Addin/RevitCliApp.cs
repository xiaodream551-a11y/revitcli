using System;
using RevitCli.Addin.Server;
using RevitCli.Addin.Services;

namespace RevitCli.Addin;

/// <summary>
/// Revit add-in entry point.
///
/// In the real Revit environment, this implements IExternalApplication
/// with OnStartup/OnShutdown methods. The Revit API types are omitted
/// here to allow cross-platform development; they will be added when
/// targeting net48 with Revit API references on Windows.
/// </summary>
public class RevitCliApp
{
    private ApiServer? _server;
    private const int DefaultPort = 17839;

    public void OnStartup()
    {
        var operations = new PlaceholderRevitOperations();

        _server = new ApiServer(DefaultPort, operations);
        _server.Start();

        Console.WriteLine($"[RevitCli] Server started on port {DefaultPort}");
    }

    public void OnShutdown()
    {
        _server?.Stop();
        _server = null;

        Console.WriteLine("[RevitCli] Server stopped");
    }
}
