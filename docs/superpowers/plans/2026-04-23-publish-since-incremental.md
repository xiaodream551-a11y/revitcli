# Publish --Since Incremental Implementation Plan (Model-as-Code Phase 2)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship v1.2.0 — add `revitcli publish --since SNAPSHOT` to re-export only sheets whose content changed since a baseline, plus fill `SnapshotSheet.ContentHash` so content-level sheet diff works.

**Architecture:** `PublishCommand.ExecuteAsync` loads a baseline snapshot, captures a fresh snapshot via the existing `/api/snapshot` endpoint, runs `SnapshotDiffer.Diff` in a new `Content | Meta` mode to produce a changed-sheet set, and narrows each export preset's sheet selector to that set. On successful publish, `BaselineManager` atomically writes the fresh snapshot back to the baseline path (opt-in via CLI flag or profile `incremental: true`).

**Tech Stack:** .NET 8 / netstandard2.0 / xUnit / YamlDotNet 16.x / existing P1 infrastructure (SnapshotHasher, SnapshotDiffer, RealRevitOperations.CaptureSnapshotAsync, SnapshotController). **Zero new external dependencies.**

**Phase 1 dependency:** all P1 primitives are already on `main` as of v1.1.0 (merge commit `408902f`). This plan builds on top — no P1 re-work.

---

## Key Decisions (resolving the spec's open questions)

### Q1: Schema compatibility across P1/P2 snapshots

**Decision:** Keep `schemaVersion=1`. Treat empty `ContentHash` as "meta-mode fallback for this sheet."

Concretely:
- A P1 baseline (written by v1.1.0) has every `SnapshotSheet.ContentHash = ""`. A v1.2.0 diff of that baseline against a fresh v1.2.0 snapshot will silently fall back to comparing `MetaHash` for any sheet where either side is empty — the existing P1 sheet-diff semantics still work unchanged.
- A v1.2.0 baseline populates `ContentHash`. Subsequent v1.2.0 → v1.2.0 diffs use content-level precision.
- No forced rebuild of existing baselines.

**Why not bump to v2:** would strand v1.1.0 baselines on disk with a hard error, and "regenerate your baseline" is worse UX than gradual fallback. The `schemaVersion` field is reserved for changes that truly break readers (renaming fields, changing types) — adding a field that's already present but empty in v1 does not qualify.

### Q2: ContentHash performance budget

**Decision:** Accept the compute cost. Target budget for a 100-sheet × 5-placed-views × 100-elements project:

- ~500 `FilteredElementCollector(doc, viewId)` calls (one per placed view)
- ~50,000 element-hash lookups — **O(1) dictionary lookups** against the already-computed `snapshot.Categories` element-hash index. No element hash is recomputed.
- Expected wall-clock: < 30s on a typical project

**No progress signal in P2.** Measured time on the user's real 20-element / 16-schedule model in Task 7 E2E will be documented. If a future user reports >30s, a stderr progress ticker is a trivial follow-up; P2 ships without one to avoid scope creep.

### Q3: "Visible elements" precise definition

**Decision:** Don't honor Visibility/Graphics overrides. `FilteredElementCollector(doc, viewId)` returns all non-type elements in view scope regardless of V/G hidden state.

**Trade-off accepted:** a sheet where the user hides a wall via V/G will not change its `ContentHash` and won't be flagged for re-export. This is a conscious limitation: "incremental publish" answers "which sheets need re-export because *data* changed," not "which sheets look different due to V/G tweaks." Honoring V/G would require a per-element `element.IsHidden(view)` API call — 10x the cost — for a niche case.

Documented via an XML doc comment on `SnapshotSheet.ContentHash` so users know the contract.

---

## File Structure

### New files

```
src/RevitCli/Output/BaselineManager.cs                (~60 lines) — load/save baseline snapshot with atomic write
src/RevitCli/Output/SinceMode.cs                      (~10 lines) — enum Content | Meta
tests/RevitCli.Tests/Profile/PublishPipelineTests.cs  (~80 lines) — YAML deserialization of new fields
tests/RevitCli.Tests/Output/SinceModeTests.cs         (~120 lines) — differ sheet diff in content/meta/mixed-fallback modes
tests/RevitCli.Tests/Output/BaselineManagerTests.cs   (~100 lines) — load/save round-trip + missing-file + corruption cases
tests/RevitCli.Tests/Commands/PublishSinceTests.cs    (~150 lines) — publish --since flag parsing, sheet filtering, baseline update
```

### Modified files

```
src/RevitCli/Profile/ProjectProfile.cs                + PublishPipeline.Incremental / BaselinePath / SinceMode
shared/RevitCli.Shared/SnapshotHasher.cs              + HashSheetContent helper
src/RevitCli.Addin/Services/RealRevitOperations.cs    + populate SnapshotSheet.ContentHash
src/RevitCli/Output/SnapshotDiffer.cs                 + Diff overload with SinceMode parameter
src/RevitCli/Commands/PublishCommand.cs               + --since / --since-mode / --update-baseline flags + incremental filtering
src/RevitCli/Commands/CompletionsCommand.cs           + new publish options in bash/zsh/pwsh scripts
shared/RevitCli.Shared/ModelSnapshot.cs               + XML doc comment on SnapshotSheet.ContentHash (no signature change)
tests/RevitCli.Tests/Commands/CompletionsCommandTests.cs  (update hardcoded assertion if options appear inline)
src/RevitCli/RevitCli.csproj                          + Version 1.2.0
CHANGELOG.md                                          + v1.2.0 entry
```

---

## Task 1: Extend PublishPipeline profile schema

**Files:**
- Modify: `src/RevitCli/Profile/ProjectProfile.cs`
- Create: `tests/RevitCli.Tests/Profile/PublishPipelineTests.cs`

- [ ] **Step 1.1: Write failing profile deserialization test**

Create `tests/RevitCli.Tests/Profile/PublishPipelineTests.cs`:

```csharp
using System.IO;
using RevitCli.Profile;
using Xunit;

namespace RevitCli.Tests.Profile;

public class PublishPipelineTests
{
    private static ProjectProfile LoadYaml(string yaml)
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, yaml);
        try { return ProfileLoader.Load(path); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Publish_Incremental_True_ReadsNewFields()
    {
        var profile = LoadYaml(@"
version: 1
publish:
  default:
    precheck: default
    incremental: true
    baselinePath: baseline.json
    sinceMode: content
    presets: [dwg]
");
        var pipeline = profile.Publish["default"];
        Assert.True(pipeline.Incremental);
        Assert.Equal("baseline.json", pipeline.BaselinePath);
        Assert.Equal("content", pipeline.SinceMode);
        Assert.Equal(new[] { "dwg" }, pipeline.Presets);
        Assert.Equal("default", pipeline.Precheck);
    }

    [Fact]
    public void Publish_Defaults_WhenNewFieldsOmitted()
    {
        var profile = LoadYaml(@"
version: 1
publish:
  default:
    presets: [dwg]
");
        var pipeline = profile.Publish["default"];
        Assert.False(pipeline.Incremental);
        Assert.Null(pipeline.BaselinePath);
        Assert.Equal("content", pipeline.SinceMode);
    }

    [Fact]
    public void Publish_SinceMode_Meta_PassesThrough()
    {
        var profile = LoadYaml(@"
version: 1
publish:
  default:
    sinceMode: meta
    presets: [dwg]
");
        Assert.Equal("meta", profile.Publish["default"].SinceMode);
    }

    [Fact]
    public void Publish_NoIncrementalField_FalseByDefault()
    {
        var profile = LoadYaml(@"
version: 1
publish:
  default:
    precheck: default
    presets: [dwg, pdf]
");
        Assert.False(profile.Publish["default"].Incremental);
    }
}
```

- [ ] **Step 1.2: Verify compile fail**

```bash
dotnet build src/RevitCli/RevitCli.csproj --nologo --verbosity minimal
```

Expected: compile error — `Incremental`, `BaselinePath`, `SinceMode` don't exist on `PublishPipeline`.

- [ ] **Step 1.3: Extend `PublishPipeline` in `src/RevitCli/Profile/ProjectProfile.cs`**

Replace the existing `PublishPipeline` class body:

```csharp
public class PublishPipeline
{
    [YamlMember(Alias = "presets")]
    public List<string> Presets { get; set; } = new();

    [YamlMember(Alias = "precheck")]
    public string? Precheck { get; set; }

    [YamlMember(Alias = "incremental")]
    public bool Incremental { get; set; } = false;

    [YamlMember(Alias = "baselinePath")]
    public string? BaselinePath { get; set; }

    [YamlMember(Alias = "sinceMode")]
    public string SinceMode { get; set; } = "content";
}
```

- [ ] **Step 1.4: Verify build**

```bash
dotnet build src/RevitCli/RevitCli.csproj --nologo --verbosity minimal
```

Expected: 0 errors, 0 warnings.

WSL cannot run `dotnet test` (testhost timeout). Controller verifies on Windows.

- [ ] **Step 1.5: Commit**

```bash
git add src/RevitCli/Profile/ProjectProfile.cs \
        tests/RevitCli.Tests/Profile/PublishPipelineTests.cs
git commit -m "feat(profile): add publish.incremental/baselinePath/sinceMode fields (P2)"
```

---

## Task 2: SnapshotHasher.HashSheetContent + SinceMode-aware SnapshotDiffer

**Files:**
- Create: `src/RevitCli/Output/SinceMode.cs`
- Modify: `shared/RevitCli.Shared/SnapshotHasher.cs`
- Modify: `src/RevitCli/Output/SnapshotDiffer.cs`
- Create: `tests/RevitCli.Tests/Output/SinceModeTests.cs`

- [ ] **Step 2.1: Create `src/RevitCli/Output/SinceMode.cs`**

```csharp
namespace RevitCli.Output;

public enum SinceMode
{
    /// <summary>
    /// Compare sheets using MetaHash + ContentHash (sheet meta + elements on placed views).
    /// Falls back to MetaHash-only when either side's ContentHash is empty (e.g. a P1 baseline).
    /// </summary>
    Content,

    /// <summary>
    /// Compare sheets using MetaHash only (sheet own parameters + viewId).
    /// Faster, but won't detect changes to elements drawn on the sheet.
    /// </summary>
    Meta
}

public static class SinceModeParser
{
    /// <summary>Parse the profile string into a SinceMode. Unrecognized strings default to Content.</summary>
    public static SinceMode Parse(string? raw) =>
        string.Equals(raw, "meta", System.StringComparison.OrdinalIgnoreCase)
            ? SinceMode.Meta
            : SinceMode.Content;
}
```

- [ ] **Step 2.2: Write failing HashSheetContent test**

Add to `tests/RevitCli.Tests/Shared/SnapshotHasherTests.cs` (at end of class, before closing brace):

```csharp
    [Fact]
    public void HashSheetContent_StableForSameInputs()
    {
        var perView = new List<(long viewId, List<string> elementHashes)>
        {
            (200, new List<string> { "h-wall-1", "h-wall-2" }),
            (201, new List<string> { "h-door-1" })
        };
        var h1 = SnapshotHasher.HashSheetContent("meta-hash-A", perView);
        var h2 = SnapshotHasher.HashSheetContent("meta-hash-A", perView);
        Assert.Equal(h1, h2);
        Assert.Equal(16, h1.Length);
    }

    [Fact]
    public void HashSheetContent_DiffersWhenMetaHashChanges()
    {
        var perView = new List<(long, List<string>)>
        {
            (200, new List<string> { "h-wall-1" })
        };
        var a = SnapshotHasher.HashSheetContent("meta-A", perView);
        var b = SnapshotHasher.HashSheetContent("meta-B", perView);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void HashSheetContent_DiffersWhenElementHashesChange()
    {
        var perViewA = new List<(long, List<string>)> { (200, new() { "h1" }) };
        var perViewB = new List<(long, List<string>)> { (200, new() { "h2" }) };
        Assert.NotEqual(
            SnapshotHasher.HashSheetContent("m", perViewA),
            SnapshotHasher.HashSheetContent("m", perViewB));
    }

    [Fact]
    public void HashSheetContent_StableAcrossViewInsertionOrder()
    {
        var a = new List<(long, List<string>)>
        {
            (200, new() { "h1" }),
            (201, new() { "h2" })
        };
        var b = new List<(long, List<string>)>
        {
            (201, new() { "h2" }),
            (200, new() { "h1" })
        };
        Assert.Equal(
            SnapshotHasher.HashSheetContent("m", a),
            SnapshotHasher.HashSheetContent("m", b));
    }

    [Fact]
    public void HashSheetContent_StableAcrossElementOrderWithinView()
    {
        // Element hashes within a view: sorted stably so hash doesn't depend on
        // the order Revit returned elements in.
        var a = new List<(long, List<string>)> { (200, new() { "h-a", "h-b", "h-c" }) };
        var b = new List<(long, List<string>)> { (200, new() { "h-c", "h-a", "h-b" }) };
        Assert.Equal(
            SnapshotHasher.HashSheetContent("m", a),
            SnapshotHasher.HashSheetContent("m", b));
    }
```

- [ ] **Step 2.3: Add `HashSheetContent` to `shared/RevitCli.Shared/SnapshotHasher.cs`**

Insert after the existing `HashSheetMeta` method, before `HashSchedule`:

```csharp
    /// <summary>
    /// Compose a sheet's ContentHash from its MetaHash and the element-hash set of each placed view.
    /// Views are sorted by viewId, element hashes within a view are sorted ordinally, so result
    /// is stable across collector iteration order.
    /// </summary>
    public static string HashSheetContent(
        string sheetMetaHash,
        List<(long viewId, List<string> elementHashes)> perView)
    {
        var sb = new StringBuilder();
        sb.Append("meta=").Append(Escape(sheetMetaHash ?? "")).Append('\n');
        foreach (var (viewId, hashes) in (perView ?? new List<(long, List<string>)>())
                 .OrderBy(v => v.viewId))
        {
            sb.Append("view=").Append(viewId).Append('\n');
            foreach (var h in (hashes ?? new List<string>()).OrderBy(h => h, StringComparer.Ordinal))
            {
                sb.Append("  ").Append(Escape(h ?? "")).Append('\n');
            }
        }
        return Sha256Short(sb.ToString());
    }
```

- [ ] **Step 2.4: Write failing SinceMode differ test**

Create `tests/RevitCli.Tests/Output/SinceModeTests.cs`:

```csharp
using System.Collections.Generic;
using RevitCli.Output;
using RevitCli.Shared;
using Xunit;

namespace RevitCli.Tests.Output;

public class SinceModeTests
{
    private static ModelSnapshot SnapWithSheet(string number, string metaHash, string contentHash)
    {
        return new ModelSnapshot
        {
            SchemaVersion = 1,
            Revit = new SnapshotRevit { DocumentPath = "/a.rvt" },
            Sheets = new List<SnapshotSheet>
            {
                new() { Number = number, Name = "S", ViewId = 99,
                        MetaHash = metaHash, ContentHash = contentHash }
            }
        };
    }

    [Fact]
    public void Diff_ContentMode_DetectsContentHashChange_EvenIfMetaSame()
    {
        var a = SnapWithSheet("A-01", metaHash: "m1", contentHash: "c1");
        var b = SnapWithSheet("A-01", metaHash: "m1", contentHash: "c2");
        var d = SnapshotDiffer.Diff(a, b, sinceMode: SinceMode.Content);
        Assert.Single(d.Sheets.Modified);
        Assert.Equal("sheet:A-01", d.Sheets.Modified[0].Key);
    }

    [Fact]
    public void Diff_MetaMode_IgnoresContentHashChange_WhenMetaSame()
    {
        var a = SnapWithSheet("A-01", metaHash: "m1", contentHash: "c1");
        var b = SnapWithSheet("A-01", metaHash: "m1", contentHash: "c2");
        var d = SnapshotDiffer.Diff(a, b, sinceMode: SinceMode.Meta);
        Assert.Empty(d.Sheets.Modified);
    }

    [Fact]
    public void Diff_ContentMode_EmptyBaselineContentHash_FallsBackToMeta()
    {
        // P1 baseline had empty ContentHash. v1.2.0 diff should NOT mark this sheet as
        // modified just because content is filled now — fall back to MetaHash comparison.
        var a = SnapWithSheet("A-01", metaHash: "m1", contentHash: "");     // P1-era baseline
        var b = SnapWithSheet("A-01", metaHash: "m1", contentHash: "c2");   // v1.2.0 snapshot
        var d = SnapshotDiffer.Diff(a, b, sinceMode: SinceMode.Content);
        Assert.Empty(d.Sheets.Modified);
    }

    [Fact]
    public void Diff_ContentMode_EmptyBaselineContentHash_DetectsMetaChange()
    {
        var a = SnapWithSheet("A-01", metaHash: "m1", contentHash: "");
        var b = SnapWithSheet("A-01", metaHash: "m2", contentHash: "c2");
        var d = SnapshotDiffer.Diff(a, b, sinceMode: SinceMode.Content);
        Assert.Single(d.Sheets.Modified);
    }

    [Fact]
    public void Diff_NoSinceModeArgument_DefaultsToPreExistingMetaHashBehavior()
    {
        // The original Diff(from, to) overload (no sinceMode) keeps its P1 semantics
        // (compare MetaHash). This is source compat for any caller that passed nothing.
        var a = SnapWithSheet("A-01", metaHash: "m1", contentHash: "c1");
        var b = SnapWithSheet("A-01", metaHash: "m1", contentHash: "c2");
        var d = SnapshotDiffer.Diff(a, b);
        Assert.Empty(d.Sheets.Modified);  // MetaHash-only under the no-arg overload
    }

    [Fact]
    public void Diff_ContentMode_DetectsBothMetaAndContentChanges()
    {
        var a = SnapWithSheet("A-01", metaHash: "m1", contentHash: "c1");
        var b = SnapWithSheet("A-01", metaHash: "m2", contentHash: "c2");
        var d = SnapshotDiffer.Diff(a, b, sinceMode: SinceMode.Content);
        Assert.Single(d.Sheets.Modified);
    }
}
```

- [ ] **Step 2.5: Extend `SnapshotDiffer` with SinceMode-aware overload**

In `src/RevitCli/Output/SnapshotDiffer.cs`, add `SinceMode` parameter to the public API. The existing zero-arg overload keeps MetaHash-only semantics (source compat for P1 callers). Replace the entire `Diff` method and the `DiffSheets` method as shown:

**Update `Diff` signature** — replace:

```csharp
    public static SnapshotDiff Diff(ModelSnapshot from, ModelSnapshot to,
                                    string? fromLabel = null, string? toLabel = null)
```

with:

```csharp
    public static SnapshotDiff Diff(ModelSnapshot from, ModelSnapshot to,
                                    string? fromLabel = null, string? toLabel = null,
                                    SinceMode sinceMode = SinceMode.Meta)
```

The new default is `SinceMode.Meta` — this preserves the P1 semantics when old callers invoke `Diff(from, to)` without specifying mode. New P2 call sites pass `SinceMode.Content` explicitly.

**Thread `sinceMode` into `DiffSheets`** — update the call inside `Diff`:

```csharp
        // Sheets — key on Number
        result.Sheets = DiffSheets(from.Sheets, to.Sheets, sinceMode);
```

**Replace `DiffSheets` method body** with:

```csharp
    private static CategoryDiff DiffSheets(List<SnapshotSheet> a, List<SnapshotSheet> b, SinceMode sinceMode)
    {
        var aByNum = a.GroupBy(s => s.Number).ToDictionary(g => g.Key, g => g.First());
        var bByNum = b.GroupBy(s => s.Number).ToDictionary(g => g.Key, g => g.First());
        var diff = new CategoryDiff();

        foreach (var num in bByNum.Keys.Except(aByNum.Keys))
        {
            var s = bByNum[num];
            diff.Added.Add(new AddedItem { Id = s.ViewId, Key = $"sheet:{num}", Name = s.Name });
        }
        foreach (var num in aByNum.Keys.Except(bByNum.Keys))
        {
            var s = aByNum[num];
            diff.Removed.Add(new RemovedItem { Id = s.ViewId, Key = $"sheet:{num}", Name = s.Name });
        }
        foreach (var num in aByNum.Keys.Intersect(bByNum.Keys))
        {
            var sa = aByNum[num];
            var sb = bByNum[num];
            if (SheetChanged(sa, sb, sinceMode))
            {
                diff.Modified.Add(new ModifiedItem
                {
                    Id = sb.ViewId,
                    Key = $"sheet:{num}",
                    OldHash = sinceMode == SinceMode.Content ? sa.ContentHash : sa.MetaHash,
                    NewHash = sinceMode == SinceMode.Content ? sb.ContentHash : sb.MetaHash,
                    Changed = DiffParameters(sa.Parameters, sb.Parameters)
                });
            }
        }
        return diff;
    }

    private static bool SheetChanged(SnapshotSheet a, SnapshotSheet b, SinceMode sinceMode)
    {
        if (sinceMode == SinceMode.Meta) return a.MetaHash != b.MetaHash;

        // Content mode: use ContentHash if both sides populated; otherwise fall back to MetaHash.
        // A P1 baseline (empty ContentHash on both) effectively runs Meta-mode automatically.
        if (string.IsNullOrEmpty(a.ContentHash) || string.IsNullOrEmpty(b.ContentHash))
            return a.MetaHash != b.MetaHash;

        return a.ContentHash != b.ContentHash;
    }
```

- [ ] **Step 2.6: Verify build + commit**

```bash
dotnet build src/RevitCli/RevitCli.csproj --nologo --verbosity minimal
```

Expected: 0 errors.

```bash
git add src/RevitCli/Output/SinceMode.cs \
        src/RevitCli/Output/SnapshotDiffer.cs \
        shared/RevitCli.Shared/SnapshotHasher.cs \
        tests/RevitCli.Tests/Shared/SnapshotHasherTests.cs \
        tests/RevitCli.Tests/Output/SinceModeTests.cs
git commit -m "feat(cli,shared): SinceMode + ContentHash-aware sheet diff (P2)"
```

---

## Task 3: Fill SnapshotSheet.ContentHash in RealRevitOperations

**Files:**
- Modify: `src/RevitCli.Addin/Services/RealRevitOperations.cs`
- Modify: `shared/RevitCli.Shared/ModelSnapshot.cs` (doc comment only)

Note: Cannot run Revit API code on Linux. Addin compile is verified on Windows via controller rsync + `dotnet build`. E2E in Task 7.

- [ ] **Step 3.1: Add XML doc to `SnapshotSheet.ContentHash`**

In `shared/RevitCli.Shared/ModelSnapshot.cs`, replace the `SnapshotSheet` class property:

```csharp
    /// <summary>
    /// sheet 本身(不含 placed views)的 hash. 填充于 P1.
    /// </summary>
    [JsonPropertyName("metaHash")]
    public string MetaHash { get; set; } = "";

    /// <summary>
    /// MetaHash + 所有 PlacedViewIds 上可见元素的 hash 聚合.
    /// Reflects structural content: sheet metadata + element hashes of every element in the
    /// non-hidden scope of every placed view. Does NOT honor per-view Visibility/Graphics
    /// overrides — hiding a wall via V/G does not change ContentHash. Populated starting in P2.
    /// Empty string on P1-era baselines; diff callers should fall back to MetaHash when empty.
    /// </summary>
    [JsonPropertyName("contentHash")]
    public string ContentHash { get; set; } = "";
```

Note: keep the `[JsonPropertyName]` attributes exactly as-is — the fix here is inserting the `<summary>` comment above them, nothing else.

- [ ] **Step 3.2: Locate the sheets loop in `RealRevitOperations.CaptureSnapshotAsync`**

It's inside `_bridge.InvokeAsync(app => { ... })`. Find the block that starts:

```csharp
            // Sheets
            if (request.IncludeSheets)
            {
                foreach (var sheet in new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSheet)).Cast<ViewSheet>())
                {
```

and the non-SummaryOnly branch that currently writes `ContentHash = ""  // P2 will compute`.

- [ ] **Step 3.3: Build an element-hash index before the sheets loop**

**Insert** immediately before the `// Sheets` comment, after the categories loop closes but while still inside the `_bridge.InvokeAsync` lambda:

```csharp
            // Build an element-hash index for ContentHash lookup. Elements in SummaryOnly mode
            // have empty Hash, in which case we'd produce empty-string rows in the view vectors,
            // but SummaryOnly skips sheets/schedules entirely below so this index is only
            // consulted in the full-snapshot path.
            var elementHashById = new Dictionary<long, string>();
            foreach (var kv in snapshot.Categories)
            {
                foreach (var el in kv.Value)
                {
                    elementHashById[el.Id] = el.Hash;
                }
            }
```

- [ ] **Step 3.4: Replace the sheets loop's ContentHash line with the real computation**

Find the lines that currently say:

```csharp
                    var sheetSnap = new SnapshotSheet
                    {
                        Number = sheet.SheetNumber ?? "",
                        Name = sheet.Name ?? "",
                        ViewId = ToCliElementId(sheet.Id),
                        PlacedViewIds = placedIds,
                        Parameters = ReadVisibleParameters(doc, sheet),
                        ContentHash = ""  // P2 will compute
                    };
                    sheetSnap.MetaHash = SnapshotHasher.HashSheetMeta(sheetSnap);
                    snapshot.Sheets.Add(sheetSnap);
```

**Replace** the whole block with:

```csharp
                    var sheetSnap = new SnapshotSheet
                    {
                        Number = sheet.SheetNumber ?? "",
                        Name = sheet.Name ?? "",
                        ViewId = ToCliElementId(sheet.Id),
                        PlacedViewIds = placedIds,
                        Parameters = ReadVisibleParameters(doc, sheet)
                    };
                    sheetSnap.MetaHash = SnapshotHasher.HashSheetMeta(sheetSnap);
                    sheetSnap.ContentHash = ComputeSheetContentHash(
                        doc, sheetSnap.MetaHash, placedIds, elementHashById);
                    snapshot.Sheets.Add(sheetSnap);
```

- [ ] **Step 3.5: Add the private `ComputeSheetContentHash` helper at the end of `RealRevitOperations`**

Place it just before the closing `}` of the class:

```csharp
    /// <summary>
    /// Per placed view, enumerate non-type elements in view scope and look up each element's
    /// already-computed Hash from the snapshot's element index. Missing lookups yield empty
    /// strings (sorted out deterministically by HashSheetContent).
    /// </summary>
    private static string ComputeSheetContentHash(
        Document doc,
        string metaHash,
        List<long> placedViewIds,
        Dictionary<long, string> elementHashById)
    {
        var perView = new List<(long viewId, List<string> elementHashes)>();
        foreach (var viewIdLong in placedViewIds)
        {
            var hashes = new List<string>();
            try
            {
                var viewId = new ElementId(viewIdLong);
                foreach (var element in new FilteredElementCollector(doc, viewId)
                    .WhereElementIsNotElementType())
                {
                    var id = ToCliElementId(element.Id);
                    if (elementHashById.TryGetValue(id, out var h))
                        hashes.Add(h);
                    // Elements outside snapshot categories (e.g. annotations, detail items)
                    // are intentionally skipped — ContentHash tracks the structural scope we snapshot.
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[RevitCli] ComputeSheetContentHash view {viewIdLong}: {ex.Message}");
            }
            perView.Add((viewIdLong, hashes));
        }
        return SnapshotHasher.HashSheetContent(metaHash, perView);
    }
```

- [ ] **Step 3.6: Verify addin build (Windows-side, controller will run)**

Controller runs:
```
dotnet build src/RevitCli.Addin/RevitCli.Addin.csproj -f net8.0-windows -p:RevitYear=2026
```
Expected: 0 errors.

On the Linux side, `dotnet build src/RevitCli/RevitCli.csproj` still works because only the shared doc-comment change is in the CLI graph.

- [ ] **Step 3.7: Commit**

```bash
git add src/RevitCli.Addin/Services/RealRevitOperations.cs \
        shared/RevitCli.Shared/ModelSnapshot.cs
git commit -m "feat(addin): populate SnapshotSheet.ContentHash via placed-view element hashes (P2)"
```

---

## Task 4: BaselineManager — atomic baseline read/write

**Files:**
- Create: `src/RevitCli/Output/BaselineManager.cs`
- Create: `tests/RevitCli.Tests/Output/BaselineManagerTests.cs`

- [ ] **Step 4.1: Write failing BaselineManager tests**

Create `tests/RevitCli.Tests/Output/BaselineManagerTests.cs`:

```csharp
using System.IO;
using System.Text.Json;
using RevitCli.Output;
using RevitCli.Shared;
using Xunit;

namespace RevitCli.Tests.Output;

public class BaselineManagerTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), "revitcli-baseline-test-" + System.Guid.NewGuid() + ".json");

    [Fact]
    public void Load_MissingFile_ReturnsNull()
    {
        var path = TempPath();
        Assert.False(File.Exists(path));

        var result = BaselineManager.Load(path);

        Assert.Null(result);
    }

    [Fact]
    public void Load_ValidSnapshot_ReturnsIt()
    {
        var path = TempPath();
        var original = new ModelSnapshot
        {
            SchemaVersion = 1,
            TakenAt = "2026-04-23T00:00:00Z",
            Revit = new SnapshotRevit { Version = "2026", DocumentPath = "/a.rvt" }
        };
        File.WriteAllText(path, JsonSerializer.Serialize(original));
        try
        {
            var loaded = BaselineManager.Load(path);
            Assert.NotNull(loaded);
            Assert.Equal("2026", loaded!.Revit.Version);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_CorruptedJson_ReturnsNull()
    {
        var path = TempPath();
        File.WriteAllText(path, "{not valid json");
        try
        {
            var result = BaselineManager.Load(path);
            Assert.Null(result);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Save_WritesJsonAtomically()
    {
        var path = TempPath();
        var snap = new ModelSnapshot { SchemaVersion = 1, TakenAt = "2026-04-23T10:00:00Z" };
        try
        {
            BaselineManager.Save(path, snap);
            Assert.True(File.Exists(path));

            var loaded = BaselineManager.Load(path);
            Assert.NotNull(loaded);
            Assert.Equal("2026-04-23T10:00:00Z", loaded!.TakenAt);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Save_CreatesParentDirectory_IfMissing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "revitcli-baseline-" + System.Guid.NewGuid());
        var path = Path.Combine(dir, "nested", "baseline.json");
        var snap = new ModelSnapshot { SchemaVersion = 1 };
        try
        {
            BaselineManager.Save(path, snap);
            Assert.True(File.Exists(path));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Save_OverwritesExistingFile()
    {
        var path = TempPath();
        var a = new ModelSnapshot { SchemaVersion = 1, TakenAt = "first" };
        var b = new ModelSnapshot { SchemaVersion = 1, TakenAt = "second" };
        try
        {
            BaselineManager.Save(path, a);
            BaselineManager.Save(path, b);

            var loaded = BaselineManager.Load(path);
            Assert.Equal("second", loaded!.TakenAt);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
```

- [ ] **Step 4.2: Verify compile fail**

```bash
dotnet build src/RevitCli/RevitCli.csproj --nologo --verbosity minimal
```

Expected: compile error — `BaselineManager` does not exist.

- [ ] **Step 4.3: Create `src/RevitCli/Output/BaselineManager.cs`**

```csharp
using System;
using System.IO;
using System.Text.Json;
using RevitCli.Shared;

namespace RevitCli.Output;

/// <summary>
/// Reads and writes baseline snapshots for `publish --since`. Write is atomic via tmp+rename
/// so a half-written file never surfaces if the process dies mid-save. Corrupted files return
/// null on Load so the caller can surface a clean error and keep the old baseline untouched.
/// </summary>
public static class BaselineManager
{
    private static readonly JsonSerializerOptions ReadOpts = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };

    /// <summary>
    /// Reads a snapshot from disk. Returns null if the file doesn't exist or is unreadable.
    /// Never throws for I/O or deserialization errors — callers decide whether missing means
    /// "no baseline yet" or "error" based on context.
    /// </summary>
    public static ModelSnapshot? Load(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<ModelSnapshot>(json, ReadOpts);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes a snapshot to disk atomically (write to .tmp, then rename over target).
    /// Creates parent directories as needed. Throws on I/O failure so the caller can
    /// decide whether to preserve the old baseline or retry.
    /// </summary>
    public static void Save(string path, ModelSnapshot snapshot)
    {
        var fullPath = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var tmp = fullPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot, WriteOpts));
        if (File.Exists(fullPath)) File.Delete(fullPath);
        File.Move(tmp, fullPath);
    }
}
```

- [ ] **Step 4.4: Verify build + commit**

```bash
dotnet build src/RevitCli/RevitCli.csproj --nologo --verbosity minimal
```

Expected: 0 errors.

```bash
git add src/RevitCli/Output/BaselineManager.cs \
        tests/RevitCli.Tests/Output/BaselineManagerTests.cs
git commit -m "feat(cli): add BaselineManager for atomic baseline snapshot I/O (P2)"
```

---

## Task 5: PublishCommand `--since` / `--since-mode` / `--update-baseline` flags

**Files:**
- Modify: `src/RevitCli/Commands/PublishCommand.cs`
- Create: `tests/RevitCli.Tests/Commands/PublishSinceTests.cs`

- [ ] **Step 5.1: Write failing publish tests**

Create `tests/RevitCli.Tests/Commands/PublishSinceTests.cs`:

```csharp
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

public class PublishSinceTests
{
    private static string WriteFixture(string dir, string name, string content)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "revitcli-publish-since-" + System.Guid.NewGuid());
        Directory.CreateDirectory(d);
        return d;
    }

    private static ModelSnapshot SnapWith(params (string number, string metaHash, string contentHash)[] sheets)
    {
        var s = new ModelSnapshot { SchemaVersion = 1, Revit = new SnapshotRevit { DocumentPath = "/a.rvt" } };
        foreach (var (num, mh, ch) in sheets)
            s.Sheets.Add(new SnapshotSheet { Number = num, Name = num, ViewId = 1000 + num.Length,
                MetaHash = mh, ContentHash = ch });
        return s;
    }

    [Fact]
    public async Task Publish_WithoutSince_FullExportAsUsual()
    {
        var dir = TempDir();
        try
        {
            var profilePath = WriteFixture(dir, ".revitcli.yml", @"
version: 1
exports:
  dwg-all:
    format: dwg
    sheets: [all]
    outputDir: ./out
publish:
  default:
    presets: [dwg-all]
");
            var snapshotJson = JsonSerializer.Serialize(SnapWith(("A-01", "m1", "c1")));
            var handler = new FakeHttpHandler(JsonSerializer.Serialize(
                ApiResponse<ExportProgress>.Ok(new ExportProgress { Status = "completed", Progress = 100 })));
            var client = new RevitClient(new HttpClient(handler) { BaseAddress = new System.Uri("http://localhost:17839") });
            var writer = new StringWriter();

            var exit = await PublishCommand.ExecuteAsync(
                client, name: "default", profilePath: profilePath,
                dryRun: false,
                since: null, sinceMode: null, updateBaseline: false,
                writer);

            Assert.Equal(0, exit);
            Assert.Contains("1 succeeded", writer.ToString());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Publish_Since_NoSheetChanges_SkipsExport()
    {
        var dir = TempDir();
        try
        {
            var profilePath = WriteFixture(dir, ".revitcli.yml", @"
version: 1
exports:
  dwg-all:
    format: dwg
    sheets: [all]
    outputDir: ./out
publish:
  default:
    presets: [dwg-all]
");
            var baseline = SnapWith(("A-01", "m1", "c1"), ("A-02", "m2", "c2"));
            var current = baseline;
            var baselinePath = WriteFixture(dir, "baseline.json", JsonSerializer.Serialize(baseline));

            var handler = new FakeHttpHandler(JsonSerializer.Serialize(
                ApiResponse<ModelSnapshot>.Ok(current)));
            var client = new RevitClient(new HttpClient(handler) { BaseAddress = new System.Uri("http://localhost:17839") });
            var writer = new StringWriter();

            var exit = await PublishCommand.ExecuteAsync(
                client, name: "default", profilePath: profilePath,
                dryRun: false,
                since: baselinePath, sinceMode: "content", updateBaseline: false,
                writer);

            Assert.Equal(0, exit);
            var output = writer.ToString();
            Assert.Contains("no sheets changed", output.ToLower());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Publish_Since_ContentChanged_FiltersToChangedSheets()
    {
        var dir = TempDir();
        try
        {
            var profilePath = WriteFixture(dir, ".revitcli.yml", @"
version: 1
exports:
  dwg-all:
    format: dwg
    sheets: [all]
    outputDir: ./out
publish:
  default:
    presets: [dwg-all]
");
            var baseline = SnapWith(("A-01", "m1", "c1"), ("A-02", "m2", "c2"));
            var current  = SnapWith(("A-01", "m1", "c1"), ("A-02", "m2", "c2-CHANGED"));
            var baselinePath = WriteFixture(dir, "baseline.json", JsonSerializer.Serialize(baseline));

            // The server returns two JSON payloads in sequence: the current snapshot (from
            // CaptureSnapshotAsync) and the export result. FakeHttpHandler only stores one
            // response, so we model "capture then export" as a single snapshot fetch followed
            // by a separate test that validates the export call. Here we verify the *decision*:
            // which sheets would be exported given this diff.

            var handler = new FakeHttpHandler(JsonSerializer.Serialize(
                ApiResponse<ModelSnapshot>.Ok(current)));
            var client = new RevitClient(new HttpClient(handler) { BaseAddress = new System.Uri("http://localhost:17839") });
            var writer = new StringWriter();

            var exit = await PublishCommand.ExecuteAsync(
                client, name: "default", profilePath: profilePath,
                dryRun: true,   // dry-run so we don't actually call the export endpoint
                since: baselinePath, sinceMode: "content", updateBaseline: false,
                writer);

            Assert.Equal(0, exit);
            var output = writer.ToString();
            Assert.Contains("A-02", output);
            Assert.DoesNotContain("A-01 [", output);  // A-01 not in any export line
            Assert.Contains("dry-run", output);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Publish_Since_BaselineMissing_ReturnsError()
    {
        var dir = TempDir();
        try
        {
            var profilePath = WriteFixture(dir, ".revitcli.yml", @"
version: 1
exports:
  dwg-all:
    format: dwg
    sheets: [all]
    outputDir: ./out
publish:
  default:
    presets: [dwg-all]
");
            var handler = new FakeHttpHandler("{}");
            var client = new RevitClient(new HttpClient(handler) { BaseAddress = new System.Uri("http://localhost:17839") });
            var writer = new StringWriter();

            var exit = await PublishCommand.ExecuteAsync(
                client, name: "default", profilePath: profilePath,
                dryRun: true,
                since: Path.Combine(dir, "does-not-exist.json"),
                sinceMode: "content", updateBaseline: false,
                writer);

            Assert.Equal(1, exit);
            Assert.Contains("baseline not found", writer.ToString().ToLower());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Publish_IncrementalProfile_UsesDefaultBaselinePath()
    {
        var dir = TempDir();
        try
        {
            var profilePath = WriteFixture(dir, ".revitcli.yml", @"
version: 1
exports:
  dwg-all:
    format: dwg
    sheets: [all]
    outputDir: ./out
publish:
  default:
    incremental: true
    presets: [dwg-all]
");
            var baseline = SnapWith(("A-01", "m1", "c1"));
            var baselineDir = Path.Combine(dir, ".revitcli");
            Directory.CreateDirectory(baselineDir);
            File.WriteAllText(Path.Combine(baselineDir, "last-publish.json"),
                JsonSerializer.Serialize(baseline));

            var handler = new FakeHttpHandler(JsonSerializer.Serialize(
                ApiResponse<ModelSnapshot>.Ok(baseline)));
            var client = new RevitClient(new HttpClient(handler) { BaseAddress = new System.Uri("http://localhost:17839") });
            var writer = new StringWriter();

            var exit = await PublishCommand.ExecuteAsync(
                client, name: "default", profilePath: profilePath,
                dryRun: true,
                since: null, sinceMode: null, updateBaseline: false,
                writer);

            Assert.Equal(0, exit);
            Assert.Contains("no sheets changed", writer.ToString().ToLower());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
```

- [ ] **Step 5.2: Verify test compile failure**

```bash
dotnet build src/RevitCli/RevitCli.csproj --nologo --verbosity minimal
```

Expected: compile error — `PublishCommand.ExecuteAsync` signature doesn't have `since`, `sinceMode`, `updateBaseline` parameters yet.

- [ ] **Step 5.3: Replace `PublishCommand.Create` method**

In `src/RevitCli/Commands/PublishCommand.cs`, replace the existing `Create` method with:

```csharp
    public static Command Create(RevitClient client)
    {
        var nameArg = new Argument<string?>("name", () => null, "Publish pipeline name (default: 'default')");
        var profileOpt = new Option<string?>("--profile", "Path to .revitcli.yml profile");
        var dryRunOpt = new Option<bool>("--dry-run", "Show what would be exported without exporting");
        var sinceOpt = new Option<string?>("--since", "Baseline snapshot JSON file; only re-export sheets whose content changed since");
        var sinceModeOpt = new Option<string?>("--since-mode", "content | meta (default: content, or from profile)");
        var updateBaselineOpt = new Option<bool>("--update-baseline", "After successful publish, write the current snapshot back to the --since path");

        var command = new Command("publish", "Run export pipeline from .revitcli.yml profile")
        {
            nameArg, profileOpt, dryRunOpt, sinceOpt, sinceModeOpt, updateBaselineOpt
        };

        command.SetHandler(async (name, profilePath, dryRun, since, sinceMode, updateBaseline) =>
        {
            Environment.ExitCode = await ExecuteAsync(
                client, name, profilePath, dryRun, since, sinceMode, updateBaseline, Console.Out);
        }, nameArg, profileOpt, dryRunOpt, sinceOpt, sinceModeOpt, updateBaselineOpt);

        return command;
    }
```

- [ ] **Step 5.4: Replace `PublishCommand.ExecuteAsync` signature and add incremental logic**

Replace the **entire** `ExecuteAsync` method (from its signature line down to its final `return exitCode;`) with the version below. This integrates baseline load, snapshot capture, sheet filtering, and baseline update. Everything outside `ExecuteAsync` in the file (Create, ComputeFileHash) stays as-is.

```csharp
    public static async Task<int> ExecuteAsync(
        RevitClient client,
        string? name,
        string? profilePath,
        bool dryRun,
        string? since,
        string? sinceMode,
        bool updateBaseline,
        TextWriter output)
    {
        // Load profile
        ProjectProfile? profile;
        string? profileDir = null;
        string? resolvedProfilePath = null;
        try
        {
            if (profilePath != null)
            {
                resolvedProfilePath = Path.GetFullPath(profilePath);
                profile = ProfileLoader.Load(resolvedProfilePath);
                profileDir = Path.GetDirectoryName(resolvedProfilePath);
            }
            else
            {
                var discovered = ProfileLoader.Discover();
                if (discovered != null)
                {
                    resolvedProfilePath = Path.GetFullPath(discovered);
                    profile = ProfileLoader.Load(resolvedProfilePath);
                    profileDir = Path.GetDirectoryName(resolvedProfilePath);
                }
                else
                {
                    profile = null;
                }
            }
        }
        catch (Exception ex)
        {
            await output.WriteLineAsync($"Error loading profile: {ex.Message}");
            return 1;
        }

        if (profile == null)
        {
            await output.WriteLineAsync($"Error: no {ProfileLoader.FileName} found.");
            await output.WriteLineAsync($"  Create one in your project root, or copy from .revitcli.example.yml");
            return 1;
        }

        var pipelineName = name ?? "default";
        if (!profile.Publish.TryGetValue(pipelineName, out var pipeline))
        {
            await output.WriteLineAsync($"Error: publish pipeline '{pipelineName}' not found in profile.");
            if (profile.Publish.Count > 0)
                await output.WriteLineAsync($"  Available pipelines: {string.Join(", ", profile.Publish.Keys)}");
            else
                await output.WriteLineAsync($"  Your profile has no publish pipelines. Add a 'publish:' section.");
            return 1;
        }

        // ── Incremental resolution ──────────────────────────────────────────
        // Effective baseline path: CLI --since wins; else profile.incremental → default
        string? effectiveBaselinePath = since;
        if (effectiveBaselinePath == null && pipeline.Incremental)
        {
            effectiveBaselinePath = pipeline.BaselinePath ?? ".revitcli/last-publish.json";
            // Resolve relative to profile dir
            if (profileDir != null && !Path.IsPathRooted(effectiveBaselinePath))
                effectiveBaselinePath = Path.GetFullPath(Path.Combine(profileDir, effectiveBaselinePath));
        }
        var effectiveSinceMode = SinceModeParser.Parse(sinceMode ?? pipeline.SinceMode);
        var shouldUpdateBaseline = updateBaseline || (pipeline.Incremental && since == null);

        HashSet<string>? changedSheetNumbers = null;
        ModelSnapshot? currentSnapshot = null;
        if (effectiveBaselinePath != null)
        {
            var baseline = BaselineManager.Load(effectiveBaselinePath);
            if (baseline == null)
            {
                await output.WriteLineAsync($"Error: baseline not found or unreadable: {effectiveBaselinePath}");
                await output.WriteLineAsync($"  First time? Run: revitcli snapshot --output {effectiveBaselinePath}");
                return 1;
            }

            await output.WriteLineAsync($"Capturing current snapshot for diff against baseline ...");
            var snapResult = await client.CaptureSnapshotAsync(new SnapshotRequest());
            if (!snapResult.Success)
            {
                await output.WriteLineAsync($"Error: {snapResult.Error}");
                if (snapResult.Error?.Contains("not running") == true)
                    await output.WriteLineAsync("  Run 'revitcli doctor' to diagnose connection issues.");
                return 1;
            }
            currentSnapshot = snapResult.Data!;

            var diff = SnapshotDiffer.Diff(
                baseline, currentSnapshot,
                Path.GetFileName(effectiveBaselinePath), "current",
                effectiveSinceMode);

            changedSheetNumbers = new HashSet<string>();
            foreach (var m in diff.Sheets.Modified)
            {
                // Key is "sheet:A-01" → strip the prefix
                if (m.Key.StartsWith("sheet:"))
                    changedSheetNumbers.Add(m.Key.Substring("sheet:".Length));
            }
            foreach (var a in diff.Sheets.Added)
            {
                if (a.Key.StartsWith("sheet:"))
                    changedSheetNumbers.Add(a.Key.Substring("sheet:".Length));
            }

            if (changedSheetNumbers.Count == 0)
            {
                await output.WriteLineAsync(
                    $"Publish '{pipelineName}': no sheets changed since baseline ({effectiveSinceMode.ToString().ToLower()} mode). Nothing to export.");
                // Still update baseline if incremental — captures any schedule-only or element-only
                // changes that didn't surface at the sheet level.
                if (shouldUpdateBaseline && !dryRun)
                {
                    try
                    {
                        BaselineManager.Save(effectiveBaselinePath, currentSnapshot);
                        await output.WriteLineAsync($"Baseline refreshed: {effectiveBaselinePath}");
                    }
                    catch (Exception ex)
                    {
                        await output.WriteLineAsync($"Warning: failed to update baseline: {ex.Message}");
                    }
                }
                return 0;
            }

            await output.WriteLineAsync(
                $"Incremental publish: {changedSheetNumbers.Count} sheet(s) changed ({effectiveSinceMode.ToString().ToLower()} mode).");
        }

        // Run precheck if defined
        if (!string.IsNullOrWhiteSpace(pipeline.Precheck))
        {
            await output.WriteLineAsync($"Running precheck '{pipeline.Precheck}' ...");
            var checkResult = await CheckCommand.ExecuteAsync(client, pipeline.Precheck, profilePath, "table", null, true, false, output);
            if (checkResult != 0)
            {
                await output.WriteLineAsync("Precheck failed. Aborting publish.");
                await output.WriteLineAsync("  Fix the issues above, or use suppressions to waive known problems.");
                return 1;
            }
            await output.WriteLineAsync("");
        }

        // Run each export preset
        var succeeded = 0;
        var failed = 0;

        foreach (var presetName in pipeline.Presets)
        {
            if (!profile.Exports.TryGetValue(presetName, out var preset))
            {
                await output.WriteLineAsync($"Error: export preset '{presetName}' not found in profile.");
                if (profile.Exports.Count > 0)
                    await output.WriteLineAsync($"  Available presets: {string.Join(", ", profile.Exports.Keys)}");
                failed++;
                continue;
            }

            // Incremental: narrow sheet selector to changed set
            var effectiveSheets = preset.Sheets;
            if (changedSheetNumbers != null)
            {
                if (preset.Sheets == null || preset.Sheets.Contains("all", StringComparer.OrdinalIgnoreCase))
                {
                    // Preset targeted all sheets — replace with the changed set
                    effectiveSheets = new List<string>(changedSheetNumbers);
                }
                else
                {
                    // Preset targeted specific sheets — intersect with changed
                    effectiveSheets = preset.Sheets
                        .Where(s => changedSheetNumbers.Contains(s))
                        .ToList();
                }

                if (effectiveSheets.Count == 0)
                {
                    await output.WriteLineAsync($"  Skipping '{presetName}': no matching changed sheets.");
                    succeeded++;
                    continue;
                }
            }

            // Resolve output dir relative to profile file
            var outputDir = preset.OutputDir ?? profile.Defaults.OutputDir ?? "./exports";
            if (profileDir != null && !Path.IsPathRooted(outputDir))
                outputDir = Path.GetFullPath(Path.Combine(profileDir, outputDir));

            if (dryRun)
            {
                var sheetSummary = effectiveSheets != null && effectiveSheets.Count > 0
                    ? string.Join(",", effectiveSheets)
                    : "(preset default)";
                await output.WriteLineAsync(
                    $"[dry-run] Would export '{presetName}': format={preset.Format}, sheets=[{sheetSummary}], outputDir={outputDir}");
                succeeded++;
                continue;
            }

            await output.WriteLineAsync($"Exporting '{presetName}' ({preset.Format.ToUpper()}) ...");

            var request = new ExportRequest
            {
                Format = preset.Format,
                Sheets = effectiveSheets ?? new List<string>(),
                Views = preset.Views ?? new List<string>(),
                OutputDir = outputDir
            };

            var result = await client.ExportAsync(request);
            if (result.Success && result.Data?.Status == "completed")
            {
                await output.WriteLineAsync($"  Completed: {result.Data.Message ?? "OK"}");
                succeeded++;
            }
            else
            {
                var errMsg = result.Error ?? result.Data?.Message ?? "Unknown error";
                await output.WriteLineAsync($"  Failed: {errMsg}");
                if (errMsg.Contains("not running"))
                    await output.WriteLineAsync("  Run 'revitcli doctor' to diagnose connection issues.");
                failed++;
            }
        }

        await output.WriteLineAsync("");
        await output.WriteLineAsync($"Publish '{pipelineName}': {succeeded} succeeded, {failed} failed");

        var exitCode = failed > 0 ? 1 : 0;

        // Update baseline if all presets succeeded and caller asked for it
        if (exitCode == 0 && shouldUpdateBaseline && currentSnapshot != null && effectiveBaselinePath != null && !dryRun)
        {
            try
            {
                BaselineManager.Save(effectiveBaselinePath, currentSnapshot);
                await output.WriteLineAsync($"Baseline updated: {effectiveBaselinePath}");
            }
            catch (Exception ex)
            {
                await output.WriteLineAsync($"Warning: failed to update baseline: {ex.Message}");
            }
        }
        else if (exitCode != 0 && shouldUpdateBaseline)
        {
            await output.WriteLineAsync($"Baseline NOT updated (publish had failures; baseline retained at {effectiveBaselinePath}).");
        }

        // Journal log + receipt
        if (!dryRun)
        {
            var receipt = new
            {
                action = "publish",
                pipeline = pipelineName,
                succeeded,
                failed,
                presets = pipeline.Presets,
                incremental = changedSheetNumbers != null,
                changedSheets = changedSheetNumbers?.Count ?? 0,
                timestamp = DateTime.UtcNow.ToString("o"),
                user = Environment.UserName,
                profileHash = resolvedProfilePath != null && File.Exists(resolvedProfilePath)
                    ? ComputeFileHash(resolvedProfilePath) : null,
                machine = Environment.MachineName
            };

            Output.JournalLogger.Log(profileDir, receipt);

            try
            {
                var receiptDir = profileDir ?? Directory.GetCurrentDirectory();
                var receiptPath = Path.Combine(receiptDir, ".revitcli", "receipts",
                    $"{pipelineName}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
                var dir = Path.GetDirectoryName(receiptPath)!;
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(receiptPath, System.Text.Json.JsonSerializer.Serialize(receipt,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                await output.WriteLineAsync($"Receipt saved to {receiptPath}");
            }
            catch { /* best effort */ }
        }

        // Webhook notification
        if (!string.IsNullOrWhiteSpace(profile.Defaults.Notify) && !dryRun)
        {
            await Output.WebhookNotifier.NotifyAsync(profile.Defaults.Notify, new
            {
                type = "publish",
                pipeline = pipelineName,
                succeeded,
                failed,
                presets = pipeline.Presets,
                incremental = changedSheetNumbers != null,
                status = exitCode == 0 ? "passed" : "failed",
                timestamp = DateTime.UtcNow.ToString("o")
            });
        }

        return exitCode;
    }
```

- [ ] **Step 5.5: Add required using directives**

At top of `src/RevitCli/Commands/PublishCommand.cs`, ensure present:

```csharp
using RevitCli.Output;   // BaselineManager, SinceMode, SinceModeParser, SnapshotDiffer
```

(`RevitCli.Shared`, `RevitCli.Profile`, `System.CommandLine`, `System.Collections.Generic`, etc. are already in the existing `using` block.)

- [ ] **Step 5.6: Verify build**

```bash
dotnet build src/RevitCli/RevitCli.csproj --nologo --verbosity minimal
```

Expected: 0 errors, 0 warnings.

- [ ] **Step 5.7: Commit**

```bash
git add src/RevitCli/Commands/PublishCommand.cs \
        tests/RevitCli.Tests/Commands/PublishSinceTests.cs
git commit -m "feat(cli): publish --since + --since-mode + --update-baseline + incremental filtering (P2)"
```

---

## Task 6: Completions update + regression assertions

**Files:**
- Modify: `src/RevitCli/Commands/CompletionsCommand.cs`
- Modify: `tests/RevitCli.Tests/Commands/CompletionsCommandTests.cs`

- [ ] **Step 6.1: Check current CompletionsCommand structure**

```bash
grep -n "\"publish\"\|publish)" src/RevitCli/Commands/CompletionsCommand.cs | head -10
```

Look at the `publish` option block in each of the three shell generators (bash `case "$cmd" in publish)`, zsh `(publish)`, pwsh `'publish'`). The existing script likely lists `--profile --dry-run` already — we add `--since --since-mode --update-baseline`.

- [ ] **Step 6.2: Add option completions for publish**

In `src/RevitCli/Commands/CompletionsCommand.cs`:

**Bash section** — find the `publish)` branch inside `case "$cmd" in` and add `--since --since-mode --update-baseline` to its `compgen -W "..."` options list.

**Zsh section** — find the `publish)` arm and add option tuples (the format surrounding code uses — likely something like `'--since[Baseline snapshot JSON]:file:_files'`).

**PowerShell section** — find the block that generates option completions per command and append the three options for `publish`.

Because each generator uses a different syntax, follow the exact pattern of a neighboring option (e.g. `--dry-run` for bool flags, `--profile` for value-taking flags). Exact edits:

**For bash:** locate the line:
```
publish) COMPREPLY=( $(compgen -W "--profile --dry-run" -- "$cur") ) ;;
```
Replace with:
```
publish) COMPREPLY=( $(compgen -W "--profile --dry-run --since --since-mode --update-baseline" -- "$cur") ) ;;
```

**For zsh:** locate the `(publish)` arm and change:
```
(publish) _arguments \
    '--profile[path to .revitcli.yml profile]:file:_files' \
    '--dry-run[show what would be exported]' \
    ;;
```
to:
```
(publish) _arguments \
    '--profile[path to .revitcli.yml profile]:file:_files' \
    '--dry-run[show what would be exported]' \
    '--since[baseline snapshot JSON file]:file:_files' \
    '--since-mode[content or meta]:mode:(content meta)' \
    '--update-baseline[update baseline after successful publish]' \
    ;;
```

**For PowerShell:** locate the publish options block, e.g.:
```
'publish' {
    @('--profile', '--dry-run')
}
```
(or similar pattern — may use a dispatch table). Extend to include `'--since', '--since-mode', '--update-baseline'` following the same pattern.

If the file's structure diverges from the above (e.g. uses a data-driven dispatch rather than switch), adapt the intent: register the three new options for the `publish` command in all three shell outputs.

- [ ] **Step 6.3: Add regression test for publish options in completions**

Append to `tests/RevitCli.Tests/Commands/CompletionsCommandTests.cs` (inside the class, before closing brace):

```csharp
    [Fact]
    public async Task BashCompletions_PublishIncludes_SinceFlags()
    {
        var stdout = new StringWriter();
        Console.SetOut(stdout);

        var exitCode = await CompletionsCommand.Create().InvokeAsync(new[] { "bash" });
        var script = stdout.ToString();

        Assert.Equal(0, exitCode);
        Assert.Contains("--since", script);
        Assert.Contains("--since-mode", script);
        Assert.Contains("--update-baseline", script);
    }

    [Fact]
    public async Task ZshCompletions_PublishIncludes_SinceFlags()
    {
        var stdout = new StringWriter();
        Console.SetOut(stdout);

        var exitCode = await CompletionsCommand.Create().InvokeAsync(new[] { "zsh" });
        var script = stdout.ToString();

        Assert.Equal(0, exitCode);
        Assert.Contains("--since", script);
        Assert.Contains("--since-mode", script);
    }
```

- [ ] **Step 6.4: Build + verify completions regression**

```bash
dotnet build src/RevitCli/RevitCli.csproj --nologo --verbosity minimal
```

Expected: 0 errors.

Controller runs full test suite on Windows:
```
dotnet test tests/RevitCli.Tests/RevitCli.Tests.csproj
```
Expected: baseline 182 + Task 1 (4) + Task 2 (6+5 = 11) + Task 4 (6) + Task 5 (5) + Task 6 (2) = **216 passing** (cumulative through Task 6). No regressions.

- [ ] **Step 6.5: Commit**

```bash
git add src/RevitCli/Commands/CompletionsCommand.cs \
        tests/RevitCli.Tests/Commands/CompletionsCommandTests.cs
git commit -m "feat(cli): complete publish --since options in shell scripts (P2)"
```

---

## Task 7: End-to-end on Revit 2026 + CHANGELOG + tag v1.2.0

**Files:**
- Modify: `CHANGELOG.md`
- Modify: `src/RevitCli/RevitCli.csproj` (Version bump)

Windows + real Revit 2026 required. Addin needs rebuild + redeploy because Task 3 changed `RealRevitOperations`.

- [ ] **Step 7.1: Build + publish on Windows**

```powershell
cd D:\temp\revitcli_build   # assumes controller has rsync'd source here
dotnet publish src\RevitCli\RevitCli.csproj -c Release -o .\publish-cli --nologo --verbosity minimal
$env:RevitInstallDir = 'D:\revit2026\Revit 2026'   # adjust for host
dotnet publish src\RevitCli.Addin\RevitCli.Addin.csproj -c Release -f net8.0-windows -p:RevitYear=2026 --nologo --verbosity minimal
```

Expected: both succeed.

- [ ] **Step 7.2: Deploy addin**

Close Revit 2026. Run the same `deploy-addin.ps1` used for P1 (clears Addins\2026, copies new bits, writes .addin with absolute DLL path).

- [ ] **Step 7.3: Start Revit 2026, open `revit_cli.rvt`, run E2E**

```powershell
$cli = 'D:\temp\revitcli_build\publish-cli\revitcli.exe'

# 1. Status sanity
& $cli status

# 2. Snapshot — ContentHash should now be populated
& $cli snapshot --output D:\temp\snap-a.json

# 3. Idempotency with ContentHash populated
& $cli snapshot --output D:\temp\snap-b.json
& $cli diff D:\temp\snap-a.json D:\temp\snap-b.json
# Expected: every section +0/-0/~0

# 4. Verify ContentHash is no longer empty in the JSON
(Get-Content D:\temp\snap-a.json | ConvertFrom-Json).sheets | Select-Object -First 3 number, metaHash, contentHash
```

Expected: `contentHash` column is non-empty for every sheet (P1 wrote `""`, P2 writes real hashes).

- [ ] **Step 7.4: Create a minimal incremental-publish profile and baseline**

```powershell
# In the project dir alongside revit_cli.rvt:
@"
version: 1
exports:
  dwg-all:
    format: dwg
    sheets: [all]
    outputDir: ./exports-p2
publish:
  default:
    incremental: true
    presets: [dwg-all]
"@ | Out-File -Encoding utf8 D:\桌面\revitcli-p2-test\.revitcli.yml

# Establish baseline:
cd D:\桌面\revitcli-p2-test
& $cli snapshot --output .revitcli\last-publish.json
```

Expected: baseline saved.

- [ ] **Step 7.5: Test: no-change incremental publish**

```powershell
& $cli publish --dry-run
```

Expected: output contains `no sheets changed since baseline (content mode)` and exit code 0.

- [ ] **Step 7.6: Test: change one sheet, incremental publish picks only it**

In Revit: change ONE sheet's title parameter (any sheet — e.g. the first one, edit the "Sheet Name" parameter). Save the model.

```powershell
& $cli publish --dry-run
```

Expected: output contains `Incremental publish: 1 sheet(s) changed (content mode).` and the dry-run preview line mentions only that one sheet number (not `all` or the full sheet list).

- [ ] **Step 7.7: Cleanup and revert Revit change**

Undo the sheet title change in Revit so the .rvt isn't dirty, or skip saving. Close Revit without saving.

- [ ] **Step 7.8: Bump version**

Replace in `src/RevitCli/RevitCli.csproj`:

```xml
    <Version>1.1.0</Version>
```

with

```xml
    <Version>1.2.0</Version>
```

- [ ] **Step 7.9: Update CHANGELOG.md**

Insert after the `# Changelog` header and before `## [1.1.0]` entry:

```markdown
## [1.2.0] - 2026-XX-XX

Model-as-Code Phase 2 — incremental publish. Pairs `publish --since` with the
snapshot/diff infrastructure from v1.1.0 so a 50-sheet project can re-export
only the 3 sheets that changed, not the whole set.

### Added

- **`revitcli publish --since SNAPSHOT`** — diff a baseline snapshot against
  the current model and narrow each export preset's sheet selector to only the
  changed sheets. Options: `--since-mode content|meta` (default content),
  `--update-baseline` (rewrite the baseline on successful publish).
- **Profile `publish.<pipeline>.incremental: true`** — enable incremental
  publish by default. Baseline path defaults to `.revitcli/last-publish.json`
  and can be overridden with `publish.<pipeline>.baselinePath: <path>`.
  `sinceMode: content|meta` picks the diff granularity.
- **`SnapshotSheet.ContentHash`** — now populated (empty in v1.1.0). For each
  sheet, hashes its `MetaHash` + the element hashes of every non-type element
  in scope of each placed view. Skipped in `--summary-only` mode.
- **`SnapshotHasher.HashSheetContent`** — stable hash helper for sheet
  content, shared between CLI and addin.
- **`BaselineManager`** — atomic read/write of baseline snapshot files
  (tmp-then-rename), used by publish to persist the post-publish state.
- **`SinceMode` enum** — content vs meta; used by `SnapshotDiffer.Diff`'s new
  optional `sinceMode` parameter.

### Changed

- **`SnapshotDiffer.Diff`** — new signature accepts `SinceMode sinceMode =
  SinceMode.Meta`. Existing callers (v1.1.0 `revitcli diff` command) keep
  MetaHash-only behavior; new P2 call sites pass `SinceMode.Content`
  explicitly. A P1 baseline (empty ContentHash) falls back to MetaHash
  comparison automatically — no schema bump, no forced baseline rebuild.

### Backward compatibility

- `schemaVersion` stays at `1`. A v1.1.0 baseline diffs cleanly against a
  v1.2.0 snapshot (content mode gracefully degrades to meta for sheets where
  either side has empty ContentHash).
- `revitcli diff` command output format unchanged.
- `revitcli snapshot` output is bit-identical for sheet MetaHash; only
  `contentHash` field transitions from `""` to real hashes.

### Known Carry-forward

- ContentHash does not honor Visibility/Graphics overrides — hiding a wall
  via V/G won't change `ContentHash`. This is intentional for performance;
  documented on `SnapshotSheet.ContentHash`.
- No progress signal during snapshot. Typical 100-sheet model should complete
  in <30s; if you hit this limit, open an issue.
- `--verbose` on `snapshot` and `--severity` on `diff` still unimplemented
  (carry-forward from v1.1.0).

Spec: [docs/superpowers/specs/2026-04-23-model-as-code-design.md](docs/superpowers/specs/2026-04-23-model-as-code-design.md)
Plan: [docs/superpowers/plans/2026-04-23-publish-since-incremental.md](docs/superpowers/plans/2026-04-23-publish-since-incremental.md)

```

Fix the date placeholder after all manual E2E steps pass.

- [ ] **Step 7.10: Commit + tag**

```bash
git add CHANGELOG.md src/RevitCli/RevitCli.csproj
git commit -m "docs: CHANGELOG + csproj version bump for v1.2.0"
git tag -a v1.2.0 -m "v1.2.0 — publish --since incremental (Model-as-Code P2)"
```

- [ ] **Step 7.11: Push branch and tag**

```bash
git push origin feat/model-as-code-p2
git push origin v1.2.0
```

Expected: both push via the persisted gh credential helper.

---

## Task 8: Final branch-wide review before merge

- [ ] **Step 8.1: Dispatch a final code reviewer**

Controller dispatches a `superpowers:code-reviewer` subagent to review the full branch diff `main..feat/model-as-code-p2`. Give it:

- Head SHA and the commit list
- Spec and plan references
- The P2 key decisions (schema compat, perf, visibility) so it can check the implementation matches the decisions
- Pre-verified state: CLI tests passing, addin build clean, E2E on Revit 2026 pass

- [ ] **Step 8.2: Address any Critical / Important findings**

Fix in a new commit; do not amend. Re-review loop if needed.

- [ ] **Step 8.3: Open PR**

```bash
gh pr create --base main --head feat/model-as-code-p2 --title "feat: model-as-code phase 2 — publish --since (v1.2.0)" --body "$(cat <<'EOF'
## Summary

- Adds `revitcli publish --since SNAPSHOT` — incremental export based on snapshot diff
- Fills `SnapshotSheet.ContentHash` (was empty in v1.1.0)
- New profile fields: `publish.<pipeline>.incremental / baselinePath / sinceMode`
- Atomic baseline I/O via `BaselineManager`

## Test plan

- [x] CLI unit tests: N/N pass on Windows (N to be filled at PR time)
- [x] Addin build: 0 errors on Windows (Revit 2026)
- [x] E2E on `revit_cli.rvt`: snapshot populates ContentHash, idempotent diff is clean, incremental publish correctly filters to changed sheets only
- [x] Schema compat verified: v1.1.0 baseline → v1.2.0 snapshot falls back to MetaHash cleanly

## Reference

- Design spec: [docs/superpowers/specs/2026-04-23-model-as-code-design.md](docs/superpowers/specs/2026-04-23-model-as-code-design.md)
- Implementation plan: [docs/superpowers/plans/2026-04-23-publish-since-incremental.md](docs/superpowers/plans/2026-04-23-publish-since-incremental.md)
- Release notes: [CHANGELOG.md](CHANGELOG.md#120---2026-xx-xx)

Tagged as v1.2.0.
EOF
)"
```

---

## Self-Review Notes

**Spec coverage check:**
- ✅ `publish --since FILE` — Task 5
- ✅ `--since-mode content|meta` — Task 2 (SinceMode enum + differ), Task 5 (flag wiring)
- ✅ `--update-baseline` — Task 5
- ✅ Profile fields `incremental` / `baselinePath` / `sinceMode` — Task 1
- ✅ `SnapshotSheet.ContentHash` populated — Task 3
- ✅ `SnapshotHasher.HashSheetContent` — Task 2
- ✅ `BaselineManager` — Task 4
- ✅ Schema compat fallback — Task 2 (SheetChanged empty-ContentHash handling)
- ✅ End-to-end + CHANGELOG + tag — Task 7
- ✅ Final branch review — Task 8

**Three open questions answered in the plan header:** Q1 schema=1+fallback, Q2 accept-and-measure, Q3 don't-honor-V/G. Each has a rationale paragraph.

**Placeholder scan:**
- No "TBD" or "implement later" in code/command steps.
- Step 7.9 CHANGELOG has `2026-XX-XX` placeholder — deliberately so the filler puts in the actual release day after E2E passes.
- Step 7.3 expects `contentHash` non-empty after P2 — testable assertion, not a placeholder.

**Type consistency:**
- `SinceMode` in CLI (`namespace RevitCli.Output`) used by both `SnapshotDiffer` (CLI) and `PublishCommand` (CLI). Not in shared — differ lives in CLI-only.
- `SnapshotHasher.HashSheetContent` in shared (netstandard2.0) — consumed by addin only; no CLI caller.
- `BaselineManager.Load/Save` signatures (`string` path, `ModelSnapshot?` return) used consistently in Task 5.
- Element ID type is `long` throughout; never `int`.

**Risks:**
- Task 3's `ComputeSheetContentHash` lives inside a `_bridge.InvokeAsync` lambda — it runs on the Revit main thread. All Revit API calls (`FilteredElementCollector(doc, viewId)`) are legal there. No thread-affinity bug.
- Task 5's baseline auto-update runs **before** journal/receipt write — if the baseline write fails, we still journal/receipt the publish as successful. Acceptable: baseline failure is a warning, not a publish failure (user's .rvt is already exported correctly).
- E2E Task 7 modifies Revit state (user needs to change a sheet title then undo). No risk of unexpected persistence — user explicitly closes without saving.

---

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-04-23-publish-since-incremental.md`. Two execution options:

**1. Subagent-Driven** — Fresh subagent per task, two-stage review between tasks.

**2. Inline Execution** — Controller writes code, every 2-3 commits a batch code-quality review (matches what we did for P1 batch B).

Which approach?

Given P1 experience: all tasks here are small, plan-prescribed, and the controller (me) is fastest. Recommend **Option 2** (Inline + batch review) unless you want fresh isolation for the larger `PublishCommand` refactor in Task 5 — in which case dispatch a single implementer subagent for just Task 5.
