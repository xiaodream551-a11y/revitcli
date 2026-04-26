using System;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Xml.Linq;
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
            Environment.ExitCode = await ExecuteAsync(client, config, Console.Out);
        });

        return command;
    }

    public static Task<int> ExecuteAsync(RevitClient client, CliConfig config, TextWriter output)
    {
        return ExecuteAsync(client, config, output, DoctorEnvironment.Current());
    }

    internal static async Task<int> ExecuteAsync(
        RevitClient client,
        CliConfig config,
        TextWriter output,
        DoctorEnvironment environment)
    {
        var hasFailure = false;

        // 1. Config file
        var configPath = environment.ConfigPath;
        if (File.Exists(configPath))
            await output.WriteLineAsync($"OK: Configuration file exists ({configPath})");
        else
            await output.WriteLineAsync($"INFO: No configuration file ({configPath}) - using defaults");

        // 2. Revit 2026 local prerequisites
        hasFailure |= !await WriteRevit2026ApiCheck(output, environment);
        hasFailure |= !await WriteAddinManifestCheck(output, environment);

        // 3. Server URL
        await output.WriteLineAsync($"OK: Server URL: {config.ServerUrl}");

        // 4. Server info file
        hasFailure |= !await WriteServerInfoCheck(output, environment);

        // 5. Connection test
        var status = await client.GetStatusAsync();
        if (status.Success)
        {
            await output.WriteLineAsync($"OK: Connected to Revit {status.Data!.RevitVersion}");
            if (!IsRevit2026(status.Data))
            {
                await output.WriteLineAsync(
                    $"FAIL: Connected Revit version is {status.Data.RevitVersion}; this internal smoke baseline targets Revit 2026 only.");
                hasFailure = true;
            }
            if (status.Data.DocumentName != null)
                await output.WriteLineAsync($"OK: Document: {status.Data.DocumentName}");
            else
                await output.WriteLineAsync("INFO: No document open");
        }
        else
        {
            await output.WriteLineAsync($"FAIL: {status.Error}");
            await output.WriteLineAsync("HINT: Start Revit 2026, confirm the RevitCli add-in is loaded, open the test model, then rerun 'revitcli doctor'.");
            hasFailure = true;
        }

        // 6. Project profile
        WriteProfileInfo(null, s => output.WriteLine(s));

        return hasFailure ? 1 : 0;
    }

    private static bool IsRevit2026(StatusInfo status)
    {
        if (status.RevitYear != 0)
            return status.RevitYear == 2026;

        return status.RevitVersion.Contains("2026", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> WriteRevit2026ApiCheck(TextWriter output, DoctorEnvironment environment)
    {
        var installDir = environment.ResolvedRevit2026InstallDir;
        var missing = new[] { "RevitAPI.dll", "RevitAPIUI.dll" }
            .Where(dll => !File.Exists(Path.Combine(installDir, dll)))
            .ToArray();

        if (missing.Length == 0)
        {
            await output.WriteLineAsync($"OK: Revit 2026 API DLLs found ({installDir})");
            return true;
        }

        await output.WriteLineAsync(
            $"FAIL: Revit 2026 API DLLs missing at {installDir}: {string.Join(", ", missing)}");
        await output.WriteLineAsync(
            "HINT: Install Revit 2026 or set REVITCLI_REVIT2026_INSTALL_DIR / Revit2026InstallDir to the Revit 2026 install directory.");
        return false;
    }

    private static async Task<bool> WriteAddinManifestCheck(TextWriter output, DoctorEnvironment environment)
    {
        var manifestPath = environment.ManifestPath;
        if (!File.Exists(manifestPath))
        {
            await output.WriteLineAsync($"FAIL: Add-in manifest missing ({manifestPath})");
            await output.WriteLineAsync("HINT: Build/publish the add-in and install RevitCli.addin under Autodesk\\Revit\\Addins\\2026.");
            return false;
        }

        try
        {
            var doc = XDocument.Load(manifestPath);
            var assembly = doc.Descendants("Assembly").FirstOrDefault()?.Value.Trim();
            if (string.IsNullOrWhiteSpace(assembly))
            {
                await output.WriteLineAsync($"FAIL: Add-in manifest has no Assembly path ({manifestPath})");
                return false;
            }

            var assemblyPath = Path.IsPathRooted(assembly)
                ? assembly
                : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(manifestPath)!, assembly));
            if (!File.Exists(assemblyPath))
            {
                await output.WriteLineAsync($"FAIL: Add-in assembly from manifest does not exist ({assemblyPath})");
                return false;
            }

            await output.WriteLineAsync($"OK: Add-in manifest: {manifestPath}");
            await output.WriteLineAsync($"OK: Add-in assembly: {assemblyPath}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            await output.WriteLineAsync($"FAIL: Add-in manifest cannot be read ({manifestPath}): {ex.Message}");
            return false;
        }
    }

    private static async Task<bool> WriteServerInfoCheck(TextWriter output, DoctorEnvironment environment)
    {
        var serverInfoPath = environment.ServerInfoPath;
        if (!File.Exists(serverInfoPath))
        {
            await output.WriteLineAsync("INFO: No server info file (OK if Revit is not running)");
            return true;
        }

        ServerInfo? info;
        try
        {
            var json = File.ReadAllText(serverInfoPath);
            info = JsonSerializer.Deserialize<ServerInfo>(json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            await output.WriteLineAsync($"FAIL: Server info file exists but cannot be parsed ({serverInfoPath}): {ex.Message}");
            return false;
        }

        if (info == null)
        {
            await output.WriteLineAsync($"FAIL: Server info file is empty or invalid ({serverInfoPath})");
            return false;
        }

        var failures = new List<string>();
        if (info.Port < 1024 || info.Port > 65535)
            failures.Add($"invalid port={info.Port}");
        if (info.Pid <= 0)
            failures.Add($"invalid pid={info.Pid}");
        if (string.IsNullOrWhiteSpace(info.Token))
            failures.Add("missing token");

        string? processName = null;
        if (info.Pid > 0)
        {
            try
            {
                using var proc = System.Diagnostics.Process.GetProcessById(info.Pid);
                if (proc.HasExited)
                    failures.Add($"stale pid={info.Pid}");
                else
                {
                    processName = proc.ProcessName;
                    if (!processName.Contains("Revit", StringComparison.OrdinalIgnoreCase))
                        failures.Add($"pid={info.Pid} belongs to process '{processName}', not Revit");
                }
            }
            catch (ArgumentException)
            {
                failures.Add($"stale pid={info.Pid}");
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                failures.Add($"cannot inspect pid={info.Pid}: {ex.Message}");
            }
        }

        if (failures.Count > 0)
        {
            await output.WriteLineAsync(
                $"FAIL: Server info is stale or invalid ({serverInfoPath}): {string.Join(", ", failures)}");
            await output.WriteLineAsync("HINT: Close Revit, delete the stale server.json if it remains, restart Revit 2026, and rerun doctor.");
            return false;
        }

        var processSuffix = processName == null ? "" : $", process={processName}";
        await output.WriteLineAsync(
            $"OK: Server info: port={info.Port}, pid={info.Pid}{processSuffix}, started={info.StartedAt}");
        return true;
    }

    private static void WriteProfileInfo(Action<string>? spectreWrite, Action<string>? plainWrite)
    {
        var profilePath = ProfileLoader.Discover();
        if (profilePath == null)
        {
            var msg = $"No {ProfileLoader.FileName} found in directory tree";
            spectreWrite?.Invoke($"  [blue]\u25cb[/] {msg}");
            plainWrite?.Invoke($"INFO: {msg}");

            // Quickstart guidance
            spectreWrite?.Invoke("");
            spectreWrite?.Invoke("  [yellow]Quick start:[/]");
            spectreWrite?.Invoke("    1. Copy a starter profile to your project root:");
            spectreWrite?.Invoke("       [dim]cp profiles/architectural-issue.yml .revitcli.yml[/]");
            spectreWrite?.Invoke("       [dim]cp profiles/interior-room-data.yml .revitcli.yml[/]");
            spectreWrite?.Invoke("       [dim]cp profiles/general-publish.yml .revitcli.yml[/]");
            spectreWrite?.Invoke("    2. Run: [white]revitcli check[/]");
            spectreWrite?.Invoke("    3. Run: [white]revitcli publish --dry-run[/]");

            plainWrite?.Invoke("");
            plainWrite?.Invoke("Quick start:");
            plainWrite?.Invoke("  1. Copy a starter profile: cp profiles/general-publish.yml .revitcli.yml");
            plainWrite?.Invoke("  2. Run: revitcli check");
            plainWrite?.Invoke("  3. Run: revitcli publish --dry-run");
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

}

internal sealed class DoctorEnvironment
{
    public string UserProfile { get; init; } =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public string AppData { get; init; } =
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    public string? Revit2026InstallDir { get; init; }

    public string ConfigPath => Path.Combine(UserProfile, ".revitcli", "config.json");

    public string ServerInfoPath => Path.Combine(UserProfile, ".revitcli", "server.json");

    public string ManifestPath => Path.Combine(
        AppData, "Autodesk", "Revit", "Addins", "2026", "RevitCli.addin");

    public string ResolvedRevit2026InstallDir =>
        !string.IsNullOrWhiteSpace(Revit2026InstallDir)
            ? Revit2026InstallDir!
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Autodesk", "Revit 2026");

    public static DoctorEnvironment Current()
    {
        return new DoctorEnvironment
        {
            Revit2026InstallDir =
                Environment.GetEnvironmentVariable("REVITCLI_REVIT2026_INSTALL_DIR") ??
                Environment.GetEnvironmentVariable("Revit2026InstallDir")
        };
    }
}
