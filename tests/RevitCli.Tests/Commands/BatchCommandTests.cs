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

[Collection("Sequential")]
public class BatchCommandTests : IDisposable
{
    private readonly int _savedExitCode;

    public BatchCommandTests()
    {
        _savedExitCode = Environment.ExitCode;
        Environment.ExitCode = 0;
    }

    public void Dispose()
    {
        Environment.ExitCode = _savedExitCode;
    }

    [Fact]
    public async Task Execute_ValidBatchFile_RunsCommand()
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
            Assert.Equal(1, handler.CallCount); // status command made exactly one HTTP call
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

            // Parser won't find "nonexistent" subcommand → non-zero exit
            Assert.Equal(1, exitCode);
            Assert.Equal(0, handler.CallCount); // no HTTP call made
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }

    [Fact]
    public async Task Execute_SetWithInvalidId_ParserRejects()
    {
        var handler = new FakeHttpHandler("{}");
        var client = new RevitClient(new HttpClient(handler) { BaseAddress = new System.Uri("http://localhost:17839") });
        var config = new CliConfig();

        var tmpFile = Path.GetTempFileName();
        try
        {
            // This is the exact bug Codex found: --id abc should be rejected by parser,
            // not silently turned into null (which would cause a different set target)
            await File.WriteAllTextAsync(tmpFile,
                """[{"command": "set", "args": ["doors", "--id", "abc", "--param", "Mark", "--value", "X"]}]""");
            var writer = new StringWriter();

            var exitCode = await BatchCommand.ExecuteAsync(client, config, tmpFile, writer);

            Assert.Equal(1, exitCode);
            Assert.Equal(0, handler.CallCount); // parser rejected → no HTTP call
        }
        finally
        {
            File.Delete(tmpFile);
        }
    }
}
