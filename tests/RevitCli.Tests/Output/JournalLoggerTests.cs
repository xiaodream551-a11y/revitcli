using System;
using System.IO;
using System.Text.Json;
using RevitCli.Output;
using Xunit;

namespace RevitCli.Tests.Output;

public class JournalLoggerTests
{
    [Fact]
    public void Log_WritesJsonlToCorrectPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"revitcli_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        JournalLogger.Log(dir, new { action = "set", param = "Mark", value = "TEST" });

        var journalPath = Path.Combine(dir, ".revitcli", "journal.jsonl");
        Assert.True(File.Exists(journalPath));

        var lines = File.ReadAllLines(journalPath);
        Assert.Single(lines);

        var doc = JsonDocument.Parse(lines[0]);
        Assert.Equal("set", doc.RootElement.GetProperty("action").GetString());
        Assert.Equal("Mark", doc.RootElement.GetProperty("param").GetString());

        Directory.Delete(dir, true);
    }

    [Fact]
    public void Log_AppendsMultipleEntries()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"revitcli_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        JournalLogger.Log(dir, new { action = "set", param = "A" });
        JournalLogger.Log(dir, new { action = "publish", pipeline = "default" });

        var journalPath = Path.Combine(dir, ".revitcli", "journal.jsonl");
        var lines = File.ReadAllLines(journalPath);
        Assert.Equal(2, lines.Length);

        Directory.Delete(dir, true);
    }

    [Fact]
    public void Log_DoesNotThrowOnInvalidPath()
    {
        // Should not throw even with an invalid directory
        var exception = Record.Exception(() =>
            JournalLogger.Log("/nonexistent/invalid/path", new { action = "test" }));
        Assert.Null(exception);
    }
}
