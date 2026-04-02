using System;
using RevitCli.Addin.Bridge;
using RevitCli.Addin.Server;

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
    private RevitBridge? _bridge;
    private const int DefaultPort = 17839;

    public void OnStartup()
    {
        _bridge = new RevitBridge();

        // Adapt RevitBridge.InvokeOnMainThreadAsync(Action<Action<object?>>) to
        // the Func<Action<object?>, Task<object?>> delegate ApiServer expects.
        // The callback passed by ApiServer is the result-setter; we wrap it so
        // the bridge's outer action receives it properly.
        _server = new ApiServer(DefaultPort, callback => _bridge.InvokeOnMainThreadAsync(setResult => callback(setResult)));
        _server.Start();

        Console.WriteLine($"[RevitCli] Server started on port {DefaultPort}");
    }

    public void OnShutdown()
    {
        _server?.Stop();
        _server = null;
        _bridge = null;

        Console.WriteLine("[RevitCli] Server stopped");
    }
}
