using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using RevitCli.Output;

namespace RevitCli.Commands;

public static class EvidenceCommand
{
    private const string ManifestFileName = "evidence-manifest.json";
    private static readonly string[] ArtifactExtensions =
    {
        ".json", ".jsonl", ".md", ".txt", ".yml", ".yaml", ".csv", ".sig"
    };

    public static Command Create()
    {
        var command = new Command("evidence", "Package, verify, and publish local BIMOps evidence");
        command.AddCommand(CreatePackageCommand());
        command.AddCommand(CreateVerifyCommand());
        command.AddCommand(CreateSiteCommand());
        return command;
    }

    private static Command CreatePackageCommand()
    {
        var dirOpt = new Option<string?>("--dir", "Project directory containing .revitcli evidence (default: current directory)");
        var outputDirOpt = new Option<string>("--output-dir", () => Path.Combine(".revitcli", "evidence", "packet"), "Directory to write the evidence packet");
        var outputOpt = new Option<string>("--output", () => "table", "Output format: table, json, markdown");
        var command = new Command("package", "Build a local evidence packet manifest and copy indexed artifacts")
        {
            dirOpt,
            outputDirOpt,
            outputOpt
        };

        command.SetHandler(async (string? dir, string outputDir, string output) =>
        {
            Environment.ExitCode = await ExecutePackageAsync(dir, outputDir, output, Console.Out);
        }, dirOpt, outputDirOpt, outputOpt);

        return command;
    }

    private static Command CreateVerifyCommand()
    {
        var packetOpt = new Option<string>("--packet", "Evidence packet directory") { IsRequired = true };
        var outputOpt = new Option<string>("--output", () => "table", "Output format: table, json, markdown");
        var command = new Command("verify", "Verify a local evidence packet manifest and artifact hashes")
        {
            packetOpt,
            outputOpt
        };

        command.SetHandler(async (string packet, string output) =>
        {
            Environment.ExitCode = await ExecuteVerifyAsync(packet, output, Console.Out);
        }, packetOpt, outputOpt);

        return command;
    }

    private static Command CreateSiteCommand()
    {
        var packetOpt = new Option<string>("--packet", "Evidence packet directory") { IsRequired = true };
        var outputDirOpt = new Option<string>("--output-dir", () => Path.Combine(".revitcli", "evidence", "site"), "Directory to write the static evidence site");
        var outputOpt = new Option<string>("--output", () => "table", "Output format: table, json, markdown");
        var command = new Command("site", "Write a static HTML/JSON viewer for a local evidence packet")
        {
            packetOpt,
            outputDirOpt,
            outputOpt
        };

        command.SetHandler(async (string packet, string outputDir, string output) =>
        {
            Environment.ExitCode = await ExecuteSiteAsync(packet, outputDir, output, Console.Out);
        }, packetOpt, outputDirOpt, outputOpt);

        return command;
    }

    public static async Task<int> ExecutePackageAsync(
        string? dir,
        string outputDir,
        string outputFormat,
        TextWriter output)
    {
        if (!TryNormalizeOutput(outputFormat, output, out var normalizedOutput))
            return 1;
        if (string.IsNullOrWhiteSpace(outputDir))
        {
            await output.WriteLineAsync("Error: --output-dir is required.");
            return 1;
        }

        var sourceDirectory = Path.GetFullPath(string.IsNullOrWhiteSpace(dir) ? Directory.GetCurrentDirectory() : dir);
        var packetDirectory = Path.GetFullPath(outputDir);
        var artifactsRoot = Path.Combine(packetDirectory, "artifacts");
        var manifestPath = Path.Combine(packetDirectory, ManifestFileName);

        try
        {
            Directory.CreateDirectory(packetDirectory);
            Directory.CreateDirectory(artifactsRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await output.WriteLineAsync($"Error: failed to create evidence packet directory: {ex.Message}");
            return 1;
        }

        var artifactEntries = new List<EvidenceArtifact>();
        foreach (var sourcePath in EnumerateEvidenceArtifacts(sourceDirectory, packetDirectory))
        {
            var sourceRelativePath = ToRelativeUnixPath(sourceDirectory, sourcePath);
            var packetRelativePath = ToUnixPath(Path.Combine("artifacts", sourceRelativePath));
            var targetPath = Path.Combine(packetDirectory, FromUnixPath(packetRelativePath));
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(sourcePath, targetPath, overwrite: true);

            var info = new FileInfo(targetPath);
            artifactEntries.Add(new EvidenceArtifact
            {
                SourceRelativePath = sourceRelativePath,
                PacketRelativePath = packetRelativePath,
                Category = ClassifyArtifact(sourceRelativePath),
                SchemaHint = DetectSchemaHint(sourcePath),
                SizeBytes = info.Length,
                Sha256 = ComputeSha256(targetPath),
                LastWriteUtc = info.LastWriteTimeUtc
            });
        }

        var manifest = new EvidencePackageManifest
        {
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            Success = true,
            SourceDirectory = sourceDirectory,
            PacketDirectory = packetDirectory,
            Summary = new EvidencePackageSummary
            {
                ArtifactCount = artifactEntries.Count,
                CategoryCount = artifactEntries.Select(artifact => artifact.Category).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                TotalSizeBytes = artifactEntries.Sum(artifact => artifact.SizeBytes)
            },
            Artifacts = artifactEntries
                .OrderBy(artifact => artifact.SourceRelativePath, StringComparer.OrdinalIgnoreCase)
                .ToArray()
        };

        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, TerminalJsonOptions.PrettyCamel));
        await WritePackageOutputAsync(output, manifest, normalizedOutput);
        return 0;
    }

    public static async Task<int> ExecuteVerifyAsync(string packet, string outputFormat, TextWriter output)
    {
        if (!TryNormalizeOutput(outputFormat, output, out var normalizedOutput))
            return 1;

        var packetDirectory = Path.GetFullPath(packet);
        var manifestPath = Path.Combine(packetDirectory, ManifestFileName);
        var issues = new List<EvidenceVerifyIssue>();
        EvidencePackageManifest? manifest = null;

        try
        {
            if (!File.Exists(manifestPath))
            {
                issues.Add(new EvidenceVerifyIssue
                {
                    Code = "manifest-missing",
                    Severity = "error",
                    Message = $"Evidence manifest not found: {manifestPath}"
                });
            }
            else
            {
                manifest = JsonSerializer.Deserialize<EvidencePackageManifest>(
                    await File.ReadAllTextAsync(manifestPath),
                    TerminalJsonOptions.CompactContract);
                if (manifest is null || !string.Equals(manifest.SchemaVersion, "bimops-evidence-package.v1", StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add(new EvidenceVerifyIssue
                    {
                        Code = "manifest-schema",
                        Severity = "error",
                        Message = "Evidence manifest must use schemaVersion bimops-evidence-package.v1."
                    });
                }
            }
        }
        catch (JsonException ex)
        {
            issues.Add(new EvidenceVerifyIssue
            {
                Code = "manifest-json",
                Severity = "error",
                Message = $"Evidence manifest JSON is invalid: {ex.Message}"
            });
        }

        if (manifest is not null)
        {
            foreach (var artifact in manifest.Artifacts)
            {
                var artifactPath = Path.Combine(packetDirectory, FromUnixPath(artifact.PacketRelativePath));
                if (!File.Exists(artifactPath))
                {
                    issues.Add(new EvidenceVerifyIssue
                    {
                        Code = "artifact-missing",
                        Severity = "error",
                        Path = artifact.PacketRelativePath,
                        Message = $"Packaged artifact is missing: {artifact.PacketRelativePath}"
                    });
                    continue;
                }

                var info = new FileInfo(artifactPath);
                if (info.Length != artifact.SizeBytes)
                {
                    issues.Add(new EvidenceVerifyIssue
                    {
                        Code = "size-mismatch",
                        Severity = "error",
                        Path = artifact.PacketRelativePath,
                        Message = $"Expected {artifact.SizeBytes} bytes but found {info.Length} bytes."
                    });
                }

                var actualSha256 = ComputeSha256(artifactPath);
                if (!string.Equals(actualSha256, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add(new EvidenceVerifyIssue
                    {
                        Code = "sha256-mismatch",
                        Severity = "error",
                        Path = artifact.PacketRelativePath,
                        Message = "Packaged artifact SHA256 does not match the manifest."
                    });
                }
            }
        }

        var report = new EvidenceVerifyReport
        {
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            PacketDirectory = packetDirectory,
            ManifestPath = manifestPath,
            Success = issues.Count == 0,
            Summary = new EvidenceVerifySummary
            {
                ArtifactCount = manifest?.Artifacts.Count ?? 0,
                VerifiedArtifactCount = manifest is null ? 0 : manifest.Artifacts.Count - issues.Count(issue => issue.Code == "artifact-missing"),
                IssueCount = issues.Count
            },
            Issues = issues.ToArray()
        };

        await WriteVerifyOutputAsync(output, report, normalizedOutput);
        return report.Success ? 0 : 1;
    }

    public static async Task<int> ExecuteSiteAsync(
        string packet,
        string outputDir,
        string outputFormat,
        TextWriter output)
    {
        if (!TryNormalizeOutput(outputFormat, output, out var normalizedOutput))
            return 1;
        if (string.IsNullOrWhiteSpace(outputDir))
        {
            await output.WriteLineAsync("Error: --output-dir is required.");
            return 1;
        }

        var packetDirectory = Path.GetFullPath(packet);
        var siteDirectory = Path.GetFullPath(outputDir);
        var manifestPath = Path.Combine(packetDirectory, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            await output.WriteLineAsync($"Error: evidence manifest not found: {manifestPath}");
            return 1;
        }

        EvidencePackageManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<EvidencePackageManifest>(
                await File.ReadAllTextAsync(manifestPath),
                TerminalJsonOptions.CompactContract) ?? new EvidencePackageManifest { Success = false };
        }
        catch (JsonException ex)
        {
            await output.WriteLineAsync($"Error: invalid evidence manifest JSON: {ex.Message}");
            return 1;
        }

        Directory.CreateDirectory(siteDirectory);
        File.Copy(manifestPath, Path.Combine(siteDirectory, ManifestFileName), overwrite: true);

        var sourceArtifacts = Path.Combine(packetDirectory, "artifacts");
        var siteArtifacts = Path.Combine(siteDirectory, "artifacts");
        if (Directory.Exists(sourceArtifacts))
            CopyDirectory(sourceArtifacts, siteArtifacts);

        await File.WriteAllTextAsync(Path.Combine(siteDirectory, "index.html"), RenderSiteHtml(manifest), Encoding.UTF8);
        var result = new EvidenceSiteReport
        {
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            PacketDirectory = packetDirectory,
            SiteDirectory = siteDirectory,
            ManifestPath = Path.Combine(siteDirectory, ManifestFileName),
            Success = true,
            ArtifactCount = manifest.Artifacts.Count
        };
        await WriteSiteOutputAsync(output, result, normalizedOutput);
        return 0;
    }

    private static IEnumerable<string> EnumerateEvidenceArtifacts(string sourceDirectory, string packetDirectory)
    {
        var revitCliDirectory = Path.Combine(sourceDirectory, ".revitcli");
        var roots = Directory.Exists(revitCliDirectory)
            ? new[] { revitCliDirectory }
            : Array.Empty<string>();
        var packetFull = Path.GetFullPath(packetDirectory);
        foreach (var root in roots)
        {
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var full = Path.GetFullPath(path);
                if (IsSamePathOrDescendant(full, packetFull))
                    continue;
                if (string.Equals(Path.GetFileName(full), ManifestFileName, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (ArtifactExtensions.Contains(Path.GetExtension(full), StringComparer.OrdinalIgnoreCase))
                    yield return full;
            }
        }
    }

    private static bool IsSamePathOrDescendant(string path, string directory)
    {
        var normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var normalizedDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (string.Equals(normalizedPath, normalizedDirectory, StringComparison.OrdinalIgnoreCase))
            return true;

        return normalizedPath.StartsWith(
            normalizedDirectory + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    private static string ClassifyArtifact(string relativePath)
    {
        var path = relativePath.Replace('\\', '/');
        if (path.Contains("/analytics/", StringComparison.OrdinalIgnoreCase) ||
            path.Contains("/ledger", StringComparison.OrdinalIgnoreCase))
            return "ledger";
        if (path.Contains("/deliveries/", StringComparison.OrdinalIgnoreCase))
            return "deliverables";
        if (path.Contains("/crash/", StringComparison.OrdinalIgnoreCase))
            return "crash";
        if (path.Contains("/journal", StringComparison.OrdinalIgnoreCase))
            return "journal";
        if (path.Contains("/receipts/", StringComparison.OrdinalIgnoreCase))
            return "receipt";
        if (path.Contains("/reports/", StringComparison.OrdinalIgnoreCase))
            return "report";
        if (path.Contains("/workflows/", StringComparison.OrdinalIgnoreCase))
            return "workflow";
        return "evidence";
    }

    private static string? DetectSchemaHint(string path)
    {
        try
        {
            var ext = Path.GetExtension(path);
            var text = ext.Equals(".jsonl", StringComparison.OrdinalIgnoreCase)
                ? File.ReadLines(path).FirstOrDefault(line => !string.IsNullOrWhiteSpace(line))
                : ext.Equals(".json", StringComparison.OrdinalIgnoreCase)
                    ? File.ReadAllText(path)
                    : null;
            if (string.IsNullOrWhiteSpace(text))
                return null;

            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("schemaVersion", out var schema) &&
                schema.ValueKind == JsonValueKind.String)
            {
                return schema.GetString();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }

        return null;
    }

    private static async Task WritePackageOutputAsync(TextWriter output, EvidencePackageManifest manifest, string outputFormat)
    {
        switch (outputFormat)
        {
            case "json":
                await output.WriteLineAsync(JsonSerializer.Serialize(manifest, TerminalJsonOptions.PrettyCamel));
                break;
            case "markdown":
                await output.WriteLineAsync(RenderPackageMarkdown(manifest));
                break;
            default:
                await output.WriteLineAsync($"Evidence package: {manifest.Summary.ArtifactCount} artifact(s)");
                await output.WriteLineAsync($"Manifest: {Path.Combine(manifest.PacketDirectory, ManifestFileName)}");
                break;
        }
    }

    private static async Task WriteVerifyOutputAsync(TextWriter output, EvidenceVerifyReport report, string outputFormat)
    {
        switch (outputFormat)
        {
            case "json":
                await output.WriteLineAsync(JsonSerializer.Serialize(report, TerminalJsonOptions.PrettyCamel));
                break;
            case "markdown":
                await output.WriteLineAsync(RenderVerifyMarkdown(report));
                break;
            default:
                await output.WriteLineAsync($"Evidence packet verify: {(report.Success ? "pass" : "fail")}");
                await output.WriteLineAsync($"Artifacts: {report.Summary.ArtifactCount}");
                await output.WriteLineAsync($"Issues: {report.Summary.IssueCount}");
                break;
        }
    }

    private static async Task WriteSiteOutputAsync(TextWriter output, EvidenceSiteReport report, string outputFormat)
    {
        switch (outputFormat)
        {
            case "json":
                await output.WriteLineAsync(JsonSerializer.Serialize(report, TerminalJsonOptions.PrettyCamel));
                break;
            case "markdown":
                await output.WriteLineAsync($"# Evidence Site{Environment.NewLine}{Environment.NewLine}- Directory: `{report.SiteDirectory}`{Environment.NewLine}- Artifacts: `{report.ArtifactCount}`");
                break;
            default:
                await output.WriteLineAsync($"Evidence site: {report.SiteDirectory}");
                await output.WriteLineAsync($"Artifacts: {report.ArtifactCount}");
                break;
        }
    }

    private static string RenderPackageMarkdown(EvidencePackageManifest manifest)
    {
        var lines = new List<string>
        {
            "# BIMOps Evidence Package",
            "",
            $"- Schema: `{manifest.SchemaVersion}`",
            $"- Source: `{manifest.SourceDirectory}`",
            $"- Artifacts: `{manifest.Summary.ArtifactCount}`",
            "",
            "| Category | Path | SHA256 |",
            "| --- | --- | --- |"
        };
        lines.AddRange(manifest.Artifacts.Select(artifact =>
            $"| `{artifact.Category}` | `{artifact.SourceRelativePath}` | `{artifact.Sha256}` |"));
        lines.Add("");
        return string.Join(Environment.NewLine, lines);
    }

    private static string RenderVerifyMarkdown(EvidenceVerifyReport report)
    {
        var lines = new List<string>
        {
            "# BIMOps Evidence Package Verify",
            "",
            $"- Status: `{(report.Success ? "pass" : "fail")}`",
            $"- Artifacts: `{report.Summary.ArtifactCount}`",
            $"- Issues: `{report.Summary.IssueCount}`",
            "",
        };
        if (report.Issues.Count > 0)
        {
            lines.Add("| Severity | Code | Path | Message |");
            lines.Add("| --- | --- | --- | --- |");
            lines.AddRange(report.Issues.Select(issue =>
                $"| `{issue.Severity}` | `{issue.Code}` | `{issue.Path ?? ""}` | {issue.Message.Replace("|", "\\|", StringComparison.Ordinal)} |"));
            lines.Add("");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string RenderSiteHtml(EvidencePackageManifest manifest)
    {
        var rows = string.Join(Environment.NewLine, manifest.Artifacts.Select(artifact =>
            $"<tr><td>{Html(artifact.Category)}</td><td>{Html(artifact.SourceRelativePath)}</td><td><code>{Html(artifact.SchemaHint ?? "")}</code></td><td><code>{Html(artifact.Sha256)}</code></td></tr>"));
        return $$"""
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <title>BIMOps Evidence Package</title>
  <style>
    body { font-family: Segoe UI, Arial, sans-serif; margin: 2rem; color: #1f2937; }
    table { border-collapse: collapse; width: 100%; }
    th, td { border: 1px solid #d1d5db; padding: 0.45rem 0.6rem; text-align: left; }
    th { background: #f3f4f6; }
    code { font-size: 0.85em; }
  </style>
</head>
<body>
  <h1>BIMOps Evidence Package</h1>
  <p>Schema: <code>{{Html(manifest.SchemaVersion)}}</code></p>
  <p>Artifacts: <strong>{{manifest.Summary.ArtifactCount.ToString(CultureInfo.InvariantCulture)}}</strong></p>
  <p>Manifest JSON: <a href="evidence-manifest.json">evidence-manifest.json</a></p>
  <table>
    <thead><tr><th>Category</th><th>Source path</th><th>Schema</th><th>SHA256</th></tr></thead>
    <tbody>
{{rows}}
    </tbody>
  </table>
</body>
</html>
""";
    }

    private static bool TryNormalizeOutput(string outputFormat, TextWriter output, out string normalized)
    {
        if (TerminalOutputFormat.TryNormalize(outputFormat, out normalized, "table", "json", "markdown"))
            return true;

        output.WriteLine("Error: --output must be 'table', 'json', or 'markdown'.");
        return false;
    }

    private static void CopyDirectory(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        foreach (var file in Directory.GetFiles(sourceDir))
            File.Copy(file, Path.Combine(destinationDir, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.GetDirectories(sourceDir))
            CopyDirectory(dir, Path.Combine(destinationDir, Path.GetFileName(dir)));
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string ToRelativeUnixPath(string root, string path) =>
        ToUnixPath(Path.GetRelativePath(root, path));

    private static string ToUnixPath(string path) =>
        path.Replace('\\', '/');

    private static string FromUnixPath(string path) =>
        path.Replace('/', Path.DirectorySeparatorChar);

    private static string Html(string value) =>
        value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal);

    public sealed class EvidencePackageManifest
    {
        public string SchemaVersion { get; init; } = "bimops-evidence-package.v1";
        public DateTimeOffset GeneratedAtUtc { get; init; }
        public bool Success { get; init; }
        public string SourceDirectory { get; init; } = "";
        public string PacketDirectory { get; init; } = "";
        public EvidencePackageSummary Summary { get; init; } = new();
        public IReadOnlyList<EvidenceArtifact> Artifacts { get; init; } = Array.Empty<EvidenceArtifact>();
    }

    public sealed class EvidencePackageSummary
    {
        public int ArtifactCount { get; init; }
        public int CategoryCount { get; init; }
        public long TotalSizeBytes { get; init; }
    }

    public sealed class EvidenceArtifact
    {
        public string SourceRelativePath { get; init; } = "";
        public string PacketRelativePath { get; init; } = "";
        public string Category { get; init; } = "";
        public string? SchemaHint { get; init; }
        public long SizeBytes { get; init; }
        public string Sha256 { get; init; } = "";
        public DateTime LastWriteUtc { get; init; }
    }

    public sealed class EvidenceVerifyReport
    {
        public string SchemaVersion { get; init; } = "bimops-evidence-package-verify.v1";
        public DateTimeOffset GeneratedAtUtc { get; init; }
        public bool Success { get; init; }
        public string PacketDirectory { get; init; } = "";
        public string ManifestPath { get; init; } = "";
        public EvidenceVerifySummary Summary { get; init; } = new();
        public IReadOnlyList<EvidenceVerifyIssue> Issues { get; init; } = Array.Empty<EvidenceVerifyIssue>();
    }

    public sealed class EvidenceVerifySummary
    {
        public int ArtifactCount { get; init; }
        public int VerifiedArtifactCount { get; init; }
        public int IssueCount { get; init; }
    }

    public sealed class EvidenceVerifyIssue
    {
        public string Severity { get; init; } = "error";
        public string Code { get; init; } = "";
        public string? Path { get; init; }
        public string Message { get; init; } = "";
    }

    public sealed class EvidenceSiteReport
    {
        public string SchemaVersion { get; init; } = "bimops-evidence-site.v1";
        public DateTimeOffset GeneratedAtUtc { get; init; }
        public bool Success { get; init; }
        public string PacketDirectory { get; init; } = "";
        public string SiteDirectory { get; init; } = "";
        public string ManifestPath { get; init; } = "";
        public int ArtifactCount { get; init; }
    }
}
