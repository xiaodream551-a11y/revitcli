using System;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Reflection;
using RevitCli.Output;
using RevitCli.Profile;

namespace RevitCli.Commands;

public static class InitCommand
{
    private static readonly (string Name, string File, string Description)[] Templates =
    {
        ("architectural", "architectural-issue.yml", "Architectural Issue Gate — room data, sheets, pre-issue checks"),
        ("interior", "interior-room-data.yml", "Interior / Room Data — metadata, naming, FM handover"),
        ("general", "general-publish.yml", "General Publish — health checks + DWG/PDF/IFC pipelines"),
    };

    public static Command Create()
    {
        var templateArg = new Argument<string?>("template", () => null,
            $"Profile template: {string.Join(", ", Templates.Select(t => t.Name))}");

        var command = new Command("init", "Create a .revitcli.yml profile in the current directory")
        {
            templateArg
        };

        command.SetHandler(template =>
        {
            var targetPath = Path.Combine(Directory.GetCurrentDirectory(), ProfileLoader.FileName);

            if (File.Exists(targetPath))
            {
                Console.WriteLine($"Error: {ProfileLoader.FileName} already exists in this directory.");
                Console.WriteLine("  Delete it first or edit it directly.");
                Environment.ExitCode = 1;
                return;
            }

            // If no template specified, list options
            if (string.IsNullOrEmpty(template))
            {
                if (ConsoleHelper.IsInteractive)
                {
                    Console.WriteLine("Available templates:");
                    Console.WriteLine();
                    for (var i = 0; i < Templates.Length; i++)
                        Console.WriteLine($"  {i + 1}. {Templates[i].Name,-15} {Templates[i].Description}");
                    Console.WriteLine();
                    Console.Write("Choose (1-3): ");
                    var input = Console.ReadLine()?.Trim();
                    if (int.TryParse(input, out var choice) && choice >= 1 && choice <= Templates.Length)
                        template = Templates[choice - 1].Name;
                    else
                    {
                        Console.WriteLine("Invalid choice.");
                        Environment.ExitCode = 1;
                        return;
                    }
                }
                else
                {
                    Console.WriteLine($"Usage: revitcli init <template>");
                    Console.WriteLine($"Templates: {string.Join(", ", Templates.Select(t => t.Name))}");
                    Environment.ExitCode = 1;
                    return;
                }
            }

            var match = Templates.FirstOrDefault(t =>
                string.Equals(t.Name, template, StringComparison.OrdinalIgnoreCase));

            if (match.File == null)
            {
                Console.WriteLine($"Error: unknown template '{template}'.");
                Console.WriteLine($"  Available: {string.Join(", ", Templates.Select(t => t.Name))}");
                Environment.ExitCode = 1;
                return;
            }

            // Find the profiles directory
            var profilesDir = FindProfilesDir();
            if (profilesDir == null)
            {
                Console.WriteLine("Error: profiles/ directory not found. Install RevitCli properly.");
                Environment.ExitCode = 1;
                return;
            }

            var sourcePath = Path.Combine(profilesDir, match.File);
            if (!File.Exists(sourcePath))
            {
                Console.WriteLine($"Error: template file not found: {sourcePath}");
                Environment.ExitCode = 1;
                return;
            }

            File.Copy(sourcePath, targetPath);
            Console.WriteLine($"Created {ProfileLoader.FileName} from '{match.Name}' template.");
            Console.WriteLine();
            Console.WriteLine("Next steps:");
            Console.WriteLine("  revitcli doctor    # verify setup");
            Console.WriteLine("  revitcli check     # run checks");
        }, templateArg);

        return command;
    }

    private static string? FindProfilesDir()
    {
        // Check relative to executable
        var exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        if (exeDir != null)
        {
            var candidate = Path.Combine(exeDir, "profiles");
            if (Directory.Exists(candidate)) return candidate;

            // Check parent directories (development layout)
            var dir = exeDir;
            for (var i = 0; i < 6; i++)
            {
                dir = Directory.GetParent(dir)?.FullName;
                if (dir == null) break;
                candidate = Path.Combine(dir, "profiles");
                if (Directory.Exists(candidate)) return candidate;
            }
        }

        // Check cwd
        var cwdCandidate = Path.Combine(Directory.GetCurrentDirectory(), "profiles");
        if (Directory.Exists(cwdCandidate)) return cwdCandidate;

        return null;
    }
}
