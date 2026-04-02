using System;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Actions;
using EmbedIO.WebApi;
using RevitCli.Addin.Handlers;

namespace RevitCli.Addin.Server;

public class ApiServer : IDisposable
{
    private WebServer? _server;
    private CancellationTokenSource? _cts;
    private readonly int _port;
    private readonly Func<Action<Action<object?>>, Task<object?>> _revitInvoke;

    public ApiServer(int port, Func<Action<Action<object?>>, Task<object?>> revitInvoke)
    {
        _port = port;
        _revitInvoke = revitInvoke;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _server = new WebServer(o => o
                .WithUrlPrefix($"http://localhost:{_port}/")
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

        _server.RunAsync(_cts.Token);
    }

    public void Stop()
    {
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
