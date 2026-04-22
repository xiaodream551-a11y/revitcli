using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RevitCli.Client;
using RevitCli.Shared;
using Xunit;

namespace RevitCli.Tests.Client;

public class RevitClientTests
{
    [Fact]
    public async Task GetStatusAsync_ReturnsStatusInfo()
    {
        var statusInfo = new StatusInfo
        {
            RevitVersion = "2025",
            DocumentName = "Project1.rvt"
        };
        var response = ApiResponse<StatusInfo>.Ok(statusInfo);
        var handler = new FakeHttpHandler(JsonSerializer.Serialize(response));
        var client = new RevitClient(new HttpClient(handler) { BaseAddress = new System.Uri("http://localhost:17839") });

        var result = await client.GetStatusAsync();

        Assert.True(result.Success);
        Assert.Equal("2025", result.Data!.RevitVersion);
        Assert.Equal("Project1.rvt", result.Data.DocumentName);
    }

    [Fact]
    public async Task GetStatusAsync_ServerDown_ReturnsFail()
    {
        var handler = new FakeHttpHandler(throwException: true);
        var client = new RevitClient(new HttpClient(handler) { BaseAddress = new System.Uri("http://localhost:17839") });

        var result = await client.GetStatusAsync();

        Assert.False(result.Success);
        Assert.Contains("not running", result.Error!.ToLower());
    }

    [Fact]
    public async Task QueryElementsAsync_WithCategory_SendsCorrectRequest()
    {
        var elements = new[] { new ElementInfo { Id = 1, Name = "Wall 1", Category = "Walls" } };
        var response = ApiResponse<ElementInfo[]>.Ok(elements);
        var handler = new FakeHttpHandler(JsonSerializer.Serialize(response));
        var client = new RevitClient(new HttpClient(handler) { BaseAddress = new System.Uri("http://localhost:17839") });

        var result = await client.QueryElementsAsync("walls", null);

        Assert.True(result.Success);
        var data = result.Data!;
        Assert.Single(data);
        Assert.Equal("Wall 1", data[0].Name);
        Assert.Contains("category=walls", handler.LastRequestUri!);
    }

    [Fact]
    public async Task ListSchedulesAsync_ReturnsSchedules()
    {
        var schedules = new[] { new ScheduleInfo { Id = 1, Name = "Door Schedule", Category = "Doors" } };
        var response = ApiResponse<ScheduleInfo[]>.Ok(schedules);
        var handler = new FakeHttpHandler(JsonSerializer.Serialize(response));
        var client = new RevitClient(new HttpClient(handler) { BaseAddress = new System.Uri("http://localhost:17839") });

        var result = await client.ListSchedulesAsync();

        Assert.True(result.Success);
        Assert.Single(result.Data!);
        Assert.Equal("Door Schedule", result.Data![0].Name);
    }

    [Fact]
    public async Task ExportScheduleAsync_ReturnsData()
    {
        var data = new ScheduleData
        {
            Columns = new List<string> { "Name", "Level" },
            Rows = new List<Dictionary<string, string>>
            {
                new() { ["Name"] = "Door-01", ["Level"] = "Level 1" }
            },
            TotalRows = 1
        };
        var response = ApiResponse<ScheduleData>.Ok(data);
        var handler = new FakeHttpHandler(JsonSerializer.Serialize(response));
        var client = new RevitClient(new HttpClient(handler) { BaseAddress = new System.Uri("http://localhost:17839") });

        var result = await client.ExportScheduleAsync(new ScheduleExportRequest { Category = "Doors" });

        Assert.True(result.Success);
        Assert.Equal(2, result.Data!.Columns.Count);
        Assert.Single(result.Data.Rows);
    }

    [Fact]
    public async Task CreateScheduleAsync_ReturnsResult()
    {
        var createResult = new ScheduleCreateResult { ViewId = 100, Name = "Test", FieldCount = 3, RowCount = 5 };
        var response = ApiResponse<ScheduleCreateResult>.Ok(createResult);
        var handler = new FakeHttpHandler(JsonSerializer.Serialize(response));
        var client = new RevitClient(new HttpClient(handler) { BaseAddress = new System.Uri("http://localhost:17839") });

        var result = await client.CreateScheduleAsync(new ScheduleCreateRequest { Category = "Doors", Name = "Test" });

        Assert.True(result.Success);
        Assert.Equal(100, result.Data!.ViewId);
    }
}

public class FakeHttpHandler : HttpMessageHandler
{
    private readonly string? _response;
    private readonly bool _throwException;
    public string? LastRequestUri { get; private set; }
    public int CallCount { get; private set; }
    public string? LastRequestBody { get; private set; }

    public FakeHttpHandler(string? response = null, bool throwException = false)
    {
        _response = response;
        _throwException = throwException;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CallCount++;
        LastRequestUri = request.RequestUri?.ToString();
        if (request.Content != null)
            LastRequestBody = await request.Content.ReadAsStringAsync();

        if (_throwException)
            throw new HttpRequestException("Connection refused");

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(_response ?? "{}")
        };
    }
}
