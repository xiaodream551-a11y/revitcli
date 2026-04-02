using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using RevitCli.Client;
using RevitCli.Commands;
using RevitCli.Shared;
using RevitCli.Tests.Client;
using Xunit;

namespace RevitCli.Tests.Commands;

public class QueryCommandTests
{
    private static readonly ElementInfo[] TestElements =
    {
        new()
        {
            Id = 100, Name = "Wall 1", Category = "Walls", TypeName = "Generic - 200mm",
            Parameters = new Dictionary<string, string> { { "Height", "3000" } }
        }
    };

    [Fact]
    public async Task Execute_WithCategory_PrintsElements()
    {
        var response = ApiResponse<ElementInfo[]>.Ok(TestElements);
        var handler = new FakeHttpHandler(JsonSerializer.Serialize(response));
        var client = new RevitClient(new HttpClient(handler) { BaseAddress = new System.Uri("http://localhost:17839") });
        var writer = new StringWriter();

        await QueryCommand.ExecuteAsync(client, "walls", null, null, "table", writer);

        var output = writer.ToString();
        Assert.Contains("Wall 1", output);
    }

    [Fact]
    public async Task Execute_WithId_PrintsSingleElement()
    {
        var element = new ElementInfo
        {
            Id = 100, Name = "Wall 1", Category = "Walls", TypeName = "Generic - 200mm",
            Parameters = new Dictionary<string, string> { { "Height", "3000" } }
        };
        var response = ApiResponse<ElementInfo>.Ok(element);
        var handler = new FakeHttpHandler(JsonSerializer.Serialize(response));
        var client = new RevitClient(new HttpClient(handler) { BaseAddress = new System.Uri("http://localhost:17839") });
        var writer = new StringWriter();

        await QueryCommand.ExecuteAsync(client, null, null, 100, "json", writer);

        var output = writer.ToString();
        Assert.Contains("\"id\": 100", output);
    }

    [Fact]
    public async Task Execute_NoResults_PrintsNoElementsMessage()
    {
        var response = ApiResponse<ElementInfo[]>.Ok(System.Array.Empty<ElementInfo>());
        var handler = new FakeHttpHandler(JsonSerializer.Serialize(response));
        var client = new RevitClient(new HttpClient(handler) { BaseAddress = new System.Uri("http://localhost:17839") });
        var writer = new StringWriter();

        await QueryCommand.ExecuteAsync(client, "walls", null, null, "table", writer);

        Assert.Contains("No elements matched", writer.ToString());
    }
}
