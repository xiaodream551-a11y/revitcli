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

public class StatusCommandTests
{
    [Fact]
    public async Task Execute_ServerOnline_PrintsStatus()
    {
        var status = new StatusInfo { RevitVersion = "2025", DocumentName = "Test.rvt" };
        var response = ApiResponse<StatusInfo>.Ok(status);
        var handler = new FakeHttpHandler(JsonSerializer.Serialize(response));
        var client = new RevitClient(new HttpClient(handler) { BaseAddress = new System.Uri("http://localhost:17839") });
        var writer = new StringWriter();

        await StatusCommand.ExecuteAsync(client, writer);

        var output = writer.ToString();
        Assert.Contains("2025", output);
        Assert.Contains("Test.rvt", output);
    }

    [Fact]
    public async Task Execute_ServerDown_PrintsError()
    {
        var handler = new FakeHttpHandler(throwException: true);
        var client = new RevitClient(new HttpClient(handler) { BaseAddress = new System.Uri("http://localhost:17839") });
        var writer = new StringWriter();

        await StatusCommand.ExecuteAsync(client, writer);

        var output = writer.ToString();
        Assert.Contains("not running", output.ToLower());
    }
}
