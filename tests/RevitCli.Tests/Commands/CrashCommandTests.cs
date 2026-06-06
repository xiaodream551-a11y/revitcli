using System.Text.Json;
using RevitCli.Commands;
using RevitCli.Crash;

namespace RevitCli.Tests.Commands;

[Collection("Sequential")]
public sealed class CrashCommandTests : IDisposable
{
    private readonly string _root;

    public CrashCommandTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "revitcli-crash-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task Analyze_JournalWithManagedException_ReturnsHighConfidenceFinding()
    {
        var journal = WriteJournal(
            "journal.0001.txt",
            """
            ' 0:< File Name C:\Models\office.rvt
            Jrn.Command "Ribbon" , "ID_FILE_OPEN"
            'C 31-May-2026 12:00:00.000; ExceptionCode=0xe0434352
            """);
        var output = new StringWriter();

        var exitCode = await CrashCommand.ExecuteAnalyzeAsync(
            2026,
            journal,
            "24h",
            includeEventLog: false,
            contextLines: 1,
            "json",
            output,
            generatedAtUtc: FixedNow());

        Assert.Equal(0, exitCode);
        using var json = JsonDocument.Parse(output.ToString());
        var root = json.RootElement;
        Assert.Equal("revit-crash-analysis.v1", root.GetProperty("schemaVersion").GetString());
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal("managed-dotnet-exception", root.GetProperty("summary").GetProperty("mostLikelyRuleId").GetString());
        Assert.Equal(@"C:\Models\office.rvt", root.GetProperty("summary").GetProperty("lastModelPath").GetString());
        var findings = root.GetProperty("findings").EnumerateArray().ToArray();
        Assert.Contains(findings, finding =>
            finding.GetProperty("ruleId").GetString() == "managed-dotnet-exception" &&
            finding.GetProperty("confidence").GetString() == "high" &&
            finding.GetProperty("suggestions").EnumerateArray().Any(suggestion =>
                suggestion.GetString()!.Contains("disable third-party add-ins", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task Analyze_JournalWithApiError_ReturnsAddinFinding()
    {
        var journal = WriteJournal(
            "journal.0002.txt",
            """
            Jrn.Command "Ribbon" , "External command"
            API_ERROR: ExternalCommand failed in Contoso.Addin
            Could not load file or assembly 'Contoso.RevitAddin, Version=1.0.0.0'
            """);
        var output = new StringWriter();

        var exitCode = await CrashCommand.ExecuteAnalyzeAsync(
            2026,
            journal,
            "24h",
            includeEventLog: false,
            contextLines: 1,
            "json",
            output,
            generatedAtUtc: FixedNow());

        Assert.Equal(0, exitCode);
        using var json = JsonDocument.Parse(output.ToString());
        var findings = json.RootElement.GetProperty("findings").EnumerateArray().ToArray();
        Assert.Contains(findings, finding =>
            finding.GetProperty("ruleId").GetString() == "addin-api-or-assembly-failure" &&
            finding.GetProperty("title").GetString()!.Contains("Add-in", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Analyze_EventLogWarning_DoesNotFailWhenJournalExists()
    {
        var journal = WriteJournal("journal.0003.txt", "Fatal error during model open");
        var output = new StringWriter();
        var eventLog = new FakeEventLogReader(issueCode: "event-log-unavailable");

        var exitCode = await CrashCommand.ExecuteAnalyzeAsync(
            2026,
            journal,
            "24h",
            includeEventLog: true,
            contextLines: 0,
            "json",
            output,
            eventLog,
            FixedNow());

        Assert.Equal(0, exitCode);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.True(json.RootElement.GetProperty("success").GetBoolean());
        Assert.Contains(json.RootElement.GetProperty("issues").EnumerateArray(), issue =>
            issue.GetProperty("code").GetString() == "event-log-unavailable" &&
            issue.GetProperty("severity").GetString() == "warning");
    }

    [Fact]
    public async Task Analyze_MissingExplicitJournal_ReturnsError()
    {
        var output = new StringWriter();
        var missing = Path.Combine(_root, "missing.txt");

        var exitCode = await CrashCommand.ExecuteAnalyzeAsync(
            2026,
            missing,
            "24h",
            includeEventLog: false,
            contextLines: 0,
            "json",
            output,
            generatedAtUtc: FixedNow());

        Assert.Equal(1, exitCode);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.False(json.RootElement.GetProperty("success").GetBoolean());
        Assert.Contains(json.RootElement.GetProperty("issues").EnumerateArray(), issue =>
            issue.GetProperty("code").GetString() == "journal-missing");
    }

    [Fact]
    public async Task Collect_CopiesJournalAndWritesAnalysis()
    {
        var journal = WriteJournal("journal.0004.txt", "ExceptionCode=0xe0434352");
        var outputDir = Path.Combine(_root, "packet");
        var output = new StringWriter();

        var exitCode = await CrashCommand.ExecuteCollectAsync(
            2026,
            journal,
            "24h",
            includeEventLog: false,
            contextLines: 0,
            outputDir,
            "json",
            output,
            generatedAtUtc: FixedNow());

        Assert.Equal(0, exitCode);
        using var json = JsonDocument.Parse(output.ToString());
        var root = json.RootElement;
        Assert.True(root.GetProperty("success").GetBoolean());
        var analysisPath = root.GetProperty("analysisPath").GetString();
        var redactionReportPath = root.GetProperty("redactionReportPath").GetString();
        Assert.True(File.Exists(analysisPath));
        Assert.True(File.Exists(redactionReportPath));
        Assert.True(File.Exists(Path.Combine(outputDir, "README.md")));
        Assert.True(Directory.EnumerateFiles(Path.Combine(outputDir, "journals")).Any());
        using var analysis = JsonDocument.Parse(File.ReadAllText(analysisPath!));
        Assert.Equal("revit-crash-analysis.v1", analysis.RootElement.GetProperty("schemaVersion").GetString());
        using var redaction = JsonDocument.Parse(File.ReadAllText(redactionReportPath!));
        Assert.Equal("revit-crash-redaction-report.v1", redaction.RootElement.GetProperty("schemaVersion").GetString());
        Assert.False(redaction.RootElement.GetProperty("redactionApplied").GetBoolean());
        Assert.True(redaction.RootElement.GetProperty("reviewRequiredBeforeExternalSharing").GetBoolean());
        Assert.True(File.Exists(Path.Combine(outputDir, "manifest.json")));

        var files = root.GetProperty("files").EnumerateArray().ToArray();
        Assert.Contains(files, file => file.GetProperty("kind").GetString() == "redaction");
    }

    [Fact]
    public async Task Repro_Json_EmitsCleanSessionChecklist()
    {
        var output = new StringWriter();

        var exitCode = await CrashCommand.ExecuteReproAsync(
            2026,
            "family-saveas",
            "json",
            output,
            FixedNow(),
            processProvider: () => new[]
            {
                new RevitCrashReproProcess
                {
                    ProcessId = 123,
                    ProcessName = "Revit",
                    StartTimeUtc = FixedNow().AddMinutes(-5),
                },
            });

        Assert.Equal(0, exitCode);
        using var json = JsonDocument.Parse(output.ToString());
        var root = json.RootElement;
        Assert.Equal("revit-crash-repro.v1", root.GetProperty("schemaVersion").GetString());
        Assert.Equal("family-saveas", root.GetProperty("caseName").GetString());
        Assert.Equal(1, root.GetProperty("revitProcessCount").GetInt32());
        Assert.Contains(root.GetProperty("checklist").EnumerateArray(), step =>
            step.GetProperty("stepId").GetString() == "close-revit");
        Assert.Contains(root.GetProperty("commands").EnumerateArray(), command =>
            command.GetString()!.Contains("crash collect", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Verify_CollectedPacketPassesThenFailsAfterTamper()
    {
        var journal = WriteJournal("journal.0005.txt", "ExceptionCode=0xe0434352");
        var outputDir = Path.Combine(_root, "packet-verify");
        var collectOutput = new StringWriter();
        Assert.Equal(0, await CrashCommand.ExecuteCollectAsync(
            2026,
            journal,
            "24h",
            includeEventLog: false,
            contextLines: 0,
            outputDir,
            "json",
            collectOutput,
            generatedAtUtc: FixedNow()));

        var verifyOutput = new StringWriter();
        var verifyExitCode = await CrashCommand.ExecuteVerifyAsync(
            outputDir,
            "json",
            verifyOutput);

        Assert.Equal(0, verifyExitCode);
        using (var json = JsonDocument.Parse(verifyOutput.ToString()))
        {
            var root = json.RootElement;
            Assert.Equal("revit-crash-packet-verify.v1", root.GetProperty("schemaVersion").GetString());
            Assert.True(root.GetProperty("success").GetBoolean());
            Assert.True(root.GetProperty("hasRedactionReport").GetBoolean());
            Assert.True(root.GetProperty("verifiedFileCount").GetInt32() >= 4);
        }

        File.AppendAllText(Path.Combine(outputDir, "analysis.json"), "tampered");
        var tamperedOutput = new StringWriter();
        var tamperedExitCode = await CrashCommand.ExecuteVerifyAsync(
            outputDir,
            "json",
            tamperedOutput);

        Assert.Equal(1, tamperedExitCode);
        using var tamperedJson = JsonDocument.Parse(tamperedOutput.ToString());
        Assert.False(tamperedJson.RootElement.GetProperty("success").GetBoolean());
        Assert.Contains(tamperedJson.RootElement.GetProperty("issues").EnumerateArray(), issue =>
            issue.GetProperty("code").GetString() == "file-hash-mismatch");
    }

    private string WriteJournal(string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private static DateTimeOffset FixedNow() =>
        new(2026, 5, 31, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeEventLogReader : IRevitEventLogReader
    {
        private readonly string? _issueCode;

        public FakeEventLogReader(string? issueCode = null)
        {
            _issueCode = issueCode;
        }

        public Task<RevitEventLogResult> ReadAsync(
            int revitYear,
            DateTimeOffset sinceUtc,
            CancellationToken cancellationToken = default)
        {
            var result = new RevitEventLogResult();
            if (!string.IsNullOrWhiteSpace(_issueCode))
            {
                result.Issues.Add(new RevitCrashIssue
                {
                    Severity = "warning",
                    Code = _issueCode,
                    Message = "test warning",
                });
            }

            return Task.FromResult(result);
        }
    }
}
