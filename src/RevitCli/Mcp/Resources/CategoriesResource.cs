using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using RevitCli.Client;
using RevitCli.Shared;

namespace RevitCli.Mcp.Resources;

/// <summary>
/// <c>revitcli://categories</c> — read-only list of Revit categories
/// present in the active document, with element counts. Sized for an
/// LLM cold-start: when an agent connects without prior context, it
/// can list this resource to learn which categories exist BEFORE
/// composing a <c>query</c> or <c>set</c> call.
///
/// Implementation note: derives the projection from a summary-only
/// snapshot. That keeps the read cheap (no per-element payload) and
/// reuses the existing <c>/api/snapshot?summaryOnly=true</c> path.
/// Categories are sorted by <c>elementCount</c> descending so the
/// dominant ones appear first — handy when the LLM has a token budget
/// for what it ingests.
/// </summary>
internal sealed class CategoriesResource : IMcpResource
{
    private static readonly JsonSerializerOptions PrettyJson = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly RevitClient _client;

    public CategoriesResource(RevitClient client)
    {
        _client = client;
    }

    public string Uri => "revitcli://categories";

    public string Name => "Revit categories (current document)";

    public string Description =>
        "List of element categories present in the active Revit document, " +
        "with per-category element counts. Use this to discover what's in " +
        "the model before composing query/set/import calls.";

    public string MimeType => "application/json";

    public async Task<string> ReadAsync(CancellationToken cancellationToken)
    {
        var result = await _client.CaptureSnapshotAsync(new SnapshotRequest { SummaryOnly = true });
        if (!result.Success)
            throw new InvalidOperationException(
                $"Failed to capture snapshot: {result.Error ?? "unknown error"}");

        var data = result.Data!;

        // Prefer Summary.ElementCounts (kept across schema revisions and
        // present even on summary-only snapshots). Fall back to the
        // Categories dictionary if Summary is empty for some reason.
        IEnumerable<KeyValuePair<string, int>> source =
            data.Summary?.ElementCounts is { Count: > 0 } counts
                ? counts
                : data.Categories.ToDictionary(kvp => kvp.Key, kvp => kvp.Value?.Count ?? 0);

        var categories = source
            .Where(kvp => kvp.Value > 0)
            .OrderByDescending(kvp => kvp.Value)
            .ThenBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kvp => new CategoryEntry { Name = kvp.Key, ElementCount = kvp.Value })
            .ToList();

        var payload = new CategoriesPayload
        {
            DocumentName = data.Revit?.Document,
            CapturedAt = data.TakenAt,
            CategoryCount = categories.Count,
            TotalElements = categories.Sum(c => c.ElementCount),
            Categories = categories,
        };

        return JsonSerializer.Serialize(payload, PrettyJson);
    }

    /// <summary>Schema for the JSON returned by <see cref="ReadAsync"/>.</summary>
    private sealed class CategoriesPayload
    {
        [JsonPropertyName("documentName")]
        public string? DocumentName { get; set; }

        [JsonPropertyName("capturedAt")]
        public string? CapturedAt { get; set; }

        [JsonPropertyName("categoryCount")]
        public int CategoryCount { get; set; }

        [JsonPropertyName("totalElements")]
        public int TotalElements { get; set; }

        [JsonPropertyName("categories")]
        public List<CategoryEntry> Categories { get; set; } = new();
    }

    private sealed class CategoryEntry
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("elementCount")]
        public int ElementCount { get; set; }
    }
}
