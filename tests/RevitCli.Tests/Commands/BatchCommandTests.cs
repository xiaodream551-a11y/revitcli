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

public class BatchCommandTests
{
    [Fact]
    public async Task Execute_ValidBatchFile_RunsAllCommands()
    {
        var statusResponse = ApiResponse<StatusInfo>.Ok(new StatusInfo { RevitVersion = "2025" });
        var handler = new FakeHttpHandler(JsonSerializer.Serialize(statusResponse));
        var client = new RevitClient(new HttpClient(handler) { BaseAddress = new System.Uri("http://localhost:17839") });
        var config = new CliConfig();

        var tmpFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tmpFile, """[{"command": "status"}]""");
            var writer = new StringWriter();

            var exitCode = await BatchCommand.ExecuteAsync(client, config, tmpFile, writer);

            Assert.Equal(0, exitCode);
            Assert.Contains("2025", writer.ToString());
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }

    [Fact]
    public async Task Execute_FileNotFound_ReturnsError()
    {
        var handler = new FakeHttpHandler("{}");
        var client = new RevitClient(new HttpClient(handler) { BaseAddress = new System.Uri("http://localhost:17839") });
        var config = new CliConfig();
        var writer = new StringWriter();

        var exitCode = await BatchCommand.ExecuteAsync(client, config, "/nonexistent.json", writer);

        Assert.Equal(1, exitCode);
        Assert.Contains("not found", writer.ToString().ToLower());
    }

    [Fact]
    public async Task Execute_InvalidJson_ReturnsError()
    {
        var handler = new FakeHttpHandler("{}");
        var client = new RevitClient(new HttpClient(handler) { BaseAddress = new System.Uri("http://localhost:17839") });
        var config = new CliConfig();

        var tmpFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tmpFile, "not json");
            var writer = new StringWriter();

            var exitCode = await BatchCommand.ExecuteAsync(client, config, tmpFile, writer);

            Assert.Equal(1, exitCode);
            Assert.Contains("invalid json", writer.ToString().ToLower());
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }

    [Fact]
    public async Task Execute_UnknownCommand_ReturnsError()
    {
        var handler = new FakeHttpHandler("{}");
        var client = new RevitClient(new HttpClient(handler) { BaseAddress = new System.Uri("http://localhost:17839") });
        var config = new CliConfig();

        var tmpFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tmpFile, """[{"command": "nonexistent"}]""");
            var writer = new StringWriter();

            var exitCode = await BatchCommand.ExecuteAsync(client, config, tmpFile, writer);

            Assert.Equal(1, exitCode);
            Assert.Contains("unknown command", writer.ToString().ToLower());
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }
}
