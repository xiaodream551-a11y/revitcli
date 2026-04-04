using System;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using RevitCli.Client;
using RevitCli.Config;
using RevitCli.Output;
using RevitCli.Profile;
using RevitCli.Shared;
using Spectre.Console;

namespace RevitCli.Commands;

public static class DoctorCommand
{
    public static Command Create(RevitClient client, CliConfig config)
    {
        var command = new Command("doctor", "Check RevitCli setup and diagnose issues");

        command.SetHandler(async () =>
        {
            if (!ConsoleHelper.IsInteractive)
            {
                Environment.ExitCode = await ExecuteAsync(client, config, Console.Out);
                return;
            }

            await RunChecksSpectre(client, config);
        });

        return command;
    }

    public static async Task<int> ExecuteAsync(RevitClient client, CliConfig config, TextWriter output)
    {
        // 1. Config file
        var configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".revitcli", "config.json");
        if (File.Exists(configPath))
            await output.WriteLineAsync($"OK: Configuration file exists ({configPath})");
        else
            await output.WriteLineAsync($"INFO: No configuration file ({configPath}) - using defaults");

        // 2. Server URL
        await output.WriteLineAsync($"OK: Server URL: {config.ServerUrl}");

        // 3. Server info file
        var serverInfoPath = CliConfig.ServerInfoPath;
        if (File.Exists(serverInfoPath))
        {
            try
            {
                var json = File.ReadAllText(serverInfoPath);
                var info = JsonSerializer.Deserialize<ServerInfo>(json);
                if (info != null)
                    await output.WriteLineAsync($"OK: Server info: port={info.Port}, pid={info.Pid}, started={info.StartedAt}");
                else
                    await output.WriteLineAsync("WARN: Server info file exists but is empty/invalid");
            }
            catch
            {
                await output.WriteLineAsync("WARN: Server info file exists but cannot be parsed");
            }
        }
        else
        {
            await output.WriteLineAsync("INFO: No server info file (OK if Revit is not running)");
        }

        // 4. Connection test
        var status = await client.GetStatusAsync();
        if (status.Success)
        {
            await output.WriteLineAsync($"OK: Connected to Revit {status.Data!.RevitVersion}");
            if (status.Data.DocumentName != null)
                await output.WriteLineAsync($"OK: Document: {status.Data.DocumentName}");
            else
                await output.WriteLineAsync("INFO: No document open");
        }
        else
        {
            await output.WriteLineAsync($"FAIL: {status.Error}");
            return 1;
        }

        // 5. Project profile
        WriteProfileInfo(null, s => output.WriteLineAsync(s).Wait());

        return 0;
    }

    private static void WriteProfileInfo(Action<string>? spectreWrite, Action<string>? plainWrite)
    {
        var profilePath = ProfileLoader.Discover();
        if (profilePath == null)
        {
            var msg = $"No {ProfileLoader.FileName} found in directory tree";
            spectreWrite?.Invoke($"  [blue]\u25cb[/] {msg}");
            plainWrite?.Invoke($"INFO: {msg}");
            return;
        }

        spectreWrite?.Invoke($"  [green]\u2713[/] Profile: [cyan]{Markup.Escape(profilePath)}[/]");
        plainWrite?.Invoke($"OK: Profile: {profilePath}");

        try
        {
            var profile = ProfileLoader.Load(profilePath);

            if (profile.Checks.Count > 0)
            {
                var checks = string.Join(", ", profile.Checks.Keys);
                spectreWrite?.Invoke($"      Check sets: [white]{Markup.Escape(checks)}[/]");
                plainWrite?.Invoke($"  Check sets: {checks}");
            }

            if (profile.Exports.Count > 0)
            {
                var exports = string.Join(", ", profile.Exports.Keys);
                spectreWrite?.Invoke($"      Export presets: [white]{Markup.Escape(exports)}[/]");
                plainWrite?.Invoke($"  Export presets: {exports}");
            }

            if (profile.Publish.Count > 0)
            {
                var pipelines = string.Join(", ", profile.Publish.Keys);
                spectreWrite?.Invoke($"      Publish pipelines: [white]{Markup.Escape(pipelines)}[/]");
                plainWrite?.Invoke($"  Publish pipelines: {pipelines}");
            }

            if (!string.IsNullOrWhiteSpace(profile.Extends))
            {
                spectreWrite?.Invoke($"      Extends: [dim]{Markup.Escape(profile.Extends)}[/]");
                plainWrite?.Invoke($"  Extends: {profile.Extends}");
            }
        }
        catch (Exception ex)
        {
            spectreWrite?.Invoke($"  [red]\u2717[/] Profile parse error: [red]{Markup.Escape(ex.Message)}[/]");
            plainWrite?.Invoke($"FAIL: Profile parse error: {ex.Message}");
        }
    }

    private static async Task RunChecksSpectre(RevitClient client, CliConfig config)
    {
        AnsiConsole.MarkupLine("[bold]RevitCli Doctor[/]");
        AnsiConsole.WriteLine();

        // 1. Config file
        var configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".revitcli", "config.json");
        if (File.Exists(configPath))
            AnsiConsole.MarkupLine($"  [green]\u2713[/] Configuration file: [dim]{Markup.Escape(configPath)}[/]");
        else
            AnsiConsole.MarkupLine($"  [blue]\u25cb[/] No configuration file [dim](using defaults)[/]");

        // 2. Server URL
        AnsiConsole.MarkupLine($"  [green]\u2713[/] Server URL: [cyan]{Markup.Escape(config.ServerUrl)}[/]");

        // 3. Server info
        var serverInfoPath = CliConfig.ServerInfoPath;
        if (File.Exists(serverInfoPath))
        {
            try
            {
                var json = File.ReadAllText(serverInfoPath);
                var info = JsonSerializer.Deserialize<ServerInfo>(json);
                if (info != null)
                {
                    // Check if PID is alive
                    var alive = false;
                    try
                    {
                        using var proc = System.Diagnostics.Process.GetProcessById(info.Pid);
                        alive = !proc.HasExited;
                    }
                    catch { }

                    if (alive)
                        AnsiConsole.MarkupLine($"  [green]\u2713[/] Server info: port=[cyan]{info.Port}[/], pid=[cyan]{info.Pid}[/] [green](running)[/]");
                    else
                        AnsiConsole.MarkupLine($"  [yellow]![/] Server info: port=[cyan]{info.Port}[/], pid=[cyan]{info.Pid}[/] [yellow](process not found)[/]");
                }
            }
            catch
            {
                AnsiConsole.MarkupLine("  [yellow]![/] Server info file exists but cannot be parsed");
            }
        }
        else
        {
            AnsiConsole.MarkupLine("  [blue]\u25cb[/] No server info file [dim](OK if Revit not running)[/]");
        }

        // 4. Connection test
        var status = await client.GetStatusAsync();
        if (status.Success)
        {
            AnsiConsole.MarkupLine($"  [green]\u2713[/] Connected to Revit [green]{Markup.Escape(status.Data!.RevitVersion)}[/]");
            if (status.Data.DocumentName != null)
                AnsiConsole.MarkupLine($"  [green]\u2713[/] Document: [cyan]{Markup.Escape(status.Data.DocumentName)}[/]");
            else
                AnsiConsole.MarkupLine("  [blue]\u25cb[/] No document open");
        }
        else
        {
            AnsiConsole.MarkupLine($"  [red]\u2717[/] Connection failed: [red]{Markup.Escape(status.Error ?? "Unknown")}[/]");
            Environment.ExitCode = 1;
        }

        // 5. Project profile
        AnsiConsole.WriteLine();
        WriteProfileInfo(s => AnsiConsole.MarkupLine(s), null);

        AnsiConsole.WriteLine();
    }
}
