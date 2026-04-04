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

public class SetCommandTests
{
    [Fact]
    public async Task Execute_DryRun_PrintsPreview()
    {
        var setResult = new SetResult
        {
            Affected = 2,
            Preview = new List<SetPreviewItem>
            {
                new() { Id = 100, Name = "Door 1", OldValue = "30min", NewValue = "60min" },
                new() { Id = 200, Name = "Door 2", OldValue = "30min", NewValue = "60min" }
            }
        };
        var response = ApiResponse<SetResult>.Ok(setResult);
        var handler = new FakeHttpHandler(JsonSerializer.Serialize(response));
        var client = new RevitClient(new HttpClient(handler) { BaseAddress = new System.Uri("http://localhost:17839") });
        var writer = new StringWriter();

        var exitCode = await SetCommand.ExecuteAsync(client, "doors", null, null, "Fire Rating", "60min", true, false, null, writer);

        var output = writer.ToString();
        Assert.Contains("2 element(s)", output);
        Assert.Contains("Door 1", output);
        Assert.Contains("30min", output);
        Assert.Contains("60min", output);
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task Execute_MissingParam_PrintsError()
    {
        var handler = new FakeHttpHandler("{}");
        var client = new RevitClient(new HttpClient(handler) { BaseAddress = new System.Uri("http://localhost:17839") });
        var writer = new StringWriter();

        var exitCode = await SetCommand.ExecuteAsync(client, "doors", null, null, null!, "60min", false, false, null, writer);

        Assert.Contains("--param", writer.ToString().ToLower());
        Assert.Equal(1, exitCode);
    }

    [Fact]
    public void SetRequest_ElementIds_SerializesCorrectly()
    {
        var request = new SetRequest
        {
            Param = "Mark",
            Value = "TEST",
            ElementIds = new List<long> { 337596, 337601 }
        };

        var json = JsonSerializer.Serialize(request);
        Assert.Contains("\"elementIds\":[337596,337601]", json);

        var deserialized = JsonSerializer.Deserialize<SetRequest>(json);
        Assert.NotNull(deserialized);
        Assert.NotNull(deserialized!.ElementIds);
        Assert.Equal(2, deserialized.ElementIds!.Count);
        Assert.Equal(337596, deserialized.ElementIds[0]);
        Assert.Equal(337601, deserialized.ElementIds[1]);

        // Category and ElementId should be null
        Assert.Null(deserialized.Category);
        Assert.Null(deserialized.ElementId);
    }

    [Fact]
    public void QueryJsonOutput_ParseIds_ExtractsCorrectly()
    {
        // Simulates query --output json piped to set --ids-from
        var queryOutput = @"[
            {""id"": 337596, ""name"": ""Wall 1"", ""category"": ""Walls""},
            {""id"": 337601, ""name"": ""Wall 2"", ""category"": ""Walls""}
        ]";

        var elements = JsonSerializer.Deserialize<List<JsonElement>>(queryOutput);
        Assert.NotNull(elements);
        var ids = new List<long>();
        foreach (var elem in elements!)
        {
            if (elem.TryGetProperty("id", out var idProp))
                ids.Add(idProp.GetInt64());
        }
        Assert.Equal(2, ids.Count);
        Assert.Equal(337596, ids[0]);
        Assert.Equal(337601, ids[1]);
    }

    [Fact]
    public async Task Execute_NoTarget_PrintsError()
    {
        var handler = new FakeHttpHandler("{}");
        var client = new RevitClient(new HttpClient(handler) { BaseAddress = new System.Uri("http://localhost:17839") });
        var writer = new StringWriter();

        var exitCode = await SetCommand.ExecuteAsync(client, null, null, null, "Mark", "W-01", false, false, null, writer);

        Assert.Contains("category", writer.ToString().ToLower());
        Assert.Equal(1, exitCode);
    }
}
