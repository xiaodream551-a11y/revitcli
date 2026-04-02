using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Actions;
using EmbedIO.WebApi;
using RevitCli.Addin.Handlers;
using RevitCli.Shared;

namespace RevitCli.Addin.Server;

public class ApiServer : IDisposable
{
    private WebServer? _server;
    private CancellationTokenSource? _cts;
    private readonly int _port;
    private readonly Func<Action<Action<object?>>, Task<object?>> _revitInvoke;

    private static readonly string ServerInfoPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".revitcli", "server.json");

    public ApiServer(int port, Func<Action<Action<object?>>, Task<object?>> revitInvoke)
    {
        _port = port;
        _revitInvoke = revitInvoke;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();

        // Try ports starting from _port, fallback to next 10
        int actualPort = _port;
        for (int i = 0; i <= 10; i++)
        {
            try
            {
                actualPort = _port + i;
                _server = CreateServer(actualPort);
                _server.RunAsync(_cts.Token);
                WriteServerInfo(actualPort);
                return;
            }
            catch (System.Net.HttpListenerException)
            {
                _server?.Dispose();
                _server = null;
                if (i == 10) throw;
            }
        }
    }

    private WebServer CreateServer(int port)
    {
        return new WebServer(o => o
                .WithUrlPrefix($"http://localhost:{port}/")
                .WithMode(HttpListenerMode.EmbedIO))
            .WithWebApi("/api", m => m
                .WithController(() => new StatusController(_revitInvoke))
                .WithController(() => new ElementsController(_revitInvoke))
                .WithController(() => new ExportController(_revitInvoke))
                .WithController(() => new SetController(_revitInvoke)))
            .WithModule(new ActionModule("/", HttpVerbs.Any, ctx =>
            {
                ctx.Response.StatusCode = 404;
                return Task.CompletedTask;
            }));
    }

    private void WriteServerInfo(int port)
    {
        var info = new ServerInfo
        {
            Port = port,
            Pid = Process.GetCurrentProcess().Id,
            RevitVersion = "",  // Will be set by RevitCliApp when Revit version is known
            StartedAt = DateTime.UtcNow.ToString("o")
        };
        var dir = Path.GetDirectoryName(ServerInfoPath)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(ServerInfoPath,
            JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true }));
    }

    private void RemoveServerInfo()
    {
        try { File.Delete(ServerInfoPath); } catch { }
    }

    public void Stop()
    {
        RemoveServerInfo();
        _cts?.Cancel();
        _server?.Dispose();
        _server = null;
        _cts = null;
    }

    public void Dispose()
    {
        Stop();
    }
}
