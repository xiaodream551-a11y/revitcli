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
/// Tests that go through Create().InvokeAsync() — the real CLI entry point.
/// Verifies parser behavior, required options, defaults, and Create/ExecuteAsync wiring.
/// These run with stdout redirected, so ConsoleHelper.IsInteractive = false,
/// which means Create() delegates to ExecuteAsync().
/// </summary>
public class ParserLevelTests
{
    private static RevitClient CreateClient(string? response = null, bool throwException = false)
    {
        var handler = new FakeHttpHandler(response, throwException);
        return new RevitClient(new HttpClient(handler) { BaseAddress = new System.Uri("http://localhost:17839") });
    }

    private static CliConfig DefaultConfig => new();

    // === Status ===

    [Fact]
    public async Task Status_ViaParser_ServerDown_ReturnsNonZero()
    {
        var client = CreateClient(throwException: true);
        var command = StatusCommand.Create(client);

        var exitCode = await command.InvokeAsync(Array.Empty<string>());

        // When server is down, Environment.ExitCode is set to 1
        Assert.Equal(1, Environment.ExitCode);
        Environment.ExitCode = 0; // reset for other tests
    }

    [Fact]
    public async Task Status_ViaParser_ServerUp_Succeeds()
    {
        var status = new StatusInfo { RevitVersion = "2025", DocumentName = "Test.rvt" };
        var response = ApiResponse<StatusInfo>.Ok(status);
        var client = CreateClient(JsonSerializer.Serialize(response));
        var command = StatusCommand.Create(client);

        await command.InvokeAsync(Array.Empty<string>());

        Assert.Equal(0, Environment.ExitCode);
    }

    // === Query ===

    [Fact]
    public async Task Query_ViaParser_NoArgs_ReturnsError()
    {
        var client = CreateClient();
        var command = QueryCommand.Create(client, DefaultConfig);

        await command.InvokeAsync(Array.Empty<string>());

        Assert.Equal(1, Environment.ExitCode);
        Environment.ExitCode = 0;
    }

    [Fact]
    public async Task Query_ViaParser_WithCategory_UsesDefaultOutput()
    {
        var elements = new[] { new ElementInfo { Id = 1, Name = "Wall 1", Category = "Walls" } };
        var response = ApiResponse<ElementInfo[]>.Ok(elements);
        var client = CreateClient(JsonSerializer.Serialize(response));
        var config = new CliConfig { DefaultOutput = "json" };
        var command = QueryCommand.Create(client, config);

        await command.InvokeAsync(new[] { "walls" });

        Assert.Equal(0, Environment.ExitCode);
    }

    [Fact]
    public async Task Query_ViaParser_WithId_Works()
    {
        var element = new ElementInfo { Id = 42, Name = "Element 42" };
        var response = ApiResponse<ElementInfo>.Ok(element);
        var client = CreateClient(JsonSerializer.Serialize(response));
        var command = QueryCommand.Create(client, DefaultConfig);

        await command.InvokeAsync(new[] { "--id", "42" });

        Assert.Equal(0, Environment.ExitCode);
    }

    // === Export ===

    [Fact]
    public async Task Export_ViaParser_MissingRequiredFormat_ReturnsNonZero()
    {
        var client = CreateClient();
        var command = ExportCommand.Create(client, DefaultConfig);

        // --format is required, omitting it should fail at parser level
        var exitCode = await command.InvokeAsync(Array.Empty<string>());

        Assert.NotEqual(0, exitCode);
    }

    [Fact]
    public async Task Export_ViaParser_WithFormat_Works()
    {
        var progress = new ExportProgress { TaskId = "t1", Status = "completed", Progress = 100 };
        var response = ApiResponse<ExportProgress>.Ok(progress);
        var client = CreateClient(JsonSerializer.Serialize(response));
        var command = ExportCommand.Create(client, DefaultConfig);

        await command.InvokeAsync(new[] { "--format", "dwg" });

        Assert.Equal(0, Environment.ExitCode);
        Environment.ExitCode = 0;
    }

    // === Set ===

    [Fact]
    public async Task Set_ViaParser_MissingRequiredParam_ReturnsNonZero()
    {
        var client = CreateClient();
        var command = SetCommand.Create(client);

        // --param and --value are required
        var exitCode = await command.InvokeAsync(new[] { "doors" });

        Assert.NotEqual(0, exitCode);
    }

    [Fact]
    public async Task Set_ViaParser_WithAllArgs_Works()
    {
        var result = new SetResult { Affected = 1 };
        var response = ApiResponse<SetResult>.Ok(result);
        var client = CreateClient(JsonSerializer.Serialize(response));
        var command = SetCommand.Create(client);

        await command.InvokeAsync(new[] { "doors", "--param", "Mark", "--value", "D-01" });

        Assert.Equal(0, Environment.ExitCode);
        Environment.ExitCode = 0;
    }

    [Fact]
    public async Task Set_ViaParser_DryRunFlag_Works()
    {
        var result = new SetResult { Affected = 0 };
        var response = ApiResponse<SetResult>.Ok(result);
        var client = CreateClient(JsonSerializer.Serialize(response));
        var command = SetCommand.Create(client);

        await command.InvokeAsync(new[] { "doors", "--param", "Mark", "--value", "D-01", "--dry-run" });

        Assert.Equal(0, Environment.ExitCode);
        Environment.ExitCode = 0;
    }

    // === Audit ===

    [Fact]
    public async Task Audit_ViaParser_NoArgs_RunsAllRules()
    {
        var auditResult = new AuditResult { Passed = 5, Failed = 0 };
        var response = ApiResponse<AuditResult>.Ok(auditResult);
        var client = CreateClient(JsonSerializer.Serialize(response));
        var command = AuditCommand.Create(client);

        await command.InvokeAsync(Array.Empty<string>());

        Assert.Equal(0, Environment.ExitCode);
    }

    [Fact]
    public async Task Audit_ViaParser_ListFlag_Succeeds()
    {
        var client = CreateClient();
        var command = AuditCommand.Create(client);

        await command.InvokeAsync(new[] { "--list" });

        Assert.Equal(0, Environment.ExitCode);
    }
}
