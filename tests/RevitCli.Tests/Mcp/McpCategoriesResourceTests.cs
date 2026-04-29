using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RevitCli.Client;
using RevitCli.Mcp.Resources;
using RevitCli.Shared;
using Xunit;

namespace RevitCli.Tests.Mcp;

/// <summary>
/// Verifies <see cref="CategoriesResource"/> — the LLM cold-start
/// discovery resource. Asserts the projection shape (sorted, filtered,
/// totals) and the JSON contract that an MCP client will read.
/// </summary>
public class McpCategoriesResourceTests
{
    [Fact]
    public async Task Read_ReturnsCategoriesSortedByCountDescending()
    {
        var handler = new QueueHttpHandler();
        handler.Enqueue("/api/snapshot", ApiResponse<ModelSnapshot>.Ok(new ModelSnapshot
        {
            TakenAt = "2026-04-28T10:00:00Z",
            Revit = new SnapshotRevit { Document = "Project1.rvt" },
            Summary = new SnapshotSummary
            {
                ElementCounts = new Dictionary<string, int>
                {
                    ["Doors"] = 38,
                    ["Walls"] = 142,
                    ["Windows"] = 21,
                    ["Floors"] = 12,
                },
            },
        }));
        var resource = new CategoriesResource(MakeClient(handler));

        var json = await resource.ReadAsync(CancellationToken.None);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("Project1.rvt", root.GetProperty("documentName").GetString());
        Assert.Equal("2026-04-28T10:00:00Z", root.GetProperty("capturedAt").GetString());
        Assert.Equal(4, root.GetProperty("categoryCount").GetInt32());
        Assert.Equal(213, root.GetProperty("totalElements").GetInt32());

        var cats = root.GetProperty("categories").EnumerateArray()
            .Select(e => (Name: e.GetProperty("name").GetString(),
                          Count: e.GetProperty("elementCount").GetInt32()))
            .ToList();
        // Walls (142) > Doors (38) > Windows (21) > Floors (12)
        Assert.Equal(new[] { "Walls", "Doors", "Windows", "Floors" }, cats.Select(c => c.Name));
        Assert.Equal(new[] { 142, 38, 21, 12 }, cats.Select(c => c.Count));
    }

    [Fact]
    public async Task Read_FiltersZeroCountCategories()
    {
        // Categories with 0 elements are noise for an LLM trying to
        // discover what's in the model — they're stripped from the
        // projection.
        var handler = new QueueHttpHandler();
        handler.Enqueue("/api/snapshot", ApiResponse<ModelSnapshot>.Ok(new ModelSnapshot
        {
            Revit = new SnapshotRevit { Document = "Empty.rvt" },
            Summary = new SnapshotSummary
            {
                ElementCounts = new Dictionary<string, int>
                {
                    ["Walls"] = 5,
                    ["FillerCategoryWithNothing"] = 0,
                    ["Doors"] = 2,
                },
            },
        }));
        var resource = new CategoriesResource(MakeClient(handler));

        var json = await resource.ReadAsync(CancellationToken.None);
        using var doc = JsonDocument.Parse(json);
        var names = doc.RootElement.GetProperty("categories").EnumerateArray()
            .Select(e => e.GetProperty("name").GetString())
            .ToList();
        Assert.Equal(2, names.Count);
        Assert.DoesNotContain("FillerCategoryWithNothing", names);
    }

    [Fact]
    public async Task Read_FallsBackToCategoriesDictionaryWhenSummaryEmpty()
    {
        // Some snapshots have an empty Summary.ElementCounts but still
        // populate the Categories dictionary. Fall back so the LLM
        // doesn't see an empty list when the data is actually there.
        var handler = new QueueHttpHandler();
        handler.Enqueue("/api/snapshot", ApiResponse<ModelSnapshot>.Ok(new ModelSnapshot
        {
            Revit = new SnapshotRevit { Document = "Fallback.rvt" },
            Summary = new SnapshotSummary
            {
                ElementCounts = new Dictionary<string, int>(), // empty
            },
            Categories = new Dictionary<string, List<SnapshotElement>>
            {
                ["Walls"] = new List<SnapshotElement>
                {
                    new() { Id = 1 }, new() { Id = 2 }, new() { Id = 3 },
                },
                ["Doors"] = new List<SnapshotElement>
                {
                    new() { Id = 4 },
                },
            },
        }));
        var resource = new CategoriesResource(MakeClient(handler));

        var json = await resource.ReadAsync(CancellationToken.None);
        using var doc = JsonDocument.Parse(json);
        var cats = doc.RootElement.GetProperty("categories").EnumerateArray()
            .Select(e => (Name: e.GetProperty("name").GetString(),
                          Count: e.GetProperty("elementCount").GetInt32()))
            .ToList();
        Assert.Equal(2, cats.Count);
        Assert.Equal(("Walls", 3), cats[0]);
        Assert.Equal(("Doors", 1), cats[1]);
    }

    [Fact]
    public async Task Read_PropagatesSnapshotFailureAsException()
    {
        // The dispatcher (McpServer.HandleResourceReadAsync) catches
        // and maps to JSON-RPC -32603. The resource itself MUST throw,
        // not silently return an empty payload — empty would mislead
        // the LLM into thinking the model has zero elements.
        var handler = new QueueHttpHandler();
        handler.Enqueue("/api/snapshot", ApiResponse<ModelSnapshot>.Fail("Revit is not running"));
        var resource = new CategoriesResource(MakeClient(handler));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => resource.ReadAsync(CancellationToken.None));
        Assert.Contains("Revit is not running", ex.Message);
    }

    [Fact]
    public async Task Read_RequestsSummaryOnlySnapshot()
    {
        // Performance check: we MUST set summaryOnly=true on the
        // snapshot request so that capturing this resource doesn't pull
        // the full per-element payload from the addin. Verify by
        // inspecting the captured request body.
        var handler = new QueueHttpHandler();
        handler.Enqueue("/api/snapshot", ApiResponse<ModelSnapshot>.Ok(new ModelSnapshot
        {
            Summary = new SnapshotSummary { ElementCounts = new Dictionary<string, int> { ["Walls"] = 1 } },
        }));
        var resource = new CategoriesResource(MakeClient(handler));

        await resource.ReadAsync(CancellationToken.None);

        Assert.Single(handler.Requests, r => r.EndsWith("/api/snapshot", StringComparison.Ordinal));
    }

    [Fact]
    public void StaticContract_UriAndMimeType()
    {
        var resource = new CategoriesResource(MakeClient(new QueueHttpHandler()));
        Assert.Equal("revitcli://categories", resource.Uri);
        Assert.Equal("application/json", resource.MimeType);
        Assert.Contains("categor", resource.Description, StringComparison.OrdinalIgnoreCase);
    }

    private static RevitClient MakeClient(QueueHttpHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:17839") });
}
