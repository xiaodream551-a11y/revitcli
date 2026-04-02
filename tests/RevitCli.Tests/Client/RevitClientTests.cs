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
        Assert.Single(result.Data!);
        Assert.Equal("Wall 1", result.Data[0].Name);
        Assert.Contains("category=walls", handler.LastRequestUri!);
    }
}

public class FakeHttpHandler : HttpMessageHandler
{
    private readonly string? _response;
    private readonly bool _throwException;
    public string? LastRequestUri { get; private set; }

    public FakeHttpHandler(string? response = null, bool throwException = false)
    {
        _response = response;
        _throwException = throwException;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequestUri = request.RequestUri?.ToString();

        if (_throwException)
            throw new HttpRequestException("Connection refused");

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(_response ?? "{}")
        });
    }
}
