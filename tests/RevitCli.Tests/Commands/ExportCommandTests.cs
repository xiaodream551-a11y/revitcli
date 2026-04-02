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

public class ExportCommandTests
{
    [Fact]
    public async Task Execute_ValidRequest_PrintsTaskId()
    {
        var progress = new ExportProgress { TaskId = "task-001", Status = "completed", Progress = 100 };
        var response = ApiResponse<ExportProgress>.Ok(progress);
        var handler = new FakeHttpHandler(JsonSerializer.Serialize(response));
        var client = new RevitClient(new HttpClient(handler) { BaseAddress = new System.Uri("http://localhost:17839") });
        var writer = new StringWriter();

        await ExportCommand.ExecuteAsync(client, "dwg", new[] { "A1*" }, "./exports", writer);

        var output = writer.ToString();
        Assert.Contains("task-001", output);
    }

    [Fact]
    public async Task Execute_ServerDown_PrintsError()
    {
        var handler = new FakeHttpHandler(throwException: true);
        var client = new RevitClient(new HttpClient(handler) { BaseAddress = new System.Uri("http://localhost:17839") });
        var writer = new StringWriter();

        await ExportCommand.ExecuteAsync(client, "dwg", new[] { "all" }, "./exports", writer);

        Assert.Contains("not running", writer.ToString().ToLower());
    }

    [Fact]
    public async Task Execute_MissingFormat_PrintsError()
    {
        var handler = new FakeHttpHandler("{}");
        var client = new RevitClient(new HttpClient(handler) { BaseAddress = new System.Uri("http://localhost:17839") });
        var writer = new StringWriter();

        await ExportCommand.ExecuteAsync(client, null!, new[] { "all" }, "./exports", writer);

        Assert.Contains("--format", writer.ToString().ToLower());
    }
}
