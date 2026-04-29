using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RevitCli.Mcp.Resources;
using Xunit;

namespace RevitCli.Tests.Mcp;

/// <summary>
/// Tests for <see cref="JournalRecentResource"/> — the LLM-facing
/// view of the audit trail. The resource resolves the journal path
/// the same way the CLI does (via <c>JournalCommand.ResolveJournalPath</c>),
/// so these tests change cwd to a temp dir, write a journal there,
/// and assert the resource's JSON projection.
/// </summary>
[Collection("CwdMutating")]
public class McpJournalRecentResourceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _originalCwd;

    public McpJournalRecentResourceTests()
    {
        var setupDir = Path.Combine(Path.GetTempPath(), $"revitcli-mcp-journal-{Guid.NewGuid():N}");
        Directory.CreateDirectory(setupDir);
        _originalCwd = Environment.CurrentDirectory;
        Environment.CurrentDirectory = setupDir;
        _tempDir = Environment.CurrentDirectory; // canonical (macOS /private/var)
        Directory.CreateDirectory(Path.Combine(_tempDir, ".revitcli"));
    }

    public void Dispose()
    {
        Environment.CurrentDirectory = _originalCwd;
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task Read_NoJournal_ReturnsEmptyEntriesArray()
    {
        var resource = new JournalRecentResource();
        var json = await resource.ReadAsync(CancellationToken.None);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(0, doc.RootElement.GetProperty("count").GetInt32());
        Assert.Equal(0, doc.RootElement.GetProperty("entries").GetArrayLength());
        // The path field is non-empty even when the file doesn't exist —
        // it tells the LLM where to look (or where to expect entries to
        // start landing once writes happen).
        Assert.NotEmpty(doc.RootElement.GetProperty("journalPath").GetString()!);
    }

    [Fact]
    public async Task Read_ProjectsEntriesNewestFirst_AndPreservesRawFields()
    {
        var journalPath = Path.Combine(_tempDir, ".revitcli", "journal.jsonl");
        File.WriteAllLines(journalPath, new[]
        {
            """{"action":"set","outcome":"ok","timestamp":"2026-04-28T10:00:00Z","user":"alice","param":"Mark","value":"D-1"}""",
            """{"action":"fix","outcome":"ok","timestamp":"2026-04-28T11:00:00Z","user":"bob","ruleCount":3}""",
        });

        var resource = new JournalRecentResource();
        var json = await resource.ReadAsync(CancellationToken.None);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(2, doc.RootElement.GetProperty("count").GetInt32());

        var entries = doc.RootElement.GetProperty("entries").EnumerateArray().ToList();
        // Newest first — matching JournalReader's ordering.
        Assert.Equal("fix", entries[0].GetProperty("action").GetString());
        Assert.Equal(3, entries[0].GetProperty("ruleCount").GetInt32());
        Assert.Equal("set", entries[1].GetProperty("action").GetString());
        // Raw passthrough: tool-specific fields survive (param/value).
        Assert.Equal("Mark", entries[1].GetProperty("param").GetString());
        Assert.Equal("D-1", entries[1].GetProperty("value").GetString());
    }

    [Fact]
    public async Task Read_HonorsBuiltInLimit_DoesNotExposeAllEntries()
    {
        // Write more than the default limit so we can verify the cap.
        var journalPath = Path.Combine(_tempDir, ".revitcli", "journal.jsonl");
        var lines = Enumerable.Range(1, JournalRecentResource.DefaultLimit + 5)
            .Select(i => $"{{\"action\":\"set\",\"timestamp\":\"2026-04-28T10:00:{i % 60:D2}Z\"}}")
            .ToArray();
        File.WriteAllLines(journalPath, lines);

        var resource = new JournalRecentResource();
        var json = await resource.ReadAsync(CancellationToken.None);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(JournalRecentResource.DefaultLimit, doc.RootElement.GetProperty("count").GetInt32());
        Assert.Equal(JournalRecentResource.DefaultLimit, doc.RootElement.GetProperty("limit").GetInt32());
    }

    [Fact]
    public void StaticContract()
    {
        var resource = new JournalRecentResource();
        Assert.Equal("revitcli://journal/recent", resource.Uri);
        Assert.Equal("application/json", resource.MimeType);
        Assert.Contains("journal", resource.Description, StringComparison.OrdinalIgnoreCase);
        // The default limit is intentionally exposed as a constant so
        // the description and the test stay in sync.
        Assert.Contains(JournalRecentResource.DefaultLimit.ToString(), resource.Description);
    }
}
