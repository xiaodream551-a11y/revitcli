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

        var exitCode = await SetCommand.ExecuteAsync(client, "doors", null, null, "Fire Rating", "60min", true, false, writer);

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

        var exitCode = await SetCommand.ExecuteAsync(client, "doors", null, null, null!, "60min", false, false, writer);

        Assert.Contains("--param", writer.ToString().ToLower());
        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task Execute_NoTarget_PrintsError()
    {
        var handler = new FakeHttpHandler("{}");
        var client = new RevitClient(new HttpClient(handler) { BaseAddress = new System.Uri("http://localhost:17839") });
        var writer = new StringWriter();

        var exitCode = await SetCommand.ExecuteAsync(client, null, null, null, "Mark", "W-01", false, false, writer);

        Assert.Contains("category", writer.ToString().ToLower());
        Assert.Equal(1, exitCode);
    }
}
