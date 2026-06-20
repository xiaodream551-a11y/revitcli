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
    private readonly string _root;

    public BatchCommandTests()
    {
        _savedExitCode = Environment.ExitCode;
        Environment.ExitCode = 0;
        _root = Path.Combine(Path.GetTempPath(), "revitcli-batch-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        Environment.ExitCode = _savedExitCode;
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
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

    [Fact]
    public async Task Projects_DryRunJson_PlansEachManifestProjectWithoutWritingResults()
    {
        var manifestPath = WriteProjectManifest(("tower.rvt", "hash-tower"), ("school.rvt", "hash-school"));
        var resultsPath = Path.Combine(_root, ".revitcli", "batch", "results.jsonl");
        var output = new StringWriter();

        var exitCode = await BatchCommand.ExecuteProjectsAsync(
            Client(),
            new CliConfig(),
            manifestPath,
            "rvt scan {modelDirectory} --non-recursive --output json",
            resultsPath,
            summaryPath: null,
            timeoutMs: 0,
            resume: false,
            dryRun: true,
            outputFormat: "json",
            output);

        Assert.Equal(0, exitCode);
        Assert.False(File.Exists(resultsPath));
        using var json = JsonDocument.Parse(output.ToString());
        var root = json.RootElement;
        Assert.Equal("batch-project-run.v1", root.GetProperty("schemaVersion").GetString());
        Assert.True(root.GetProperty("dryRun").GetBoolean());
        Assert.Equal(2, root.GetProperty("summary").GetProperty("totalProjectCount").GetInt32());
        Assert.Equal(2, root.GetProperty("summary").GetProperty("plannedProjectCount").GetInt32());
        Assert.Equal(0, root.GetProperty("summary").GetProperty("executedProjectCount").GetInt32());
        Assert.All(root.GetProperty("projects").EnumerateArray(), project =>
        {
            Assert.Equal("planned", project.GetProperty("status").GetString());
            Assert.Contains("rvt scan", project.GetProperty("command").GetString(), StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Projects_Run_WritesJsonlResultsAndMarkdownSummary()
    {
        WriteRvt("tower.rvt");
        WriteRvt("school.rvt");
        var manifestPath = WriteProjectManifest(("tower.rvt", "hash-tower"), ("school.rvt", "hash-school"));
        var resultsPath = Path.Combine(_root, ".revitcli", "batch", "results.jsonl");
        var summaryPath = Path.Combine(_root, ".revitcli", "batch", "summary.md");
        var output = new StringWriter();

        var exitCode = await BatchCommand.ExecuteProjectsAsync(
            Client(),
            new CliConfig(),
            manifestPath,
            "rvt scan {modelDirectory} --non-recursive --output json",
            resultsPath,
            summaryPath,
            timeoutMs: 0,
            resume: false,
            dryRun: false,
            outputFormat: "json",
            output);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(resultsPath));
        Assert.True(File.Exists(summaryPath));
        var resultLines = File.ReadAllLines(resultsPath);
        Assert.Equal(2, resultLines.Length);
        Assert.All(resultLines, line =>
        {
            using var row = JsonDocument.Parse(line);
            Assert.Equal("batch-project-result.v1", row.RootElement.GetProperty("schemaVersion").GetString());
            Assert.Equal("succeeded", row.RootElement.GetProperty("status").GetString());
            Assert.Equal(0, row.RootElement.GetProperty("exitCode").GetInt32());
            Assert.Contains("rvt scan", row.RootElement.GetProperty("command").GetString(), StringComparison.Ordinal);
        });
        Assert.Contains("# Project Batch Summary", File.ReadAllText(summaryPath), StringComparison.Ordinal);

        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal(2, json.RootElement.GetProperty("summary").GetProperty("executedProjectCount").GetInt32());
        Assert.Equal(2, json.RootElement.GetProperty("summary").GetProperty("succeededProjectCount").GetInt32());
    }

    [Fact]
    public async Task Projects_Resume_SkipsSucceededPathHashes()
    {
        WriteRvt("tower.rvt");
        WriteRvt("school.rvt");
        var manifestPath = WriteProjectManifest(("tower.rvt", "hash-tower"), ("school.rvt", "hash-school"));
        var resultsPath = Path.Combine(_root, ".revitcli", "batch", "results.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(resultsPath)!);
        await File.WriteAllTextAsync(
            resultsPath,
            JsonSerializer.Serialize(new
            {
                schemaVersion = "batch-project-result.v1",
                pathHash = "hash-tower",
                status = "succeeded",
                exitCode = 0
            }) + Environment.NewLine);
        var output = new StringWriter();

        var exitCode = await BatchCommand.ExecuteProjectsAsync(
            Client(),
            new CliConfig(),
            manifestPath,
            "rvt scan {modelDirectory} --non-recursive --output json",
            resultsPath,
            summaryPath: null,
            timeoutMs: 0,
            resume: true,
            dryRun: false,
            outputFormat: "json",
            output);

        Assert.Equal(0, exitCode);
        Assert.Equal(2, File.ReadAllLines(resultsPath).Length);
        using var json = JsonDocument.Parse(output.ToString());
        var summary = json.RootElement.GetProperty("summary");
        Assert.Equal(1, summary.GetProperty("skippedProjectCount").GetInt32());
        Assert.Equal(1, summary.GetProperty("executedProjectCount").GetInt32());
        Assert.Contains(json.RootElement.GetProperty("projects").EnumerateArray(), project =>
            project.GetProperty("pathHash").GetString() == "hash-tower" &&
            project.GetProperty("status").GetString() == "skipped");
    }

    [Fact]
    public async Task Projects_RejectsMutatingCommandTemplate()
    {
        var manifestPath = WriteProjectManifest(("tower.rvt", "hash-tower"));
        var resultsPath = Path.Combine(_root, ".revitcli", "batch", "results.jsonl");
        var output = new StringWriter();

        var exitCode = await BatchCommand.ExecuteProjectsAsync(
            Client(),
            new CliConfig(),
            manifestPath,
            "set doors --param Mark --value A-101 --yes",
            resultsPath,
            summaryPath: null,
            timeoutMs: 0,
            resume: false,
            dryRun: false,
            outputFormat: "json",
            output);

        Assert.Equal(1, exitCode);
        Assert.False(File.Exists(resultsPath));
        Assert.Contains("read-only", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private RevitClient Client()
    {
        var handler = new FakeHttpHandler("{}");
        return new RevitClient(new HttpClient(handler) { BaseAddress = new System.Uri("http://localhost:17839") });
    }

    private string WriteProjectManifest(params (string RelativePath, string PathHash)[] projects)
    {
        var manifestPath = Path.Combine(_root, ".revitcli", "projects", "manifest.json");
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        var entries = projects.Select(project =>
        {
            var path = Path.Combine(_root, project.RelativePath);
            return new
            {
                path,
                relativePath = project.RelativePath.Replace(Path.DirectorySeparatorChar, '/'),
                fileName = Path.GetFileName(project.RelativePath),
                pathHash = project.PathHash,
                classification = "candidate-project-model",
                confidence = "high",
                sizeBytes = File.Exists(path) ? new FileInfo(path).Length : 0,
                lastWriteUtc = DateTime.UtcNow,
                backupHint = new { hasNumberedBackups = false, numberedBackupCount = 0, latestBackupNumber = (string?)null },
                localityHint = new { kind = "local", confidence = "high", reason = "test" }
            };
        }).ToArray();
        var manifest = new
        {
            schemaVersion = "rvt-project-manifest.v1",
            command = "rvt manifest",
            success = true,
            generatedAtUtc = DateTimeOffset.UtcNow,
            root = _root,
            recursive = true,
            summary = new
            {
                projectCount = entries.Length,
                backupFileCount = 0,
                orphanBackupFileCount = 0,
                projectSizeBytes = 0,
                backupSizeBytes = 0,
                totalSizeBytes = 0,
                issueCount = 0
            },
            projects = entries,
            issues = Array.Empty<object>()
        };
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest));
        return manifestPath;
    }

    private string WriteRvt(string relativePath)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "rvt");
        return path;
    }
}
