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
        try
        {
            if (profilePath != null)
            {
                profile = ProfileLoader.Load(profilePath);
                profileDir = Path.GetDirectoryName(Path.GetFullPath(profilePath));
            }
            else
            {
                var discovered = ProfileLoader.Discover();
                if (discovered != null)
                {
                    profile = ProfileLoader.Load(discovered);
                    profileDir = Path.GetDirectoryName(Path.GetFullPath(discovered));
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
            await output.WriteLineAsync($"Error: no {ProfileLoader.FileName} found. Create one in your project root.");
            return 1;
        }

        var pipelineName = name ?? "default";
        if (!profile.Publish.TryGetValue(pipelineName, out var pipeline))
        {
            await output.WriteLineAsync($"Error: publish pipeline '{pipelineName}' not found in profile.");
            if (profile.Publish.Count > 0)
                await output.WriteLineAsync($"Available: {string.Join(", ", profile.Publish.Keys)}");
            return 1;
        }

        // Run precheck if defined
        if (!string.IsNullOrWhiteSpace(pipeline.Precheck))
        {
            await output.WriteLineAsync($"Running precheck '{pipeline.Precheck}' ...");
            var checkResult = await CheckCommand.ExecuteAsync(client, pipeline.Precheck, profilePath, "table", null, true, output);
            if (checkResult != 0)
            {
                await output.WriteLineAsync("Precheck failed. Aborting publish.");
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
                await output.WriteLineAsync($"  Failed: {result.Error ?? result.Data?.Message ?? "Unknown error"}");
                failed++;
            }
        }

        await output.WriteLineAsync("");
        await output.WriteLineAsync($"Publish '{pipelineName}': {succeeded} succeeded, {failed} failed");
        return failed > 0 ? 1 : 0;
    }
}
