using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using RevitCli.Commands;
using Xunit;

namespace RevitCli.Tests.Commands;

/// <summary>
/// CLI surface tests for <see cref="JournalCommand"/>. Each test
/// writes a temp jsonl file, runs the command pointing at it via
/// <c>--path</c>, and asserts on the captured output text + exit
/// code. The reader unit is covered separately by
/// <c>JournalReaderTests</c>; here we focus on argument parsing,
/// output formatting, and the json roundtrip.
/// </summary>
public class JournalCommandTests : IDisposable
{
    private readonly string _tempPath;

    public JournalCommandTests()
    {
        _tempPath = Path.Combine(Path.GetTempPath(), $"revitcli-journal-cmd-test-{Guid.NewGuid():N}.jsonl");
    }

    public void Dispose()
    {
        try { if (File.Exists(_tempPath)) File.Delete(_tempPath); }
        catch (IOException) { }
    }

    [Fact]
    public async Task Tail_NoFile_ReportsNoEntriesAndExitsZero()
    {
        var writer = new StringWriter();
        var exit = await JournalCommand.ExecuteTailAsync(20, null, null, "table", _tempPath, writer);
        Assert.Equal(0, exit);
        Assert.Contains("No matching entries", writer.ToString());
    }

    [Fact]
    public async Task Tail_RendersTableHeadersAndUserColumn()
    {
        File.WriteAllLines(_tempPath, new[]
        {
            """{"action":"set","outcome":"ok","transport":"cli","timestamp":"2026-04-28T10:00:00Z","user":"alice"}""",
        });

        var writer = new StringWriter();
        var exit = await JournalCommand.ExecuteTailAsync(20, null, null, "table", _tempPath, writer);

        Assert.Equal(0, exit);
        var output = writer.ToString();
        Assert.Contains("Timestamp", output);
        Assert.Contains("Action", output);
        Assert.Contains("alice", output);
        Assert.Contains("set", output);
    }

    [Fact]
    public async Task Tail_JsonOutput_RoundtripsRawFields()
    {
        // The tool-specific `param` field must survive the read +
        // serialize. If it didn't, an LLM consuming the journal via
        // `--output json` would lose attribution.
        File.WriteAllLines(_tempPath, new[]
        {
            """{"action":"set","outcome":"ok","transport":"cli","timestamp":"2026-04-28T10:00:00Z","param":"Mark","value":"D-1"}""",
        });

        var writer = new StringWriter();
        await JournalCommand.ExecuteTailAsync(20, null, null, "json", _tempPath, writer);

        using var doc = JsonDocument.Parse(writer.ToString());
        Assert.Equal(1, doc.RootElement.GetArrayLength());
        var first = doc.RootElement[0];
        Assert.Equal("set", first.GetProperty("action").GetString());
        Assert.Equal("Mark", first.GetProperty("param").GetString());
        Assert.Equal("D-1", first.GetProperty("value").GetString());
    }

    [Fact]
    public async Task Tail_ActionFilter_NarrowsResults()
    {
        File.WriteAllLines(_tempPath, new[]
        {
            """{"action":"set","timestamp":"2026-04-28T10:00:00Z"}""",
            """{"action":"fix","timestamp":"2026-04-28T11:00:00Z"}""",
        });

        var writer = new StringWriter();
        await JournalCommand.ExecuteTailAsync(20, "fix", null, "table", _tempPath, writer);
        var output = writer.ToString();
        Assert.Contains("fix", output);
        Assert.DoesNotContain("set", output[..output.IndexOf("entry/entries")]);
    }

    [Fact]
    public async Task Tail_InvalidSince_ReturnsOneAndSurfacesDiagnostic()
    {
        var writer = new StringWriter();
        var exit = await JournalCommand.ExecuteTailAsync(20, null, "junk", "table", _tempPath, writer);
        Assert.Equal(1, exit);
        Assert.Contains("--since must be of the form", writer.ToString());
    }

    [Fact]
    public async Task Tail_LimitTruncatesOutput()
    {
        File.WriteAllLines(_tempPath, new[]
        {
            """{"action":"a","timestamp":"2026-04-28T10:00:00Z"}""",
            """{"action":"b","timestamp":"2026-04-28T11:00:00Z"}""",
            """{"action":"c","timestamp":"2026-04-28T12:00:00Z"}""",
        });

        var writer = new StringWriter();
        await JournalCommand.ExecuteTailAsync(2, null, null, "table", _tempPath, writer);
        var output = writer.ToString();
        Assert.Contains("c", output);
        Assert.Contains("b", output);
        // 'a' is the oldest; with limit=2 (newest first), 'a' must be cut.
        Assert.DoesNotContain(" a ", output);
    }

    [Fact]
    public async Task Stats_NoEntries_RendersZerosAcrossBuckets()
    {
        var writer = new StringWriter();
        var exit = await JournalCommand.ExecuteStatsAsync("0", "table", _tempPath, writer);
        Assert.Equal(0, exit);
        var output = writer.ToString();
        Assert.Contains("Total entries: 0", output);
        Assert.Contains("By action", output);
    }

    [Fact]
    public async Task Stats_AggregatesAllDimensions()
    {
        File.WriteAllLines(_tempPath, new[]
        {
            """{"action":"set","outcome":"ok","transport":"cli","user":"alice","timestamp":"2026-04-28T10:00:00Z"}""",
            """{"action":"set","outcome":"refused-no-confirm","transport":"mcp","user":"bob","timestamp":"2026-04-28T11:00:00Z"}""",
            """{"action":"fix","outcome":"ok","transport":"mcp","user":"alice","timestamp":"2026-04-28T12:00:00Z"}""",
        });

        var writer = new StringWriter();
        await JournalCommand.ExecuteStatsAsync("0", "table", _tempPath, writer);
        var output = writer.ToString();
        Assert.Contains("Total entries: 3", output);
        // Action bucket header + counts
        Assert.Contains("By action", output);
        Assert.Contains("set", output);
        Assert.Contains("fix", output);
        // Outcome bucket
        Assert.Contains("refused-no-confirm", output);
    }

    [Fact]
    public async Task Stats_JsonOutput_IsParseable()
    {
        File.WriteAllLines(_tempPath, new[]
        {
            """{"action":"set","outcome":"ok","timestamp":"2026-04-28T10:00:00Z"}""",
        });

        var writer = new StringWriter();
        await JournalCommand.ExecuteStatsAsync("0", "json", _tempPath, writer);

        using var doc = JsonDocument.Parse(writer.ToString());
        Assert.Equal(1, doc.RootElement.GetProperty("total").GetInt32());
        Assert.True(doc.RootElement.GetProperty("byAction").GetProperty("set").GetInt32() == 1);
    }

    [Fact]
    public async Task Stats_InvalidSince_ReturnsOneAndDiagnostic()
    {
        var writer = new StringWriter();
        var exit = await JournalCommand.ExecuteStatsAsync("junk", "table", _tempPath, writer);
        Assert.Equal(1, exit);
        Assert.Contains("--since must be of the form", writer.ToString());
    }
}
