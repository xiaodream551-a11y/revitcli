using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using RevitCli.Client;
using RevitCli.Commands;
using RevitCli.Shared;
using RevitCli.Tests.Client;
using Xunit;

namespace RevitCli.Tests.Commands;

public class SnapshotCommandTests
{
    private static RevitClient MakeClient(ModelSnapshot snap, out FakeHttpHandler handler)
    {
        var response = ApiResponse<ModelSnapshot>.Ok(snap);
        handler = new FakeHttpHandler(JsonSerializer.Serialize(response));
        return new RevitClient(new HttpClient(handler) { BaseAddress = new System.Uri("http://localhost:17839") });
    }

    private static ModelSnapshot MakeFixture()
    {
        return new ModelSnapshot
        {
            SchemaVersion = 1,
            TakenAt = "2026-04-23T10:00:00Z",
            Revit = new SnapshotRevit { Version = "2026", Document = "test", DocumentPath = "/a.rvt" },
            Categories = new Dictionary<string, List<SnapshotElement>>
            {
                ["walls"] = new() { new SnapshotElement { Id = 1, Name = "W1", Hash = "h1" } }
            },
            Summary = new SnapshotSummary
            {
                ElementCounts = new() { ["walls"] = 1 }, SheetCount = 0, ScheduleCount = 0
            }
        };
    }

    [Fact]
    public async Task Snapshot_ToStdout_WritesJson()
    {
        var client = MakeClient(MakeFixture(), out _);
        var writer = new StringWriter();

        var exitCode = await SnapshotCommand.ExecuteAsync(
            client, outputPath: null, categories: null,
            includeSheets: true, includeSchedules: true, summaryOnly: false, writer);

        Assert.Equal(0, exitCode);
        var parsed = JsonSerializer.Deserialize<ModelSnapshot>(
            writer.ToString(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal(1, parsed.SchemaVersion);
        Assert.Equal("2026", parsed.Revit.Version);
    }

    [Fact]
    public async Task Snapshot_ToFile_WritesJsonAndPrintsPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "revitcli-test-snap-" + System.Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "snap.json");
        var client = MakeClient(MakeFixture(), out _);
        var writer = new StringWriter();

        var exitCode = await SnapshotCommand.ExecuteAsync(
            client, outputPath: path, categories: null,
            includeSheets: true, includeSchedules: true, summaryOnly: false, writer);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(path));
        Assert.Contains(path, writer.ToString());

        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public async Task Snapshot_CategoriesOption_PassesFilter()
    {
        var client = MakeClient(MakeFixture(), out var handler);
        var writer = new StringWriter();

        var exitCode = await SnapshotCommand.ExecuteAsync(
            client, outputPath: null, categories: "walls,doors",
            includeSheets: true, includeSchedules: true, summaryOnly: false, writer);

        Assert.Equal(0, exitCode);
        Assert.Contains("walls", handler.LastRequestBody, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("doors", handler.LastRequestBody, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Snapshot_NoSheetsFlag_SetsIncludeSheetsFalse()
    {
        var client = MakeClient(MakeFixture(), out var handler);
        var writer = new StringWriter();

        await SnapshotCommand.ExecuteAsync(
            client, outputPath: null, categories: null,
            includeSheets: false, includeSchedules: true, summaryOnly: false, writer);

        Assert.Contains("\"includeSheets\":false", handler.LastRequestBody);
    }

    [Fact]
    public async Task Snapshot_ClientFails_ReturnsOne()
    {
        var handler = new FakeHttpHandler(throwException: true);
        var client = new RevitClient(new HttpClient(handler) { BaseAddress = new System.Uri("http://localhost:17839") });
        var writer = new StringWriter();

        var exitCode = await SnapshotCommand.ExecuteAsync(
            client, outputPath: null, categories: null,
            includeSheets: true, includeSchedules: true, summaryOnly: false, writer);

        Assert.Equal(1, exitCode);
        Assert.Contains("error", writer.ToString().ToLower());
    }
}
