using System.CommandLine;
using System.Net.Http;
using System.Text.Json;
using RevitCli.Client;
using RevitCli.Commands;
using RevitCli.Config;
using RevitCli.Shared;
using RevitCli.Tests.Client;
using Xunit;

namespace RevitCli.Tests.Commands;

/// <summary>
/// Tests that go through Create().InvokeAsync() to verify System.CommandLine parser behavior:
/// required options, type binding, defaults, and that parser rejection prevents handler execution.
/// These run with stdout redirected (IsInteractive = false), so Create() delegates to ExecuteAsync().
/// </summary>
[Collection("Sequential")] // Environment.ExitCode is process-global
public class ParserLevelTests : IDisposable
{
    private readonly int _savedExitCode;

    public ParserLevelTests()
    {
        _savedExitCode = Environment.ExitCode;
        Environment.ExitCode = 0;
    }

    public void Dispose()
    {
        Environment.ExitCode = _savedExitCode;
    }

    private static (RevitClient client, FakeHttpHandler handler) CreateClientWithHandler(
        string? response = null, bool throwException = false)
    {
        var handler = new FakeHttpHandler(response, throwException);
        var client = new RevitClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:17839") });
        return (client, handler);
    }

    // ==========================================================
    // Parser rejection: required options missing → handler NOT called
    // ==========================================================

    [Fact]
    public async Task Export_MissingRequiredFormat_ParserRejects_NoHandlerCall()
    {
        var (client, handler) = CreateClientWithHandler();
        var command = ExportCommand.Create(client, new CliConfig());

        var exitCode = await command.InvokeAsync(Array.Empty<string>());

        Assert.NotEqual(0, exitCode); // parser itself returns non-zero
        Assert.Equal(0, handler.CallCount); // handler never executed → no HTTP call
    }

    [Fact]
    public async Task Set_MissingRequiredParamAndValue_ParserRejects_NoHandlerCall()
    {
        var (client, handler) = CreateClientWithHandler();
        var command = SetCommand.Create(client);

        var exitCode = await command.InvokeAsync(new[] { "doors" });

        Assert.NotEqual(0, exitCode);
        Assert.Equal(0, handler.CallCount);
    }

    // ==========================================================
    // Type binding errors → parser rejects
    // ==========================================================

    [Fact]
    public async Task Query_InvalidIdType_ParserRejects()
    {
        var (client, handler) = CreateClientWithHandler();
        var command = QueryCommand.Create(client, new CliConfig());

        var exitCode = await command.InvokeAsync(new[] { "--id", "not_a_number" });

        Assert.NotEqual(0, exitCode);
        Assert.Equal(0, handler.CallCount);
    }

    // ==========================================================
    // Default values correctly bound
    // ==========================================================

    [Fact]
    public async Task Query_DefaultOutput_UsesConfigValue()
    {
        var elements = new[] { new ElementInfo { Id = 1, Name = "Wall 1", Category = "Walls" } };
        var response = ApiResponse<ElementInfo[]>.Ok(elements);
        var (client, handler) = CreateClientWithHandler(JsonSerializer.Serialize(response));
        var config = new CliConfig { DefaultOutput = "json" };
        var command = QueryCommand.Create(client, config);

        // Capture stdout
        var originalOut = Console.Out;
        var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            var exitCode = await command.InvokeAsync(new[] { "walls" });
            var output = sw.ToString();

            Assert.Equal(0, Environment.ExitCode);
            Assert.Equal(1, handler.CallCount);
            // JSON output proves the default "json" from config was used
            Assert.Contains("\"id\":", output);
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public async Task Set_DryRunFlag_SentInRequest()
    {
        var result = new SetResult { Affected = 0 };
        var response = ApiResponse<SetResult>.Ok(result);
        var (client, handler) = CreateClientWithHandler(JsonSerializer.Serialize(response));
        var command = SetCommand.Create(client);

        var originalOut = Console.Out;
        var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            await command.InvokeAsync(new[] { "doors", "--param", "Mark", "--value", "D-01", "--dry-run" });
            var output = sw.ToString();

            Assert.Equal(0, Environment.ExitCode);
            Assert.Equal(1, handler.CallCount);
            // Dry-run output proves the flag was parsed and passed through
            Assert.Contains("dry run", output.ToLower());
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    // ==========================================================
    // Handler wiring: correct endpoints called
    // ==========================================================

    [Fact]
    public async Task Status_ViaParser_CallsStatusEndpoint()
    {
        var status = new StatusInfo { RevitVersion = "2025", DocumentName = "Test.rvt" };
        var response = ApiResponse<StatusInfo>.Ok(status);
        var (client, handler) = CreateClientWithHandler(JsonSerializer.Serialize(response));
        var command = StatusCommand.Create(client);

        await command.InvokeAsync(Array.Empty<string>());

        Assert.Equal(0, Environment.ExitCode);
        Assert.Equal(1, handler.CallCount);
        Assert.Contains("/api/status", handler.LastRequestUri!);
    }

    [Fact]
    public async Task Query_WithId_CallsElementByIdEndpoint()
    {
        var element = new ElementInfo { Id = 42, Name = "Element 42" };
        var response = ApiResponse<ElementInfo>.Ok(element);
        var (client, handler) = CreateClientWithHandler(JsonSerializer.Serialize(response));
        var command = QueryCommand.Create(client, new CliConfig());

        await command.InvokeAsync(new[] { "--id", "42" });

        Assert.Equal(0, Environment.ExitCode);
        Assert.Equal(1, handler.CallCount);
        Assert.Contains("/api/elements/42", handler.LastRequestUri!);
    }

    [Fact]
    public async Task Query_WithCategory_CallsElementsEndpoint()
    {
        var elements = Array.Empty<ElementInfo>();
        var response = ApiResponse<ElementInfo[]>.Ok(elements);
        var (client, handler) = CreateClientWithHandler(JsonSerializer.Serialize(response));
        var command = QueryCommand.Create(client, new CliConfig());

        await command.InvokeAsync(new[] { "walls" });

        Assert.Equal(0, Environment.ExitCode);
        Assert.Equal(1, handler.CallCount);
        Assert.Contains("category=walls", handler.LastRequestUri!);
    }

    [Fact]
    public async Task Export_WithFormat_CallsExportEndpoint()
    {
        var progress = new ExportProgress { TaskId = "t1", Status = "completed", Progress = 100 };
        var response = ApiResponse<ExportProgress>.Ok(progress);
        var (client, handler) = CreateClientWithHandler(JsonSerializer.Serialize(response));
        var command = ExportCommand.Create(client, new CliConfig());

        await command.InvokeAsync(new[] { "--format", "dwg" });

        Assert.Equal(0, Environment.ExitCode);
        Assert.Equal(1, handler.CallCount);
        Assert.Contains("/api/export", handler.LastRequestUri!);
    }

    // ==========================================================
    // Error propagation: server down → non-zero exit
    // ==========================================================

    [Fact]
    public async Task Status_ServerDown_SetsExitCode1()
    {
        var (client, handler) = CreateClientWithHandler(throwException: true);
        var command = StatusCommand.Create(client);

        await command.InvokeAsync(Array.Empty<string>());

        Assert.Equal(1, Environment.ExitCode);
        Assert.Equal(1, handler.CallCount); // handler DID execute, just failed
    }

    // ==========================================================
    // Missing positional args
    // ==========================================================

    [Fact]
    public async Task Completions_MissingShellArg_ParserRejects()
    {
        var command = CompletionsCommand.Create();

        var exitCode = await command.InvokeAsync(Array.Empty<string>());

        Assert.NotEqual(0, exitCode);
    }

    // ==========================================================
    // Unknown option → parser rejects
    // ==========================================================

    [Fact]
    public async Task Query_UnknownOption_ParserRejects()
    {
        var (client, handler) = CreateClientWithHandler();
        var command = QueryCommand.Create(client, new CliConfig());

        var exitCode = await command.InvokeAsync(new[] { "walls", "--nonexistent-flag" });

        Assert.NotEqual(0, exitCode);
        Assert.Equal(0, handler.CallCount);
    }
}
