using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RevitCli.Addin.Core;
using RevitCli.Addin.Server;
using RevitCli.Addin.Services;
using RevitCli.Shared;
using Xunit;

namespace RevitCli.Addin.Tests.Integration;

public sealed class ApiServerReloadTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _serverInfoPath;
    private readonly RecordingReloadService _reloadService = new();
    private readonly ApiServer _server;

    public ApiServerReloadTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"revitcli_reload_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _serverInfoPath = Path.Combine(_tempDir, "server.json");
        _server = new ApiServer(
            GetAvailablePort(),
            new PlaceholderRevitOperations(),
            CoreControllerRegistry.ControllerTypes,
            serverInfoPath: _serverInfoPath,
            reloadService: _reloadService,
            addinDirectory: _tempDir);
        _server.Start();
    }

    [Fact]
    public async Task Reload_ReturnsAcceptedAndInvokesReloadService()
    {
        var info = JsonSerializer.Deserialize<ServerInfo>(File.ReadAllText(_serverInfoPath));
        Assert.NotNull(info);

        using var client = new HttpClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"http://127.0.0.1:{info!.Port}/api/reload");
        request.Headers.Add("X-RevitCli-Token", info.Token);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(1, _reloadService.RequestCount);

        var body = await response.Content.ReadAsStringAsync();
        var payload = JsonSerializer.Deserialize<ApiResponse<ReloadResponse>>(body);
        Assert.NotNull(payload);
        Assert.True(payload!.Success);
        Assert.Equal("accepted", payload.Data?.Status);
    }

    [Theory]
    [InlineData("already-running", HttpStatusCode.Conflict)]
    [InlineData("shutting-down", HttpStatusCode.ServiceUnavailable)]
    [InlineData("unsupported", HttpStatusCode.MethodNotAllowed)]
    public async Task Reload_WhenReloadServiceDoesNotAccept_ReturnsNonSuccessStatus(
        string status,
        HttpStatusCode expectedStatusCode)
    {
        _reloadService.NextStatus = status;
        var info = JsonSerializer.Deserialize<ServerInfo>(File.ReadAllText(_serverInfoPath));
        Assert.NotNull(info);

        using var client = new HttpClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"http://127.0.0.1:{info!.Port}/api/reload");
        request.Headers.Add("X-RevitCli-Token", info.Token);

        using var response = await client.SendAsync(request);

        Assert.Equal(expectedStatusCode, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var payload = JsonSerializer.Deserialize<ApiResponse<ReloadResponse>>(body);
        Assert.NotNull(payload);
        Assert.False(payload!.Success);
        Assert.Equal(status, payload.Data?.Status);
    }

    public void Dispose()
    {
        _server.Dispose();
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

    private sealed class RecordingReloadService : IReloadService
    {
        private int _requestCount;

        public int RequestCount => _requestCount;
        public string NextStatus { get; set; } = "accepted";

        public Task<ReloadResponse> RequestReloadAsync()
        {
            Interlocked.Increment(ref _requestCount);
            return Task.FromResult(new ReloadResponse
            {
                Status = NextStatus,
                Message = "test"
            });
        }
    }
}
