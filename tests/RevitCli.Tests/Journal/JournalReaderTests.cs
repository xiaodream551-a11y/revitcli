using System;
using System.IO;
using System.Linq;
using RevitCli.Journal;
using Xunit;

namespace RevitCli.Tests.Journal;

/// <summary>
/// Unit tests for <see cref="JournalReader"/>. The journal format is
/// append-only JSONL written by <c>JournalLogger</c>, so each test
/// constructs a temp file with hand-rolled lines covering: clean
/// records, malformed lines (must be skipped), filters, time windows,
/// and the relative-duration parser.
/// </summary>
public class JournalReaderTests : IDisposable
{
    private readonly string _tempPath;

    public JournalReaderTests()
    {
        _tempPath = Path.Combine(Path.GetTempPath(), $"revitcli-journal-test-{Guid.NewGuid():N}.jsonl");
    }

    public void Dispose()
    {
        try { if (File.Exists(_tempPath)) File.Delete(_tempPath); }
        catch (IOException) { }
    }

    [Fact]
    public void Read_NonexistentFile_ReturnsEmpty()
    {
        Assert.Empty(JournalReader.Read("/no/such/path.jsonl"));
    }

    [Fact]
    public void Read_TwoEntries_ReturnsNewestFirst()
    {
        File.WriteAllLines(_tempPath, new[]
        {
            """{"action":"set","outcome":"ok","timestamp":"2026-04-28T10:00:00Z","user":"alice"}""",
            """{"action":"fix","outcome":"ok","timestamp":"2026-04-28T11:00:00Z","user":"bob"}""",
        });

        var entries = JournalReader.Read(_tempPath);
        Assert.Equal(2, entries.Count);
        Assert.Equal("fix", entries[0].Action);
        Assert.Equal("set", entries[1].Action);
    }

    [Fact]
    public void Read_MalformedLine_SkippedNotThrows()
    {
        File.WriteAllLines(_tempPath, new[]
        {
            """{"action":"set","outcome":"ok","timestamp":"2026-04-28T10:00:00Z"}""",
            "not-valid-json{{",
            """{"action":"fix","outcome":"ok","timestamp":"2026-04-28T11:00:00Z"}""",
        });

        var entries = JournalReader.Read(_tempPath);
        Assert.Equal(2, entries.Count);
        Assert.Contains(entries, e => e.Action == "set");
        Assert.Contains(entries, e => e.Action == "fix");
    }

    [Fact]
    public void Read_ActionFilter_CaseInsensitive_NarrowsResults()
    {
        File.WriteAllLines(_tempPath, new[]
        {
            """{"action":"set","timestamp":"2026-04-28T10:00:00Z"}""",
            """{"action":"FIX","timestamp":"2026-04-28T11:00:00Z"}""",
            """{"action":"fix","timestamp":"2026-04-28T12:00:00Z"}""",
        });

        var entries = JournalReader.Read(_tempPath, actionFilter: "fix");
        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.Contains(e.Action, new[] { "FIX", "fix" }, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void Read_LimitCapsResults_StillNewestFirst()
    {
        File.WriteAllLines(_tempPath, new[]
        {
            """{"action":"a","timestamp":"2026-04-28T01:00:00Z"}""",
            """{"action":"b","timestamp":"2026-04-28T02:00:00Z"}""",
            """{"action":"c","timestamp":"2026-04-28T03:00:00Z"}""",
        });

        var entries = JournalReader.Read(_tempPath, limit: 2);
        Assert.Equal(2, entries.Count);
        Assert.Equal("c", entries[0].Action);
        Assert.Equal("b", entries[1].Action);
    }

    [Fact]
    public void Read_SinceFilter_DropsOlderEntries()
    {
        File.WriteAllLines(_tempPath, new[]
        {
            """{"action":"old","timestamp":"2026-04-28T10:00:00Z"}""",
            """{"action":"new","timestamp":"2026-04-28T20:00:00Z"}""",
        });

        var cutoff = new DateTime(2026, 4, 28, 15, 0, 0, DateTimeKind.Utc);
        var entries = JournalReader.Read(_tempPath, sinceUtc: cutoff);
        Assert.Single(entries);
        Assert.Equal("new", entries[0].Action);
    }

    [Fact]
    public void Read_EntriesWithoutTimestamp_NeverMatchSinceFilter()
    {
        // Defensive: if a line has no parseable timestamp, the since
        // filter has no signal to act on; per the reader it stays.
        // We DON'T want to silently drop such entries when --since is
        // applied — the operator should be able to tell that they
        // came through unfiltered.
        File.WriteAllLines(_tempPath, new[]
        {
            """{"action":"timeless"}""",
            """{"action":"new","timestamp":"2026-04-28T20:00:00Z"}""",
        });

        var cutoff = new DateTime(2026, 4, 28, 15, 0, 0, DateTimeKind.Utc);
        var entries = JournalReader.Read(_tempPath, sinceUtc: cutoff);
        // Both pass: timeless because no parseable timestamp; new because
        // its timestamp >= cutoff.
        Assert.Equal(2, entries.Count);
    }

    [Fact]
    public void Read_BlankLines_AreSkipped()
    {
        File.WriteAllLines(_tempPath, new[]
        {
            """{"action":"a","timestamp":"2026-04-28T10:00:00Z"}""",
            "",
            "   ",
            """{"action":"b","timestamp":"2026-04-28T11:00:00Z"}""",
        });

        var entries = JournalReader.Read(_tempPath);
        Assert.Equal(2, entries.Count);
    }

    [Theory]
    [InlineData("30m", -30, 0)]
    [InlineData("2h",  0, -2)]
    [InlineData("7d",  0, 0)]
    public void ResolveSinceUtc_ParsesValidDurations(string spec, int minutes, int hours)
    {
        var now = new DateTime(2026, 4, 28, 12, 0, 0, DateTimeKind.Utc);
        var result = JournalReader.ResolveSinceUtc(spec, now);
        Assert.NotNull(result);
        if (spec.EndsWith("d"))
        {
            // Days check
            var days = int.Parse(spec.Substring(0, spec.Length - 1));
            Assert.Equal(now.AddDays(-days), result);
        }
        else
        {
            Assert.Equal(now.AddMinutes(minutes).AddHours(hours), result);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("30")]      // missing suffix
    [InlineData("30x")]     // unknown suffix
    [InlineData("foo30m")]  // junk before number
    [InlineData("0m")]      // zero is rejected (not a "duration ago")
    [InlineData("-1h")]     // negatives are rejected
    public void ResolveSinceUtc_InvalidInput_ReturnsNull(string? spec)
    {
        Assert.Null(JournalReader.ResolveSinceUtc(spec, DateTime.UtcNow));
    }

    [Fact]
    public void Summarize_GroupsByAllDimensions()
    {
        File.WriteAllLines(_tempPath, new[]
        {
            """{"action":"set","outcome":"ok","transport":"cli","user":"alice"}""",
            """{"action":"set","outcome":"ok","transport":"mcp","user":"bob"}""",
            """{"action":"fix","outcome":"error","transport":"mcp","user":"alice"}""",
            """{"action":"set","outcome":"refused-no-confirm","transport":"mcp","user":"bob"}""",
        });

        var entries = JournalReader.Read(_tempPath);
        var stats = JournalReader.Summarize(entries);

        Assert.Equal(4, stats.Total);
        Assert.Equal(3, stats.ByAction["set"]);
        Assert.Equal(1, stats.ByAction["fix"]);
        Assert.Equal(2, stats.ByOutcome["ok"]);
        Assert.Equal(1, stats.ByOutcome["error"]);
        Assert.Equal(1, stats.ByOutcome["refused-no-confirm"]);
        Assert.Equal(3, stats.ByTransport["mcp"]);
        Assert.Equal(1, stats.ByTransport["cli"]);
        Assert.Equal(2, stats.ByUser["alice"]);
        Assert.Equal(2, stats.ByUser["bob"]);
    }

    [Fact]
    public void Summarize_HandlesMissingFields()
    {
        File.WriteAllLines(_tempPath, new[]
        {
            """{"action":"set"}""",
            """{"outcome":"ok"}""",
            """{}""",
        });

        var entries = JournalReader.Read(_tempPath);
        var stats = JournalReader.Summarize(entries);
        Assert.Equal(3, stats.Total);
        Assert.True(stats.ByAction.ContainsKey("(unknown)"));
        Assert.True(stats.ByOutcome.ContainsKey("(none)"));
    }
}
