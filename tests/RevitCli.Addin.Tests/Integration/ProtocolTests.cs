using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;
using RevitCli.Addin.Server;
using RevitCli.Addin.Services;
using RevitCli.Client;
using RevitCli.Shared;
using Xunit;

namespace RevitCli.Addin.Tests.Integration;

/// <summary>
/// Verifies the HTTP protocol layer: request routing, JSON serialization/deserialization,
/// and server lifecycle. Uses PlaceholderRevitOperations — does NOT test real Revit API.
/// These become true integration tests when PlaceholderRevitOperations is swapped
/// for RealRevitOperations on Windows + Revit.
/// </summary>
public class ProtocolTests : IDisposable
{
    private readonly ApiServer _server;
    private readonly RevitClient _client;
    private readonly int _port;

    public ProtocolTests()
    {
        _port = GetAvailablePort();
        var operations = new PlaceholderRevitOperations();
        _server = new ApiServer(_port, operations);
        _server.Start();

        var serverInfoPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".revitcli", "server.json");
        var token = "";
        if (File.Exists(serverInfoPath))
        {
            var info = JsonSerializer.Deserialize<ServerInfo>(File.ReadAllText(serverInfoPath));
            token = info?.Token ?? "";
        }
        _client = new RevitClient($"http://localhost:{_port}", token);
    }

    public void Dispose()
    {
        _client.Dispose();
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

    [Fact]
    public async Task CaptureSnapshot_ReturnsPlaceholderSnapshot()
    {
        var result = await _client.CaptureSnapshotAsync(new SnapshotRequest());

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(1, result.Data.SchemaVersion);
        Assert.Equal("2025", result.Data.Revit.Version);
        Assert.True(result.Data.Categories.ContainsKey("walls"));
        Assert.Single(result.Data.Categories["walls"]);
        Assert.Equal(1001, result.Data.Categories["walls"][0].Id);
        Assert.Single(result.Data.Sheets);
        Assert.Single(result.Data.Schedules);
        Assert.Equal(1, result.Data.Summary.ElementCounts["walls"]);
    }

    [Fact]
    public async Task CaptureSnapshot_WithIncludeCategoriesFilter_RequestReaches()
    {
        var result = await _client.CaptureSnapshotAsync(new SnapshotRequest
        {
            IncludeCategories = new List<string> { "walls" },
            IncludeSheets = false
        });

        // Placeholder currently ignores filter; assert response is still well-formed.
        Assert.True(result.Success);
        Assert.NotNull(result.Data);
    }
}
