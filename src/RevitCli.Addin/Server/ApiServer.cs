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
    private readonly IRevitOperations _operations;
    private readonly string _revitVersion;
    private string _token = "";

    private static readonly string ServerInfoPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".revitcli", "server.json");

    public ApiServer(int port, IRevitOperations operations, string revitVersion = "")
    {
        _port = port;
        _operations = operations;
        _revitVersion = revitVersion;
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _token = Guid.NewGuid().ToString("N");

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
            catch (Exception)
            {
                _server?.Dispose();
                _server = null;
                if (i == 10) throw;
            }
        }
    }

    private WebServer CreateServer(int port)
    {
        var token = _token;
        return new WebServer(o => o
                .WithUrlPrefix($"http://localhost:{port}/")
                .WithMode(HttpListenerMode.EmbedIO))
            .WithModule(new TokenAuthModule(token))
            .WithWebApi("/api", m => m
                .WithController(() => new StatusController(_operations))
                .WithController(() => new ElementsController(_operations))
                .WithController(() => new ExportController(_operations))
                .WithController(() => new SetController(_operations))
                .WithController(() => new AuditController(_operations))
                .WithController(() => new ScheduleController(_operations)))
            .WithModule(new ActionModule("/", HttpVerbs.Any, ctx =>
            {
                ctx.Response.StatusCode = 404;
                return Task.CompletedTask;
            }));
    }

    private sealed class TokenAuthModule : EmbedIO.WebModuleBase
    {
        private readonly string _token;

        public TokenAuthModule(string token) : base("/")
        {
            _token = token;
        }

        public override bool IsFinalHandler => false;

        protected override Task OnRequestAsync(IHttpContext context)
        {
            var provided = context.Request.Headers["X-RevitCli-Token"];
            if (provided != _token)
                throw HttpException.Unauthorized();

            return Task.CompletedTask;
        }
    }

    private void WriteServerInfo(int port)
    {
        var info = new ServerInfo
        {
            Port = port,
            Pid = Process.GetCurrentProcess().Id,
            RevitVersion = _revitVersion,
            StartedAt = DateTime.UtcNow.ToString("o"),
            Token = _token
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
