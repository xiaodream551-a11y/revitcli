using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using RevitCli.Client;
using RevitCli.Output;
using RevitCli.Profile;
using RevitCli.Shared;
using Spectre.Console;

namespace RevitCli.Commands;

public static class PublishCommand
{
    public static Command Create(RevitClient client)
    {
        var nameArg = new Argument<string?>("name", () => null, "Publish pipeline name (default: 'default')");
        var profileOpt = new Option<string?>("--profile", "Path to .revitcli.yml profile");
        var dryRunOpt = new Option<bool>("--dry-run", "Show what would be exported without exporting");

        var command = new Command("publish", "Run export pipeline from .revitcli.yml profile")
        {
            nameArg, profileOpt, dryRunOpt
        };

        command.SetHandler(async (name, profilePath, dryRun) =>
        {
            Environment.ExitCode = await ExecuteAsync(client, name, profilePath, dryRun, Console.Out);
        }, nameArg, profileOpt, dryRunOpt);

        return command;
    }

    public static async Task<int> ExecuteAsync(RevitClient client, string? name, string? profilePath, bool dryRun, TextWriter output)
    {
        // Load profile
        ProjectProfile? profile;
        string? profileDir = null;
        string? resolvedProfilePath = null;
        try
        {
            if (profilePath != null)
            {
                resolvedProfilePath = Path.GetFullPath(profilePath);
                profile = ProfileLoader.Load(resolvedProfilePath);
                profileDir = Path.GetDirectoryName(resolvedProfilePath);
            }
            else
            {
                var discovered = ProfileLoader.Discover();
                if (discovered != null)
                {
                    resolvedProfilePath = Path.GetFullPath(discovered);
                    profile = ProfileLoader.Load(resolvedProfilePath);
                    profileDir = Path.GetDirectoryName(resolvedProfilePath);
                }
                else
                {
                    profile = null;
                }
            }
        }
        catch (Exception ex)
        {
            await output.WriteLineAsync($"Error loading profile: {ex.Message}");
            return 1;
        }

        if (profile == null)
        {
            await output.WriteLineAsync($"Error: no {ProfileLoader.FileName} found.");
            await output.WriteLineAsync($"  Create one in your project root, or copy from .revitcli.example.yml");
            return 1;
        }

        var pipelineName = name ?? "default";
        if (!profile.Publish.TryGetValue(pipelineName, out var pipeline))
        {
            await output.WriteLineAsync($"Error: publish pipeline '{pipelineName}' not found in profile.");
            if (profile.Publish.Count > 0)
                await output.WriteLineAsync($"  Available pipelines: {string.Join(", ", profile.Publish.Keys)}");
            else
                await output.WriteLineAsync($"  Your profile has no publish pipelines. Add a 'publish:' section.");
            return 1;
        }

        // Run precheck if defined
        if (!string.IsNullOrWhiteSpace(pipeline.Precheck))
        {
            await output.WriteLineAsync($"Running precheck '{pipeline.Precheck}' ...");
            var checkResult = await CheckCommand.ExecuteAsync(client, pipeline.Precheck, profilePath, "table", null, true, false, output);
            if (checkResult != 0)
            {
                await output.WriteLineAsync("Precheck failed. Aborting publish.");
                await output.WriteLineAsync("  Fix the issues above, or use suppressions to waive known problems.");
                return 1;
            }
            await output.WriteLineAsync("");
        }

        // Run each export preset
        var succeeded = 0;
        var failed = 0;

        foreach (var presetName in pipeline.Presets)
        {
            if (!profile.Exports.TryGetValue(presetName, out var preset))
            {
                await output.WriteLineAsync($"Error: export preset '{presetName}' not found in profile.");
                if (profile.Exports.Count > 0)
                    await output.WriteLineAsync($"  Available presets: {string.Join(", ", profile.Exports.Keys)}");
                failed++;
                continue;
            }

            // Resolve output dir relative to profile file
            var outputDir = preset.OutputDir ?? profile.Defaults.OutputDir ?? "./exports";
            if (profileDir != null && !Path.IsPathRooted(outputDir))
                outputDir = Path.GetFullPath(Path.Combine(profileDir, outputDir));

            if (dryRun)
            {
                await output.WriteLineAsync($"[dry-run] Would export '{presetName}': format={preset.Format}, outputDir={outputDir}");
                succeeded++;
                continue;
            }

            await output.WriteLineAsync($"Exporting '{presetName}' ({preset.Format.ToUpper()}) ...");

            var request = new ExportRequest
            {
                Format = preset.Format,
                Sheets = preset.Sheets ?? new List<string>(),
                Views = preset.Views ?? new List<string>(),
                OutputDir = outputDir
            };

            var result = await client.ExportAsync(request);
            if (result.Success && result.Data?.Status == "completed")
            {
                await output.WriteLineAsync($"  Completed: {result.Data.Message ?? "OK"}");
                succeeded++;
            }
            else
            {
                var errMsg = result.Error ?? result.Data?.Message ?? "Unknown error";
                await output.WriteLineAsync($"  Failed: {errMsg}");
                if (errMsg.Contains("not running"))
                    await output.WriteLineAsync("  Run 'revitcli doctor' to diagnose connection issues.");
                failed++;
            }
        }

        await output.WriteLineAsync("");
        await output.WriteLineAsync($"Publish '{pipelineName}': {succeeded} succeeded, {failed} failed");

        var exitCode = failed > 0 ? 1 : 0;

        // Journal log + receipt
        if (!dryRun)
        {
            var receipt = new
            {
                action = "publish",
                pipeline = pipelineName,
                succeeded,
                failed,
                presets = pipeline.Presets,
                timestamp = DateTime.UtcNow.ToString("o"),
                user = Environment.UserName,
                profileHash = resolvedProfilePath != null && File.Exists(resolvedProfilePath)
                    ? ComputeFileHash(resolvedProfilePath) : null,
                machine = Environment.MachineName
            };

            Output.JournalLogger.Log(profileDir, receipt);

            // Write receipt file
            try
            {
                var receiptDir = profileDir ?? Directory.GetCurrentDirectory();
                var receiptPath = Path.Combine(receiptDir, ".revitcli", "receipts",
                    $"{pipelineName}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
                var dir = Path.GetDirectoryName(receiptPath)!;
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(receiptPath, System.Text.Json.JsonSerializer.Serialize(receipt,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                await output.WriteLineAsync($"Receipt saved to {receiptPath}");
            }
            catch { /* best effort */ }
        }

        // Webhook notification
        if (!string.IsNullOrWhiteSpace(profile.Defaults.Notify) && !dryRun)
        {
            await Output.WebhookNotifier.NotifyAsync(profile.Defaults.Notify, new
            {
                type = "publish",
                pipeline = pipelineName,
                succeeded,
                failed,
                presets = pipeline.Presets,
                status = exitCode == 0 ? "passed" : "failed",
                timestamp = DateTime.UtcNow.ToString("o")
            });
        }

        return exitCode;
    }

    private static string ComputeFileHash(string path)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        using var stream = File.OpenRead(path);
        var hash = sha.ComputeHash(stream);
        return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant()[..16];
    }
}
