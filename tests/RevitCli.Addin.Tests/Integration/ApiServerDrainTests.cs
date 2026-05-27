using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using RevitCli.Addin.Server;
using RevitCli.Addin.Services;
using RevitCli.Shared;
using Xunit;

namespace RevitCli.Addin.Tests.Integration;

public sealed class ApiServerDrainTests : IDisposable
{
    private readonly string _tempDir;

    public ApiServerDrainTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"revitcli_drain_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public async Task Stop_WaitsForInFlightRequestBeforeDisposingServer()
    {
        SlowController.Reset();
        var serverInfoPath = Path.Combine(_tempDir, "server.json");
        using var server = new ApiServer(
            GetAvailablePort(),
            new PlaceholderRevitOperations(),
            new[] { typeof(SlowController) },
            serverInfoPath: serverInfoPath);
        server.Start();

        var info = JsonSerializer.Deserialize<ServerInfo>(File.ReadAllText(serverInfoPath));
        Assert.NotNull(info);

        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{info!.Port}/api/slow");
        request.Headers.Add("X-RevitCli-Token", info.Token);
        var requestTask = client.SendAsync(request);
        await SlowController.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));

        var stopTask = Task.Run(server.Stop);
        await Task.Delay(100);
        Assert.False(stopTask.IsCompleted);
        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.False(stopTask.IsCompleted);

        SlowController.Release.SetResult();
        using var response = await requestTask;
        await stopTask.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await SlowController.Completed.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static int GetAvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public sealed class SlowController : WebApiController
    {
        public static TaskCompletionSource Started { get; private set; } = CreateCompletionSource();
        public static TaskCompletionSource Release { get; private set; } = CreateCompletionSource();
        public static TaskCompletionSource Completed { get; private set; } = CreateCompletionSource();

        public SlowController(IRevitOperations operations)
        {
        }

        public static void Reset()
        {
            Started = CreateCompletionSource();
            Release = CreateCompletionSource();
            Completed = CreateCompletionSource();
        }

        [Route(HttpVerbs.Get, "/slow")]
        public async Task GetSlow()
        {
            Started.TrySetResult();
            await Release.Task;
            HttpContext.Response.StatusCode = (int)HttpStatusCode.OK;
            Completed.TrySetResult();
        }

        private static TaskCompletionSource CreateCompletionSource()
        {
            return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
