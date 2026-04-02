using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using RevitCli.Addin.Bridge;
using RevitCli.Addin.Server;
using RevitCli.Client;
using RevitCli.Shared;
using Xunit;

namespace RevitCli.Addin.Tests.Integration;

public class EndToEndTests : IDisposable
{
    private readonly ApiServer _server;
    private readonly RevitClient _client;
    private readonly int _port;

    public EndToEndTests()
    {
        _port = GetAvailablePort();
        var bridge = new RevitBridge();
        _server = new ApiServer(_port, bridge.InvokeOnMainThreadAsync);
        _server.Start();
        _client = new RevitClient($"http://localhost:{_port}");
    }

    public void Dispose()
    {
        _server.Dispose();
    }

    private static int GetAvailablePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public async Task Status_ReturnsPlaceholderData()
    {
        var result = await _client.GetStatusAsync();

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal("2025", result.Data.RevitVersion);
    }

    [Fact]
    public async Task QueryElements_ReturnsEmptyArray()
    {
        var result = await _client.QueryElementsAsync("walls", null);

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Empty(result.Data);
    }

    [Fact]
    public async Task QueryElementById_ReturnsPlaceholder()
    {
        var result = await _client.QueryElementByIdAsync(42);

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(42, result.Data.Id);
    }

    [Fact]
    public async Task Export_ReturnsCompletedProgress()
    {
        var request = new ExportRequest
        {
            Format = "dwg",
            Sheets = new() { "A1" },
            OutputDir = "/tmp"
        };

        var result = await _client.ExportAsync(request);

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal("completed", result.Data.Status);
        Assert.Equal(100, result.Data.Progress);
    }

    [Fact]
    public async Task SetParameter_ReturnsZeroAffected()
    {
        var request = new SetRequest
        {
            Category = "doors",
            Param = "Fire Rating",
            Value = "60min",
            DryRun = true
        };

        var result = await _client.SetParameterAsync(request);

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(0, result.Data.Affected);
    }

    [Fact]
    public async Task Audit_ReturnsPlaceholderResult()
    {
        var request = new AuditRequest { Rules = new() { "naming" } };

        var result = await _client.AuditAsync(request);

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(5, result.Data.Passed);
    }
}
