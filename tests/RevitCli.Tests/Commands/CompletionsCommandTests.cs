using System.CommandLine;
using System.IO;
using System.Threading.Tasks;
using RevitCli.Commands;
using Xunit;

namespace RevitCli.Tests.Commands;

[Collection("Sequential")]
public class CompletionsCommandTests : IDisposable
{
    private readonly TextWriter _savedOut;
    private readonly TextWriter _savedError;

    public CompletionsCommandTests()
    {
        _savedOut = Console.Out;
        _savedError = Console.Error;
    }

    public void Dispose()
    {
        Console.SetOut(_savedOut);
        Console.SetError(_savedError);
    }

    [Fact]
    public async Task Bash_CompletionsIncludeCommandAndValueSuggestions()
    {
        var stdout = new StringWriter();
        Console.SetOut(stdout);

        var exitCode = await CompletionsCommand.Create().InvokeAsync(new[] { "bash" });
        var script = stdout.ToString();

        Assert.Equal(0, exitCode);
        Assert.Contains("status query export set config audit completions batch doctor check publish init score coverage schedule diff snapshot interactive", script);
        Assert.Contains("compgen -W \"show set\"", script);
        Assert.Contains("defaultOutput)", script);
        Assert.Contains("compgen -f -- \"$cur\"", script);
    }

    [Fact]
    public async Task Zsh_CompletionsIncludeDoctorBatchAndConfigKeys()
    {
        var stdout = new StringWriter();
        Console.SetOut(stdout);

        var exitCode = await CompletionsCommand.Create().InvokeAsync(new[] { "zsh" });
        var script = stdout.ToString();

        Assert.Equal(0, exitCode);
        Assert.Contains("'doctor:Check RevitCli setup and diagnose issues'", script);
        Assert.Contains("'interactive:Enter interactive REPL mode'", script);
        Assert.Contains("_arguments '1:file:_files'", script);
        Assert.Contains("_values 'setting' serverUrl defaultOutput exportDir", script);
    }

    [Fact]
    public async Task PowerShell_CompletionsIncludeTopLevelCommandsAndConfigSuggestions()
    {
        var stdout = new StringWriter();
        Console.SetOut(stdout);

        var exitCode = await CompletionsCommand.Create().InvokeAsync(new[] { "powershell" });
        var script = stdout.ToString();

        Assert.Equal(0, exitCode);
        Assert.Contains("'doctor' = 'Check RevitCli setup and diagnose issues'", script);
        Assert.Contains("'interactive' = 'Enter interactive REPL mode'", script);
        Assert.Contains("$configKeys = @('serverUrl', 'defaultOutput', 'exportDir')", script);
        Assert.Contains("New-RevitCliCompletionResults -Values $shells -ToolTip 'Shell'", script);
    }

    [Fact]
    public async Task BashCompletions_Include_Snapshot_And_Diff()
    {
        var stdout = new StringWriter();
        Console.SetOut(stdout);

        var exitCode = await CompletionsCommand.Create().InvokeAsync(new[] { "bash" });
        var script = stdout.ToString();

        Assert.Equal(0, exitCode);
        Assert.Contains("snapshot", script);
        Assert.Contains("diff", script);
    }

    [Fact]
    public async Task ZshCompletions_Include_Snapshot_And_Diff()
    {
        var stdout = new StringWriter();
        Console.SetOut(stdout);

        var exitCode = await CompletionsCommand.Create().InvokeAsync(new[] { "zsh" });
        var script = stdout.ToString();

        Assert.Equal(0, exitCode);
        Assert.Contains("snapshot", script);
        Assert.Contains("diff", script);
    }

    [Fact]
    public async Task BashCompletions_PublishIncludes_SinceFlags()
    {
        var stdout = new StringWriter();
        Console.SetOut(stdout);

        var exitCode = await CompletionsCommand.Create().InvokeAsync(new[] { "bash" });
        var script = stdout.ToString();

        Assert.Equal(0, exitCode);
        Assert.Contains("--since", script);
        Assert.Contains("--since-mode", script);
        Assert.Contains("--update-baseline", script);
    }

    [Fact]
    public async Task ZshCompletions_PublishIncludes_SinceFlags()
    {
        var stdout = new StringWriter();
        Console.SetOut(stdout);

        var exitCode = await CompletionsCommand.Create().InvokeAsync(new[] { "zsh" });
        var script = stdout.ToString();

        Assert.Equal(0, exitCode);
        Assert.Contains("--since", script);
        Assert.Contains("--since-mode", script);
    }
}
