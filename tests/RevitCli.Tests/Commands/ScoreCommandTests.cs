using System;
using System.CommandLine;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using RevitCli.Client;
using RevitCli.Commands;
using RevitCli.History;
using RevitCli.Shared;
using RevitCli.Tests.Client;
using Xunit;

namespace RevitCli.Tests.Commands;

[Collection("Sequential")]
public class ScoreCommandTests : IDisposable
{
    private readonly string _root;
    private readonly int _savedExitCode;
    private readonly TextWriter _savedOut;
    private readonly TextWriter _savedError;

    public ScoreCommandTests()
    {
        _savedExitCode = Environment.ExitCode;
        _savedOut = Console.Out;
        _savedError = Console.Error;
        Environment.ExitCode = 0;
        _root = Path.Combine(Path.GetTempPath(), "revitcli-score-history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        Console.SetOut(_savedOut);
        Console.SetError(_savedError);
        Environment.ExitCode = _savedExitCode;
    }

    private string HistoryDir => Path.Combine(_root, "history");

    private static (RevitClient Client, FakeHttpHandler Handler) CreateClientWithHandler()
    {
        var handler = new FakeHttpHandler(throwException: true);
        var client = new RevitClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:17839") });
        return (client, handler);
    }

    private static ModelSnapshot MakeSnapshot(int wallCount, int sheetCount = 2, int scheduleCount = 1)
    {
        var snap = new ModelSnapshot
        {
            SchemaVersion = 1,
            TakenAt = "2026-04-27T10:00:00Z",
            Revit = new SnapshotRevit { Version = "2026", Document = "Sample.rvt", DocumentPath = "C:/p/Sample.rvt" },
            Summary = new SnapshotSummary(),
        };
        snap.Categories["walls"] = new List<SnapshotElement>();
        for (var i = 0; i < wallCount; i++)
        {
            snap.Categories["walls"].Add(new SnapshotElement { Id = i + 1, Name = $"W{i + 1}" });
        }
        for (var i = 0; i < sheetCount; i++)
        {
            snap.Sheets.Add(new SnapshotSheet
            {
                Number = $"A{i + 1:D3}",
                Name = $"Sheet {i + 1}",
            });
        }
        for (var i = 0; i < scheduleCount; i++)
        {
            snap.Schedules.Add(new SnapshotSchedule
            {
                Id = 100 + i,
                Name = $"Schedule {i + 1}",
                RowCount = 10,
            });
        }
        return snap;
    }

    [Fact]
    public async Task History_NoStore_ReturnsOne()
    {
        var writer = new StringWriter();
        var exit = await ScoreCommand.ExecuteHistoryAsync("7d", HistoryDir, writer);
        Assert.Equal(1, exit);
        Assert.Contains("not initialised", writer.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task History_InvalidWindow_ReturnsOne()
    {
        var writer = new StringWriter();
        var exit = await ScoreCommand.ExecuteHistoryAsync("notaduration", HistoryDir, writer);
        Assert.Equal(1, exit);
        Assert.Contains("Invalid window", writer.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task History_EmptyStore_ReportsNoSnapshots()
    {
        var store = new HistoryStore(HistoryDir);
        await store.InitAsync();
        var writer = new StringWriter();
        var exit = await ScoreCommand.ExecuteHistoryAsync("7d", HistoryDir, writer);
        Assert.Equal(0, exit);
        Assert.Contains("No snapshots in window", writer.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task History_SevenDailySnapshots_ReturnsSevenRows()
    {
        var store = new HistoryStore(HistoryDir);
        await store.InitAsync();
        var now = DateTimeOffset.UtcNow;
        for (var d = 6; d >= 0; d--)
        {
            await store.AppendAsync(MakeSnapshot(2 + d), "manual",
                now - TimeSpan.FromDays(d) - TimeSpan.FromHours(1));
        }

        var writer = new StringWriter();
        var exit = await ScoreCommand.ExecuteHistoryAsync("7d", HistoryDir, writer);
        Assert.Equal(0, exit);

        var lines = writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Length > 0)
            .ToList();
        // 1 header + 7 rows.
        Assert.True(lines.Count >= 8, $"Expected at least 8 lines, got {lines.Count}: {string.Join(" | ", lines)}");
        Assert.Contains("date", lines[0]);
        Assert.Contains("score", lines[0]);
        Assert.Contains("letter", lines[0]);
        Assert.Contains("new", lines[0]);
        Assert.Contains("resolved", lines[0]);
        Assert.Contains("unchanged", lines[0]);
    }

    [Fact]
    public async Task History_Json_PrintsStableModelHealthEnvelope()
    {
        var store = new HistoryStore(HistoryDir);
        await store.InitAsync();
        await store.AppendAsync(MakeSnapshot(3), "manual", DateTimeOffset.UtcNow - TimeSpan.FromHours(1));

        var writer = new StringWriter();
        var exit = await ScoreCommand.ExecuteHistoryAsync("7d", HistoryDir, writer, "json");

        Assert.Equal(0, exit);
        using var document = JsonDocument.Parse(writer.ToString());
        var root = document.RootElement;
        Assert.Equal("model-health-history.v1", root.GetProperty("schemaVersion").GetString());
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal("7d", root.GetProperty("window").GetString());
        Assert.Equal(1, root.GetProperty("rowCount").GetInt32());

        var row = root.GetProperty("rows").EnumerateArray().Single();
        Assert.Equal(100, row.GetProperty("score").GetInt32());
        Assert.Equal("A", row.GetProperty("letter").GetString());
        Assert.Equal(3, row.GetProperty("unchangedCount").GetInt32());
    }

    [Fact]
    public async Task History_Markdown_PrintsHandoffTable()
    {
        var store = new HistoryStore(HistoryDir);
        await store.InitAsync();
        await store.AppendAsync(MakeSnapshot(2), "manual", DateTimeOffset.UtcNow - TimeSpan.FromHours(1));

        var writer = new StringWriter();
        var exit = await ScoreCommand.ExecuteHistoryAsync("7d", HistoryDir, writer, "markdown");

        var text = writer.ToString();
        Assert.Equal(0, exit);
        Assert.Contains("# Model Health History", text);
        Assert.Contains("Schema: `model-health-history.v1`", text);
        Assert.Contains("| Date | Score | Letter | New | Resolved | Unchanged |", text);
        Assert.Contains("| 100 | A |", text);
    }

    [Fact]
    public async Task Findings_Json_ScoresBimOpsFindingEnvelope()
    {
        var findingsPath = Path.Combine(_root, "findings.json");
        var envelope = new BimOpsFindingEnvelope
        {
            Source = "check",
            GeneratedAtUtc = DateTimeOffset.UtcNow.ToString("o"),
            RequiresRevit = true,
            Findings =
            {
                new BimOpsFinding
                {
                    RuleId = "check.room-bounds",
                    Severity = BimOpsFindingSchema.SeverityError,
                    Message = "Room has zero area",
                },
                new BimOpsFinding
                {
                    RuleId = "check.naming",
                    Severity = BimOpsFindingSchema.SeverityWarning,
                    Message = "Default name",
                },
                new BimOpsFinding
                {
                    RuleId = "check.orphan",
                    Severity = BimOpsFindingSchema.SeverityInfo,
                    Message = "Informational issue",
                },
            },
        };
        await File.WriteAllTextAsync(findingsPath, JsonSerializer.Serialize(envelope));
        var writer = new StringWriter();

        var exit = await ScoreCommand.ExecuteFindingsAsync(findingsPath, writer, "json");

        Assert.Equal(0, exit);
        using var document = JsonDocument.Parse(writer.ToString());
        var root = document.RootElement;
        Assert.Equal("model-health-finding-score.v1", root.GetProperty("schemaVersion").GetString());
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.False(root.GetProperty("requiresRevit").GetBoolean());
        Assert.Equal("check", root.GetProperty("findingsSource").GetString());
        Assert.Equal("none", root.GetProperty("failOn").GetString());
        Assert.Equal(0, root.GetProperty("exitCode").GetInt32());
        Assert.Equal(80, root.GetProperty("score").GetInt32());
        Assert.Equal("B", root.GetProperty("letter").GetString());
        Assert.Equal(3, root.GetProperty("findingCount").GetInt32());
        Assert.Equal(3, root.GetProperty("originalFindingCount").GetInt32());
        Assert.Equal(0, root.GetProperty("waivedCount").GetInt32());
        var summary = root.GetProperty("summary");
        Assert.Equal(1, summary.GetProperty("errors").GetInt32());
        Assert.Equal(1, summary.GetProperty("warnings").GetInt32());
        Assert.Equal(1, summary.GetProperty("info").GetInt32());
    }

    [Fact]
    public async Task Findings_MissingFile_ReturnsJsonError()
    {
        var writer = new StringWriter();

        var exit = await ScoreCommand.ExecuteFindingsAsync(
            Path.Combine(_root, "missing-findings.json"),
            writer,
            "json");

        Assert.Equal(3, exit);
        using var document = JsonDocument.Parse(writer.ToString());
        Assert.False(document.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(3, document.RootElement.GetProperty("exitCode").GetInt32());
        Assert.Contains("findings file not found", document.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Findings_WaiversSuppressActiveRuleAndIgnoreExpired()
    {
        var findingsPath = Path.Combine(_root, "waived-findings.json");
        var envelope = new BimOpsFindingEnvelope
        {
            Source = "check",
            GeneratedAtUtc = DateTimeOffset.UtcNow.ToString("o"),
            Findings =
            {
                new BimOpsFinding
                {
                    FindingId = "F-1",
                    RuleId = "check.room-bounds",
                    Severity = BimOpsFindingSchema.SeverityError,
                    Message = "Room has zero area",
                },
                new BimOpsFinding
                {
                    FindingId = "F-2",
                    RuleId = "check.naming",
                    Severity = BimOpsFindingSchema.SeverityWarning,
                    Message = "Default name",
                },
            },
        };
        await File.WriteAllTextAsync(findingsPath, JsonSerializer.Serialize(envelope));
        var waiversPath = Path.Combine(_root, "waivers.json");
        await File.WriteAllTextAsync(waiversPath, JsonSerializer.Serialize(new
        {
            schemaVersion = "bimops-waivers.v1",
            waivers = new object[]
            {
                new
                {
                    ruleId = "check.room-bounds",
                    expires = DateTimeOffset.UtcNow.AddDays(7).ToString("o"),
                    reason = "Legacy rooms accepted for this issue"
                },
                new
                {
                    ruleId = "check.naming",
                    expires = DateTimeOffset.UtcNow.AddDays(-1).ToString("o"),
                    reason = "Expired waiver"
                }
            }
        }));
        var writer = new StringWriter();

        var exit = await ScoreCommand.ExecuteFindingsAsync(findingsPath, writer, "json", "error", waiversPath);

        Assert.Equal(0, exit);
        using var document = JsonDocument.Parse(writer.ToString());
        var root = document.RootElement;
        Assert.Equal(95, root.GetProperty("score").GetInt32());
        Assert.Equal(0, root.GetProperty("exitCode").GetInt32());
        Assert.Equal(1, root.GetProperty("findingCount").GetInt32());
        Assert.Equal(2, root.GetProperty("originalFindingCount").GetInt32());
        Assert.Equal(1, root.GetProperty("waivedCount").GetInt32());
        Assert.Equal(1, root.GetProperty("activeWaiverCount").GetInt32());
        Assert.Equal(1, root.GetProperty("expiredWaiverCount").GetInt32());
        Assert.Equal(0, root.GetProperty("summary").GetProperty("errors").GetInt32());
        Assert.Equal(1, root.GetProperty("summary").GetProperty("warnings").GetInt32());
    }

    [Fact]
    public async Task Findings_FailOnError_ReturnsFailExitCode()
    {
        var findingsPath = Path.Combine(_root, "error-findings.json");
        var envelope = new BimOpsFindingEnvelope
        {
            Source = "check",
            GeneratedAtUtc = DateTimeOffset.UtcNow.ToString("o"),
            Findings =
            {
                new BimOpsFinding
                {
                    RuleId = "check.room-bounds",
                    Severity = BimOpsFindingSchema.SeverityError,
                    Message = "Room has zero area",
                },
            },
        };
        await File.WriteAllTextAsync(findingsPath, JsonSerializer.Serialize(envelope));
        var writer = new StringWriter();

        var exit = await ScoreCommand.ExecuteFindingsAsync(findingsPath, writer, "json", "error");

        Assert.Equal(2, exit);
        using var document = JsonDocument.Parse(writer.ToString());
        var root = document.RootElement;
        Assert.Equal("error", root.GetProperty("failOn").GetString());
        Assert.Equal(2, root.GetProperty("exitCode").GetInt32());
        Assert.Equal(85, root.GetProperty("score").GetInt32());
    }

    [Fact]
    public async Task Findings_FailOnWarning_ReturnsWarnExitCode()
    {
        var findingsPath = Path.Combine(_root, "warning-findings.json");
        var envelope = new BimOpsFindingEnvelope
        {
            Source = "check",
            GeneratedAtUtc = DateTimeOffset.UtcNow.ToString("o"),
            Findings =
            {
                new BimOpsFinding
                {
                    RuleId = "check.naming",
                    Severity = BimOpsFindingSchema.SeverityWarning,
                    Message = "Default name",
                },
            },
        };
        await File.WriteAllTextAsync(findingsPath, JsonSerializer.Serialize(envelope));
        var writer = new StringWriter();

        var exit = await ScoreCommand.ExecuteFindingsAsync(findingsPath, writer, "json", "warning");

        Assert.Equal(1, exit);
        using var document = JsonDocument.Parse(writer.ToString());
        var root = document.RootElement;
        Assert.Equal("warning", root.GetProperty("failOn").GetString());
        Assert.Equal(1, root.GetProperty("exitCode").GetInt32());
        Assert.Equal(95, root.GetProperty("score").GetInt32());
    }

    [Fact]
    public async Task Create_WaiversWithoutFindings_ReturnsToolErrorWithoutCallingRevit()
    {
        var waiversPath = Path.Combine(_root, "waivers.json");
        await File.WriteAllTextAsync(waiversPath, JsonSerializer.Serialize(new
        {
            schemaVersion = "bimops-waivers.v1",
            waivers = Array.Empty<object>()
        }));
        var (client, handler) = CreateClientWithHandler();
        var stdout = new StringWriter();
        Console.SetOut(stdout);

        await ScoreCommand.Create(client).InvokeAsync(new[] { "--waivers", waiversPath, "--output", "json" });

        Assert.Equal(3, Environment.ExitCode);
        Assert.Equal(0, handler.CallCount);
        using var document = JsonDocument.Parse(stdout.ToString());
        Assert.False(document.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(3, document.RootElement.GetProperty("exitCode").GetInt32());
        Assert.Contains("--waivers", document.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task History_UnknownOutput_ReturnsOneBeforeReadingStore()
    {
        var writer = new StringWriter();

        var exit = await ScoreCommand.ExecuteHistoryAsync("7d", HistoryDir, writer, "yaml");

        Assert.Equal(1, exit);
        Assert.Equal("Error: --output must be 'table', 'json', or 'markdown'." + Environment.NewLine, writer.ToString());
    }

    [Fact]
    public async Task History_PartialDayCoverage_GroupsByDateAndKeepsLatest()
    {
        var store = new HistoryStore(HistoryDir);
        await store.InitAsync();
        var todayMorning = DateTimeOffset.UtcNow.Date.AddHours(8);
        var todayAfternoon = DateTimeOffset.UtcNow.Date.AddHours(15);

        await store.AppendAsync(MakeSnapshot(2), "manual", new DateTimeOffset(todayMorning, TimeSpan.Zero));
        await store.AppendAsync(MakeSnapshot(5), "manual", new DateTimeOffset(todayAfternoon, TimeSpan.Zero));

        var writer = new StringWriter();
        var exit = await ScoreCommand.ExecuteHistoryAsync("7d", HistoryDir, writer);
        Assert.Equal(0, exit);

        var lines = writer.ToString().Split('\n')
            .Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
        // header + exactly one row (one day).
        Assert.Equal(2, lines.Count);
        // Latest snapshot of the day has 5 walls; previous-snapshot baseline starts as unchanged=5.
        Assert.Contains("5", lines[1]);
    }

    [Fact]
    public async Task History_ComputesNewAndResolvedBetweenDays()
    {
        var store = new HistoryStore(HistoryDir);
        await store.InitAsync();
        var dayOne = DateTimeOffset.UtcNow - TimeSpan.FromDays(2);
        var dayTwo = DateTimeOffset.UtcNow - TimeSpan.FromDays(1);

        // Day 1: walls 1..3
        var snapDay1 = MakeSnapshot(3);
        await store.AppendAsync(snapDay1, "manual", dayOne);

        // Day 2: walls 1, 2 only (id 3 removed) plus ids 4 and 5 added => new=2, resolved=1, unchanged=2
        var snapDay2 = new ModelSnapshot
        {
            SchemaVersion = 1,
            TakenAt = "2026-04-27T10:00:00Z",
            Revit = new SnapshotRevit { Version = "2026", Document = "Sample.rvt", DocumentPath = "C:/p/Sample.rvt" },
            Summary = new SnapshotSummary(),
        };
        snapDay2.Categories["walls"] = new List<SnapshotElement>
        {
            new() { Id = 1, Name = "W1" },
            new() { Id = 2, Name = "W2" },
            new() { Id = 4, Name = "W4" },
            new() { Id = 5, Name = "W5" },
        };
        snapDay2.Sheets.Add(new SnapshotSheet { Number = "A001", Name = "Plan" });
        snapDay2.Schedules.Add(new SnapshotSchedule { Id = 100, Name = "S1", RowCount = 5 });
        await store.AppendAsync(snapDay2, "manual", dayTwo);

        var writer = new StringWriter();
        var exit = await ScoreCommand.ExecuteHistoryAsync("7d", HistoryDir, writer);
        Assert.Equal(0, exit);

        var text = writer.ToString();
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
        // Header + 2 day rows.
        Assert.Equal(3, lines.Count);
        // The second row should encode new=2, resolved=1, unchanged=2 in some column order.
        var secondRow = lines[2];
        Assert.Matches(@"\b2\b", secondRow);
        Assert.Matches(@"\b1\b", secondRow);
    }

    [Fact]
    public async Task History_FixBaselinesAreHidden()
    {
        var store = new HistoryStore(HistoryDir);
        await store.InitAsync();
        var now = DateTimeOffset.UtcNow;

        await store.AppendAsync(MakeSnapshot(2), "fix-baseline", now - TimeSpan.FromDays(1));
        await store.AppendAsync(MakeSnapshot(3), "manual", now - TimeSpan.FromHours(2));

        var writer = new StringWriter();
        var exit = await ScoreCommand.ExecuteHistoryAsync("7d", HistoryDir, writer);
        Assert.Equal(0, exit);

        var lines = writer.ToString().Split('\n')
            .Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
        // header + 1 row (only the manual snapshot is included).
        Assert.Equal(2, lines.Count);
    }

    [Fact]
    public void SnapshotScore_EmptySnapshot_DefaultsToHundred()
    {
        var snap = new ModelSnapshot
        {
            SchemaVersion = 1,
            Revit = new SnapshotRevit { Version = "2026", DocumentPath = "C:/p/Sample.rvt" },
        };
        Assert.Equal(100.0, ScoreCommand.SnapshotScore(snap));
    }

    [Fact]
    public void SnapshotScore_PartialSheetNumbers_PenalisesScore()
    {
        var snap = MakeSnapshot(0, sheetCount: 0, scheduleCount: 0);
        snap.Sheets.Add(new SnapshotSheet { Number = "A001", Name = "Cover" });
        snap.Sheets.Add(new SnapshotSheet { Number = "", Name = "Bad" });
        var score = ScoreCommand.SnapshotScore(snap);
        Assert.NotNull(score);
        Assert.True(score < 100, $"expected < 100 but got {score}");
        Assert.True(score >= 0);
    }

    [Fact]
    public void SnapshotScore_NullSnapshot_ReturnsNull()
    {
        Assert.Null(ScoreCommand.SnapshotScore(null!));
    }
}
