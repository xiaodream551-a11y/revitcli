using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using RevitCli.Client;
using RevitCli.Commands;
using RevitCli.Config;
using RevitCli.Shared;
using RevitCli.Tests.Client;
using Xunit;

namespace RevitCli.Tests.Commands;

public class DoctorCommandTests
{
    [Fact]
    public async Task Execute_ServerDown_PrintsFail()
    {
        var handler = new FakeHttpHandler(throwException: true);
        var client = new RevitClient(new HttpClient(handler) { BaseAddress = new System.Uri("http://localhost:17839") });
        var config = new CliConfig();
        var writer = new StringWriter();

        await DoctorCommand.ExecuteAsync(client, config, writer);

        var output = writer.ToString();
        Assert.Contains("FAIL", output);
        Assert.Contains("Server URL", output);
    }

    [Fact]
    public async Task Execute_ServerUp_PrintsOk()
    {
        var status = new StatusInfo { RevitVersion = "2025", DocumentName = "Test.rvt" };
        var response = ApiResponse<StatusInfo>.Ok(status);
        var handler = new FakeHttpHandler(JsonSerializer.Serialize(response));
        var client = new RevitClient(new HttpClient(handler) { BaseAddress = new System.Uri("http://localhost:17839") });
        var config = new CliConfig();
        var writer = new StringWriter();

        await DoctorCommand.ExecuteAsync(client, config, writer);

        var output = writer.ToString();
        Assert.Contains("Connected to Revit 2025", output);
        Assert.Contains("Test.rvt", output);
    }
}
