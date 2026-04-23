# Snapshot + Diff Implementation Plan (Model-as-Code Phase 1)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add `revitcli snapshot` and `revitcli diff` commands that capture a Revit model's semantic state as versioned JSON and compute differences between snapshots. Ships as v1.1.

**Architecture:** CLI calls POST `/api/snapshot` with a `SnapshotRequest`; addin's `SnapshotController` delegates to `RealRevitOperations.CaptureSnapshotAsync` which walks the model on the Revit main thread (via `RevitBridge`) and returns a `ModelSnapshot` DTO. `revitcli diff` is pure CLI — reads two JSON files, computes differences via `SnapshotDiffer`, renders via `DiffRenderer`. Element and schedule hashes computed using a shared `SnapshotHasher` so CLI can test hash stability without Revit.

**Tech Stack:** .NET 8 / netstandard2.0 shared DTOs / EmbedIO HTTP / xUnit + FakeHttpHandler / System.CommandLine / Spectre.Console for interactive TTY / Newtonsoft not used — `System.Text.Json` throughout.

**Non-goals in Phase 1 (deferred to P2):** `SnapshotSheet.ContentHash` computation (placed-view element aggregation), `publish --since`, profile `incremental` flag.

**Spec:** `docs/superpowers/specs/2026-04-23-model-as-code-design.md`

---

## File Structure

### New files

**Shared DTOs** (`shared/RevitCli.Shared/`)
- `ModelSnapshot.cs` — all SnapshotXxx DTO classes in one file
- `SnapshotRequest.cs` — addin input DTO
- `SnapshotDiff.cs` — diff result DTOs (CategoryDiff, ModifiedItem, ParamChange, etc.)
- `SnapshotHasher.cs` — static pure-C# hash helper (usable by both addin and CLI tests)

**CLI** (`src/RevitCli/`)
- `Output/SnapshotDiffer.cs` — pure C# diff algorithm
- `Output/DiffRenderer.cs` — table/json/markdown output
- `Commands/SnapshotCommand.cs` — `revitcli snapshot`
- `Commands/DiffCommand.cs` — `revitcli diff`

**Addin** (`src/RevitCli.Addin/`)
- `Handlers/SnapshotController.cs` — POST `/api/snapshot`

**Tests** (`tests/RevitCli.Tests/`)
- `Shared/SnapshotHasherTests.cs`
- `Shared/SnapshotDtoTests.cs` — roundtrip serialization
- `Output/SnapshotDifferTests.cs`
- `Output/DiffRendererTests.cs`
- `Commands/SnapshotCommandTests.cs`
- `Commands/DiffCommandTests.cs`

### Modified files

- `shared/RevitCli.Shared/IRevitOperations.cs` — add `CaptureSnapshotAsync`
- `src/RevitCli.Addin/Services/PlaceholderRevitOperations.cs` — fixture impl
- `src/RevitCli.Addin/Services/RealRevitOperations.cs` — real impl
- `src/RevitCli.Addin/Server/ApiServer.cs` — register controller
- `src/RevitCli/Client/RevitClient.cs` — HTTP method
- `src/RevitCli/Commands/CliCommandCatalog.cs` — register 2 new commands
- `src/RevitCli/Commands/CompletionsCommand.cs` — bash/zsh/pwsh autocomplete
- `tests/RevitCli.Addin.Tests/Integration/ProtocolTests.cs` — snapshot endpoint roundtrip
- `tests/RevitCli.Tests/Commands/CliCommandCatalogTests.cs` — assert 2 new commands registered
- `tests/RevitCli.Tests/Commands/CompletionsCommandTests.cs` — assert autocomplete updated
- `CHANGELOG.md`

---

## Task 1: Shared DTOs

**Files:**
- Create: `shared/RevitCli.Shared/ModelSnapshot.cs`
- Create: `shared/RevitCli.Shared/SnapshotRequest.cs`
- Create: `shared/RevitCli.Shared/SnapshotDiff.cs`
- Create: `tests/RevitCli.Tests/Shared/SnapshotDtoTests.cs`

- [ ] **Step 1.1: Write failing roundtrip test for `ModelSnapshot`**

Create `tests/RevitCli.Tests/Shared/SnapshotDtoTests.cs`:

```csharp
using System.Collections.Generic;
using System.Text.Json;
using RevitCli.Shared;
using Xunit;

namespace RevitCli.Tests.Shared;

public class SnapshotDtoTests
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public void ModelSnapshot_Roundtrip_PreservesAllFields()
    {
        var original = new ModelSnapshot
        {
            SchemaVersion = 1,
            TakenAt = "2026-04-23T10:00:00Z",
            Revit = new SnapshotRevit { Version = "2026", Document = "test", DocumentPath = "D:\\test.rvt" },
            Model = new SnapshotModel { SizeBytes = 100, FileHash = "abc" },
            Categories = new Dictionary<string, List<SnapshotElement>>
            {
                ["walls"] = new() { new SnapshotElement { Id = 1, Name = "W1", TypeName = "T1",
                    Parameters = new() { ["Mark"] = "A" }, Hash = "h1" } }
            },
            Sheets = new() { new SnapshotSheet { Number = "A-01", Name = "Plan",
                ViewId = 99, PlacedViewIds = new() { 1, 2 },
                Parameters = new() { ["Revision"] = "v1" }, MetaHash = "mh", ContentHash = "" } },
            Schedules = new() { new SnapshotSchedule { Id = 55, Name = "S1",
                Category = "walls", RowCount = 3, Hash = "sh" } },
            Summary = new SnapshotSummary
            {
                ElementCounts = new() { ["walls"] = 1 },
                SheetCount = 1, ScheduleCount = 1
            }
        };

        var json = JsonSerializer.Serialize(original);
        var restored = JsonSerializer.Deserialize<ModelSnapshot>(json, JsonOpts)!;

        Assert.Equal(1, restored.SchemaVersion);
        Assert.Equal("2026-04-23T10:00:00Z", restored.TakenAt);
        Assert.Equal("2026", restored.Revit.Version);
        Assert.Single(restored.Categories);
        Assert.Equal("W1", restored.Categories["walls"][0].Name);
        Assert.Equal("A", restored.Categories["walls"][0].Parameters["Mark"]);
        Assert.Single(restored.Sheets);
        Assert.Equal("A-01", restored.Sheets[0].Number);
        Assert.Equal(2, restored.Sheets[0].PlacedViewIds.Count);
        Assert.Single(restored.Schedules);
        Assert.Equal(1, restored.Summary.ElementCounts["walls"]);
    }

    [Fact]
    public void SnapshotRequest_DefaultsAreCorrect()
    {
        var r = new SnapshotRequest();
        Assert.Null(r.IncludeCategories);
        Assert.True(r.IncludeSheets);
        Assert.True(r.IncludeSchedules);
        Assert.False(r.SummaryOnly);
    }

    [Fact]
    public void SnapshotDiff_Roundtrip_PreservesAllSections()
    {
        var d = new SnapshotDiff
        {
            SchemaVersion = 1,
            From = "a.json",
            To = "b.json",
            Categories = new() {
                ["walls"] = new CategoryDiff {
                    Added = new() { new AddedItem { Id = 5, Key = "walls:W5", Name = "W5" } },
                    Modified = new() { new ModifiedItem {
                        Id = 1, Key = "walls:W1",
                        Changed = new() { ["Mark"] = new ParamChange { From = "", To = "A" } },
                        OldHash = "h1", NewHash = "h2"
                    } }
                }
            }
        };

        var json = JsonSerializer.Serialize(d);
        var restored = JsonSerializer.Deserialize<SnapshotDiff>(json, JsonOpts)!;

        Assert.Equal("a.json", restored.From);
        Assert.Single(restored.Categories["walls"].Added);
        Assert.Equal("A", restored.Categories["walls"].Modified[0].Changed["Mark"].To);
    }
}
```

- [ ] **Step 1.2: Run test to verify it fails to compile**

```
dotnet test tests/RevitCli.Tests/ --filter "FullyQualifiedName~SnapshotDtoTests"
```

Expected: compile error — types `ModelSnapshot`, `SnapshotRequest`, `SnapshotDiff` and friends don't exist yet.

- [ ] **Step 1.3: Create `shared/RevitCli.Shared/ModelSnapshot.cs`**

```csharp
using System.Collections.Generic;

namespace RevitCli.Shared;

public class ModelSnapshot
{
    public int SchemaVersion { get; set; } = 1;
    public string TakenAt { get; set; } = "";
    public SnapshotRevit Revit { get; set; } = new();
    public SnapshotModel Model { get; set; } = new();
    public Dictionary<string, List<SnapshotElement>> Categories { get; set; } = new();
    public List<SnapshotSheet> Sheets { get; set; } = new();
    public List<SnapshotSchedule> Schedules { get; set; } = new();
    public SnapshotSummary Summary { get; set; } = new();
}

public class SnapshotRevit
{
    public string Version { get; set; } = "";
    public string Document { get; set; } = "";
    public string DocumentPath { get; set; } = "";
}

public class SnapshotModel
{
    public long SizeBytes { get; set; }
    public string FileHash { get; set; } = "";
}

public class SnapshotElement
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string TypeName { get; set; } = "";
    public Dictionary<string, string> Parameters { get; set; } = new();
    public string Hash { get; set; } = "";
}

public class SnapshotSheet
{
    public string Number { get; set; } = "";
    public string Name { get; set; } = "";
    public long ViewId { get; set; }
    public List<long> PlacedViewIds { get; set; } = new();
    public Dictionary<string, string> Parameters { get; set; } = new();
    public string MetaHash { get; set; } = "";
    public string ContentHash { get; set; } = "";
}

public class SnapshotSchedule
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public int RowCount { get; set; }
    public string Hash { get; set; } = "";
}

public class SnapshotSummary
{
    public Dictionary<string, int> ElementCounts { get; set; } = new();
    public int SheetCount { get; set; }
    public int ScheduleCount { get; set; }
}
```

- [ ] **Step 1.4: Create `shared/RevitCli.Shared/SnapshotRequest.cs`**

```csharp
using System.Collections.Generic;

namespace RevitCli.Shared;

public class SnapshotRequest
{
    public List<string>? IncludeCategories { get; set; }
    public bool IncludeSheets { get; set; } = true;
    public bool IncludeSchedules { get; set; } = true;
    public bool SummaryOnly { get; set; } = false;
}
```

- [ ] **Step 1.5: Create `shared/RevitCli.Shared/SnapshotDiff.cs`**

```csharp
using System.Collections.Generic;

namespace RevitCli.Shared;

public class SnapshotDiff
{
    public int SchemaVersion { get; set; } = 1;
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public Dictionary<string, CategoryDiff> Categories { get; set; } = new();
    public CategoryDiff Sheets { get; set; } = new();
    public CategoryDiff Schedules { get; set; } = new();
    public DiffSummary Summary { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}

public class CategoryDiff
{
    public List<AddedItem> Added { get; set; } = new();
    public List<RemovedItem> Removed { get; set; } = new();
    public List<ModifiedItem> Modified { get; set; } = new();
}

public class AddedItem
{
    public long Id { get; set; }
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
}

public class RemovedItem
{
    public long Id { get; set; }
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
}

public class ModifiedItem
{
    public long Id { get; set; }
    public string Key { get; set; } = "";
    public Dictionary<string, ParamChange> Changed { get; set; } = new();
    public string? OldHash { get; set; }
    public string? NewHash { get; set; }
}

public class ParamChange
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
}

public class DiffSummary
{
    public Dictionary<string, CategoryCount> PerCategory { get; set; } = new();
    public CategoryCount Sheets { get; set; } = new();
    public CategoryCount Schedules { get; set; } = new();
}

public class CategoryCount
{
    public int Added { get; set; }
    public int Removed { get; set; }
    public int Modified { get; set; }
}
```

- [ ] **Step 1.6: Run tests — verify pass**

```
dotnet test tests/RevitCli.Tests/ --filter "FullyQualifiedName~SnapshotDtoTests"
```

Expected: 3 passed.

- [ ] **Step 1.7: Commit**

```bash
git add shared/RevitCli.Shared/ModelSnapshot.cs \
        shared/RevitCli.Shared/SnapshotRequest.cs \
        shared/RevitCli.Shared/SnapshotDiff.cs \
        tests/RevitCli.Tests/Shared/SnapshotDtoTests.cs
git commit -m "feat(shared): add Model-as-Code snapshot + diff DTOs (P1)"
```

---

## Task 2: SnapshotHasher (pure C# hash helper)

**Files:**
- Create: `shared/RevitCli.Shared/SnapshotHasher.cs`
- Create: `tests/RevitCli.Tests/Shared/SnapshotHasherTests.cs`

- [ ] **Step 2.1: Write failing hash tests**

Create `tests/RevitCli.Tests/Shared/SnapshotHasherTests.cs`:

```csharp
using System.Collections.Generic;
using RevitCli.Shared;
using Xunit;

namespace RevitCli.Tests.Shared;

public class SnapshotHasherTests
{
    [Fact]
    public void HashElement_IsStable_ForSameInput()
    {
        var el = new SnapshotElement
        {
            Id = 337596, Name = "Wall", TypeName = "200mm",
            Parameters = new() { ["Mark"] = "W01", ["Length"] = "5000" }
        };
        var h1 = SnapshotHasher.HashElement(el);
        var h2 = SnapshotHasher.HashElement(el);
        Assert.Equal(h1, h2);
        Assert.Equal(16, h1.Length);
    }

    [Fact]
    public void HashElement_ChangesWhenParamValueChanges()
    {
        var a = new SnapshotElement { Id = 1, Parameters = new() { ["Mark"] = "A" } };
        var b = new SnapshotElement { Id = 1, Parameters = new() { ["Mark"] = "B" } };
        Assert.NotEqual(SnapshotHasher.HashElement(a), SnapshotHasher.HashElement(b));
    }

    [Fact]
    public void HashElement_StableAcrossParameterInsertionOrder()
    {
        var a = new SnapshotElement { Id = 1,
            Parameters = new() { ["Mark"] = "A", ["Length"] = "5" } };
        var b = new SnapshotElement { Id = 1,
            Parameters = new() { ["Length"] = "5", ["Mark"] = "A" } };
        Assert.Equal(SnapshotHasher.HashElement(a), SnapshotHasher.HashElement(b));
    }

    [Fact]
    public void HashElement_HandlesNewlinesInValues()
    {
        var a = new SnapshotElement { Id = 1, Parameters = new() { ["Note"] = "line1\nline2" } };
        var b = new SnapshotElement { Id = 1, Parameters = new() { ["Note"] = "line1\\nline2" } };
        // Escape should preserve distinction
        Assert.NotEqual(SnapshotHasher.HashElement(a), SnapshotHasher.HashElement(b));
    }

    [Fact]
    public void HashSheetMeta_Includes_NumberNameAndParameters()
    {
        var s = new SnapshotSheet
        {
            Number = "A-01", Name = "Plan", ViewId = 99,
            Parameters = new() { ["Revision"] = "v1" }
        };
        var h = SnapshotHasher.HashSheetMeta(s);
        Assert.Equal(16, h.Length);
    }

    [Fact]
    public void HashSchedule_StableForSameColumnsAndRows()
    {
        var cols = new List<string> { "Mark", "Width" };
        var rows = new List<Dictionary<string, string>>
        {
            new() { ["Mark"] = "D1", ["Width"] = "900" },
            new() { ["Mark"] = "D2", ["Width"] = "800" }
        };
        var h1 = SnapshotHasher.HashSchedule("Doors", "Door Schedule", cols, rows);
        var h2 = SnapshotHasher.HashSchedule("Doors", "Door Schedule", cols, rows);
        Assert.Equal(h1, h2);
    }

    [Fact]
    public void HashSchedule_DiffersWhenRowsReorder()
    {
        // Row order is significant (schedule sort rule change should be detected)
        var cols = new List<string> { "Mark" };
        var rows1 = new List<Dictionary<string, string>>
        {
            new() { ["Mark"] = "A" }, new() { ["Mark"] = "B" }
        };
        var rows2 = new List<Dictionary<string, string>>
        {
            new() { ["Mark"] = "B" }, new() { ["Mark"] = "A" }
        };
        Assert.NotEqual(
            SnapshotHasher.HashSchedule("Doors", "S", cols, rows1),
            SnapshotHasher.HashSchedule("Doors", "S", cols, rows2));
    }
}
```

- [ ] **Step 2.2: Run tests — verify fail to compile**

```
dotnet test tests/RevitCli.Tests/ --filter "FullyQualifiedName~SnapshotHasherTests"
```

Expected: compile error — `SnapshotHasher` doesn't exist.

- [ ] **Step 2.3: Create `shared/RevitCli.Shared/SnapshotHasher.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace RevitCli.Shared;

public static class SnapshotHasher
{
    private const int HashLength = 16;

    public static string HashElement(SnapshotElement e)
    {
        var sb = new StringBuilder();
        sb.Append("id=").Append(e.Id).Append('\n');
        sb.Append("name=").Append(Escape(e.Name ?? "")).Append('\n');
        sb.Append("typeName=").Append(Escape(e.TypeName ?? "")).Append('\n');
        foreach (var kv in (e.Parameters ?? new Dictionary<string, string>())
                 .OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            sb.Append(Escape(kv.Key)).Append('=').Append(Escape(kv.Value ?? "")).Append('\n');
        }
        return Sha256Short(sb.ToString());
    }

    public static string HashSheetMeta(SnapshotSheet s)
    {
        var sb = new StringBuilder();
        sb.Append("number=").Append(Escape(s.Number ?? "")).Append('\n');
        sb.Append("name=").Append(Escape(s.Name ?? "")).Append('\n');
        sb.Append("viewId=").Append(s.ViewId).Append('\n');
        foreach (var kv in (s.Parameters ?? new Dictionary<string, string>())
                 .OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            sb.Append(Escape(kv.Key)).Append('=').Append(Escape(kv.Value ?? "")).Append('\n');
        }
        return Sha256Short(sb.ToString());
    }

    public static string HashSchedule(
        string category,
        string name,
        List<string> columns,
        List<Dictionary<string, string>> rows)
    {
        var sb = new StringBuilder();
        sb.Append("category=").Append(Escape(category ?? "")).Append('\n');
        sb.Append("name=").Append(Escape(name ?? "")).Append('\n');
        sb.Append("columns=").Append(string.Join("|", (columns ?? new List<string>()).Select(Escape))).Append('\n');
        foreach (var row in rows ?? new List<Dictionary<string, string>>())
        {
            var line = string.Join("|",
                (columns ?? new List<string>()).Select(c =>
                    row.TryGetValue(c, out var v) ? Escape(v ?? "") : ""));
            sb.Append(line).Append('\n');
        }
        return Sha256Short(sb.ToString());
    }

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\n", "\\n");

    private static string Sha256Short(string content)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(content));
        var sb = new StringBuilder(HashLength);
        for (int i = 0; i < HashLength / 2; i++)
            sb.Append(bytes[i].ToString("x2"));
        return sb.ToString();
    }
}
```

- [ ] **Step 2.4: Run tests — verify pass**

```
dotnet test tests/RevitCli.Tests/ --filter "FullyQualifiedName~SnapshotHasherTests"
```

Expected: 7 passed.

- [ ] **Step 2.5: Commit**

```bash
git add shared/RevitCli.Shared/SnapshotHasher.cs \
        tests/RevitCli.Tests/Shared/SnapshotHasherTests.cs
git commit -m "feat(shared): add SnapshotHasher with stable SHA256-16 hashes (P1)"
```

---

## Task 3: IRevitOperations interface + Placeholder fixture

**Files:**
- Modify: `shared/RevitCli.Shared/IRevitOperations.cs`
- Modify: `src/RevitCli.Addin/Services/PlaceholderRevitOperations.cs`

- [ ] **Step 3.1: Read existing `IRevitOperations.cs` to find insertion point**

```
grep -n "Task<" shared/RevitCli.Shared/IRevitOperations.cs
```

Insert new method signature after the existing schedule methods. Pattern used by existing methods:
```csharp
Task<SomeResult> SomeMethodAsync(SomeRequest request);
```

- [ ] **Step 3.2: Modify `shared/RevitCli.Shared/IRevitOperations.cs` — add method**

Add at end of interface body, before closing brace:

```csharp
    Task<ModelSnapshot> CaptureSnapshotAsync(SnapshotRequest request);
```

- [ ] **Step 3.3: Modify `src/RevitCli.Addin/Services/PlaceholderRevitOperations.cs` — add fixture impl**

Add this method at the end of the class:

```csharp
    public Task<ModelSnapshot> CaptureSnapshotAsync(SnapshotRequest request)
    {
        var snapshot = new ModelSnapshot
        {
            SchemaVersion = 1,
            TakenAt = "2026-04-23T00:00:00Z",
            Revit = new SnapshotRevit
            {
                Version = "2025",
                Document = "Placeholder.rvt",
                DocumentPath = "/tmp/Placeholder.rvt"
            },
            Model = new SnapshotModel { SizeBytes = 0, FileHash = "" },
            Categories = new Dictionary<string, List<SnapshotElement>>
            {
                ["walls"] = new()
                {
                    new SnapshotElement
                    {
                        Id = 1001, Name = "Placeholder wall", TypeName = "W1",
                        Parameters = new() { ["Mark"] = "W1" },
                        Hash = "placeholder111111"
                    }
                }
            },
            Sheets = new()
            {
                new SnapshotSheet
                {
                    Number = "A-01", Name = "Placeholder sheet",
                    ViewId = 2001, PlacedViewIds = new() { 3001 },
                    Parameters = new(),
                    MetaHash = "placeholder_sheet",
                    ContentHash = ""
                }
            },
            Schedules = new()
            {
                new SnapshotSchedule
                {
                    Id = 4001, Name = "Placeholder schedule",
                    Category = "walls", RowCount = 1, Hash = "placeholder_sch"
                }
            },
            Summary = new SnapshotSummary
            {
                ElementCounts = new() { ["walls"] = 1 },
                SheetCount = 1, ScheduleCount = 1
            }
        };
        return Task.FromResult(snapshot);
    }
```

- [ ] **Step 3.4: Verify compile**

```
dotnet build src/RevitCli/RevitCli.csproj
```

Expected: Success. (Addin doesn't build on Linux; that's fine — CLI + shared must build.)

- [ ] **Step 3.5: Commit**

```bash
git add shared/RevitCli.Shared/IRevitOperations.cs \
        src/RevitCli.Addin/Services/PlaceholderRevitOperations.cs
git commit -m "feat(shared,addin): add CaptureSnapshotAsync to IRevitOperations + placeholder (P1)"
```

---

## Task 4: SnapshotDiffer (pure C# diff algorithm)

**Files:**
- Create: `src/RevitCli/Output/SnapshotDiffer.cs`
- Create: `tests/RevitCli.Tests/Output/SnapshotDifferTests.cs`

- [ ] **Step 4.1: Write failing diff tests**

Create `tests/RevitCli.Tests/Output/SnapshotDifferTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using RevitCli.Output;
using RevitCli.Shared;
using Xunit;

namespace RevitCli.Tests.Output;

public class SnapshotDifferTests
{
    private static ModelSnapshot MakeSnap(params (string cat, long id, string mark, string hash)[] elements)
    {
        var snap = new ModelSnapshot
        {
            SchemaVersion = 1,
            TakenAt = "2026-04-23T00:00:00Z",
            Revit = new SnapshotRevit { Version = "2026", Document = "d", DocumentPath = "/a.rvt" }
        };
        foreach (var (cat, id, mark, hash) in elements)
        {
            if (!snap.Categories.TryGetValue(cat, out var list))
                snap.Categories[cat] = list = new List<SnapshotElement>();
            list.Add(new SnapshotElement
            {
                Id = id, Name = $"E{id}", Parameters = new() { ["Mark"] = mark }, Hash = hash
            });
        }
        return snap;
    }

    [Fact]
    public void Diff_IdenticalSnapshots_HasNoChanges()
    {
        var a = MakeSnap(("walls", 1, "A", "h1"));
        var b = MakeSnap(("walls", 1, "A", "h1"));
        var d = SnapshotDiffer.Diff(a, b);
        Assert.Empty(d.Categories["walls"].Added);
        Assert.Empty(d.Categories["walls"].Removed);
        Assert.Empty(d.Categories["walls"].Modified);
    }

    [Fact]
    public void Diff_AddedElement_AppearsInAddedList()
    {
        var a = MakeSnap(("walls", 1, "A", "h1"));
        var b = MakeSnap(("walls", 1, "A", "h1"), ("walls", 2, "B", "h2"));
        var d = SnapshotDiffer.Diff(a, b);
        var added = Assert.Single(d.Categories["walls"].Added);
        Assert.Equal(2, added.Id);
    }

    [Fact]
    public void Diff_RemovedElement_AppearsInRemovedList()
    {
        var a = MakeSnap(("walls", 1, "A", "h1"), ("walls", 2, "B", "h2"));
        var b = MakeSnap(("walls", 1, "A", "h1"));
        var d = SnapshotDiffer.Diff(a, b);
        var removed = Assert.Single(d.Categories["walls"].Removed);
        Assert.Equal(2, removed.Id);
    }

    [Fact]
    public void Diff_HashChanged_AppearsAsModifiedWithParamDelta()
    {
        var a = MakeSnap(("walls", 1, "OldMark", "h1"));
        var b = MakeSnap(("walls", 1, "NewMark", "h2"));
        var d = SnapshotDiffer.Diff(a, b);
        var mod = Assert.Single(d.Categories["walls"].Modified);
        Assert.Equal(1, mod.Id);
        Assert.Equal("OldMark", mod.Changed["Mark"].From);
        Assert.Equal("NewMark", mod.Changed["Mark"].To);
    }

    [Fact]
    public void Diff_SchemaVersionMismatch_Throws()
    {
        var a = MakeSnap();
        var b = MakeSnap();
        b.SchemaVersion = 2;
        var ex = Assert.Throws<InvalidOperationException>(() => SnapshotDiffer.Diff(a, b));
        Assert.Contains("Schema mismatch", ex.Message);
    }

    [Fact]
    public void Diff_DocumentPathMismatch_AddsWarning()
    {
        var a = MakeSnap();
        var b = MakeSnap();
        b.Revit.DocumentPath = "/different.rvt";
        var d = SnapshotDiffer.Diff(a, b);
        Assert.Contains(d.Warnings, w => w.Contains("DocumentPath"));
    }

    [Fact]
    public void Diff_NewCategoryInB_YieldsAllElementsAsAdded()
    {
        var a = MakeSnap(("walls", 1, "A", "h1"));
        var b = MakeSnap(("walls", 1, "A", "h1"), ("doors", 10, "D", "d1"));
        var d = SnapshotDiffer.Diff(a, b);
        Assert.Single(d.Categories["doors"].Added);
        Assert.Empty(d.Categories["walls"].Added);
    }

    [Fact]
    public void Diff_Sheets_KeyedByNumberNotId()
    {
        var a = MakeSnap();
        a.Sheets.Add(new SnapshotSheet { Number = "A-01", Name = "Old", ViewId = 1, MetaHash = "h1" });
        var b = MakeSnap();
        b.Sheets.Add(new SnapshotSheet { Number = "A-01", Name = "New", ViewId = 2, MetaHash = "h2" });
        var d = SnapshotDiffer.Diff(a, b);
        var mod = Assert.Single(d.Sheets.Modified);
        Assert.Equal("sheet:A-01", mod.Key);
    }

    [Fact]
    public void Diff_Summary_CountsPerCategory()
    {
        var a = MakeSnap(("walls", 1, "A", "h1"));
        var b = MakeSnap(("walls", 1, "A", "h1"),
                          ("walls", 2, "B", "h2"),
                          ("doors", 10, "D", "dh1"));
        var d = SnapshotDiffer.Diff(a, b);
        Assert.Equal(1, d.Summary.PerCategory["walls"].Added);
        Assert.Equal(1, d.Summary.PerCategory["doors"].Added);
    }

    [Fact]
    public void Diff_FromAndToFieldsSetFromLabels()
    {
        var a = MakeSnap();
        var b = MakeSnap();
        var d = SnapshotDiffer.Diff(a, b, "baseline.json", "current.json");
        Assert.Equal("baseline.json", d.From);
        Assert.Equal("current.json", d.To);
    }
}
```

- [ ] **Step 4.2: Run tests — verify fail to compile**

```
dotnet test tests/RevitCli.Tests/ --filter "FullyQualifiedName~SnapshotDifferTests"
```

Expected: compile error — `SnapshotDiffer` doesn't exist.

- [ ] **Step 4.3: Create `src/RevitCli/Output/SnapshotDiffer.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using RevitCli.Shared;

namespace RevitCli.Output;

public static class SnapshotDiffer
{
    public static SnapshotDiff Diff(ModelSnapshot from, ModelSnapshot to,
                                    string? fromLabel = null, string? toLabel = null)
    {
        if (from.SchemaVersion != to.SchemaVersion)
            throw new InvalidOperationException(
                $"Schema mismatch: from={from.SchemaVersion}, to={to.SchemaVersion}. Regenerate snapshots.");

        var result = new SnapshotDiff
        {
            SchemaVersion = from.SchemaVersion,
            From = fromLabel ?? "",
            To = toLabel ?? ""
        };

        if (!string.Equals(from.Revit.DocumentPath, to.Revit.DocumentPath, StringComparison.OrdinalIgnoreCase))
        {
            result.Warnings.Add(
                $"DocumentPath differs between snapshots: from='{from.Revit.DocumentPath}' to='{to.Revit.DocumentPath}'. Diff may be misleading.");
        }

        // Element-level diff by category
        var allCategories = new HashSet<string>(
            from.Categories.Keys.Concat(to.Categories.Keys), StringComparer.OrdinalIgnoreCase);
        foreach (var cat in allCategories.OrderBy(c => c, StringComparer.Ordinal))
        {
            var aItems = from.Categories.TryGetValue(cat, out var aList) ? aList : new List<SnapshotElement>();
            var bItems = to.Categories.TryGetValue(cat, out var bList) ? bList : new List<SnapshotElement>();
            result.Categories[cat] = DiffElementList(cat, aItems, bItems);
        }

        // Sheets — key on Number
        result.Sheets = DiffSheets(from.Sheets, to.Sheets);

        // Schedules — key on Id
        result.Schedules = DiffSchedules(from.Schedules, to.Schedules);

        // Summary
        result.Summary = BuildSummary(result);

        return result;
    }

    private static CategoryDiff DiffElementList(string categoryName,
        List<SnapshotElement> a, List<SnapshotElement> b)
    {
        var aById = a.ToDictionary(e => e.Id);
        var bById = b.ToDictionary(e => e.Id);
        var diff = new CategoryDiff();

        foreach (var id in bById.Keys.Except(aById.Keys))
        {
            var el = bById[id];
            diff.Added.Add(new AddedItem { Id = id, Key = $"{categoryName}:{el.Name}", Name = el.Name });
        }
        foreach (var id in aById.Keys.Except(bById.Keys))
        {
            var el = aById[id];
            diff.Removed.Add(new RemovedItem { Id = id, Key = $"{categoryName}:{el.Name}", Name = el.Name });
        }
        foreach (var id in aById.Keys.Intersect(bById.Keys))
        {
            var ae = aById[id];
            var be = bById[id];
            if (ae.Hash != be.Hash)
            {
                var mod = new ModifiedItem
                {
                    Id = id,
                    Key = $"{categoryName}:{be.Name}",
                    OldHash = ae.Hash, NewHash = be.Hash,
                    Changed = DiffParameters(ae.Parameters, be.Parameters)
                };
                diff.Modified.Add(mod);
            }
        }
        return diff;
    }

    private static Dictionary<string, ParamChange> DiffParameters(
        Dictionary<string, string> a, Dictionary<string, string> b)
    {
        var changes = new Dictionary<string, ParamChange>();
        var allKeys = new HashSet<string>(a.Keys.Concat(b.Keys));
        foreach (var k in allKeys)
        {
            var va = a.TryGetValue(k, out var vaVal) ? vaVal : "";
            var vb = b.TryGetValue(k, out var vbVal) ? vbVal : "";
            if (va != vb)
                changes[k] = new ParamChange { From = va, To = vb };
        }
        return changes;
    }

    private static CategoryDiff DiffSheets(List<SnapshotSheet> a, List<SnapshotSheet> b)
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
            if (sa.MetaHash != sb.MetaHash)
            {
                diff.Modified.Add(new ModifiedItem
                {
                    Id = sb.ViewId,
                    Key = $"sheet:{num}",
                    OldHash = sa.MetaHash, NewHash = sb.MetaHash,
                    Changed = DiffParameters(sa.Parameters, sb.Parameters)
                });
            }
        }
        return diff;
    }

    private static CategoryDiff DiffSchedules(List<SnapshotSchedule> a, List<SnapshotSchedule> b)
    {
        var aById = a.ToDictionary(s => s.Id);
        var bById = b.ToDictionary(s => s.Id);
        var diff = new CategoryDiff();

        foreach (var id in bById.Keys.Except(aById.Keys))
            diff.Added.Add(new AddedItem { Id = id, Key = $"schedule:{bById[id].Name}", Name = bById[id].Name });
        foreach (var id in aById.Keys.Except(bById.Keys))
            diff.Removed.Add(new RemovedItem { Id = id, Key = $"schedule:{aById[id].Name}", Name = aById[id].Name });
        foreach (var id in aById.Keys.Intersect(bById.Keys))
        {
            var sa = aById[id];
            var sb = bById[id];
            if (sa.Hash != sb.Hash)
            {
                diff.Modified.Add(new ModifiedItem
                {
                    Id = id,
                    Key = $"schedule:{sb.Name}",
                    OldHash = sa.Hash, NewHash = sb.Hash
                });
            }
        }
        return diff;
    }

    private static DiffSummary BuildSummary(SnapshotDiff d)
    {
        var s = new DiffSummary();
        foreach (var kv in d.Categories)
        {
            s.PerCategory[kv.Key] = new CategoryCount
            {
                Added = kv.Value.Added.Count,
                Removed = kv.Value.Removed.Count,
                Modified = kv.Value.Modified.Count
            };
        }
        s.Sheets = new CategoryCount
        {
            Added = d.Sheets.Added.Count,
            Removed = d.Sheets.Removed.Count,
            Modified = d.Sheets.Modified.Count
        };
        s.Schedules = new CategoryCount
        {
            Added = d.Schedules.Added.Count,
            Removed = d.Schedules.Removed.Count,
            Modified = d.Schedules.Modified.Count
        };
        return s;
    }
}
```

- [ ] **Step 4.4: Run tests — verify pass**

```
dotnet test tests/RevitCli.Tests/ --filter "FullyQualifiedName~SnapshotDifferTests"
```

Expected: 10 passed.

- [ ] **Step 4.5: Commit**

```bash
git add src/RevitCli/Output/SnapshotDiffer.cs \
        tests/RevitCli.Tests/Output/SnapshotDifferTests.cs
git commit -m "feat(cli): add SnapshotDiffer with element/sheet/schedule diff (P1)"
```

---

## Task 5: DiffRenderer (table / json / markdown)

**Files:**
- Create: `src/RevitCli/Output/DiffRenderer.cs`
- Create: `tests/RevitCli.Tests/Output/DiffRendererTests.cs`

- [ ] **Step 5.1: Write failing renderer tests**

Create `tests/RevitCli.Tests/Output/DiffRendererTests.cs`:

```csharp
using System.Collections.Generic;
using RevitCli.Output;
using RevitCli.Shared;
using Xunit;

namespace RevitCli.Tests.Output;

public class DiffRendererTests
{
    private static SnapshotDiff SampleDiff()
    {
        var d = new SnapshotDiff { SchemaVersion = 1, From = "a.json", To = "b.json" };
        d.Categories["walls"] = new CategoryDiff
        {
            Added = new() { new AddedItem { Id = 5, Key = "walls:W5", Name = "W5" } },
            Modified = new()
            {
                new ModifiedItem
                {
                    Id = 1, Key = "walls:W1",
                    Changed = new() { ["Mark"] = new ParamChange { From = "", To = "A" } },
                    OldHash = "h1", NewHash = "h2"
                }
            }
        };
        d.Summary.PerCategory["walls"] = new CategoryCount { Added = 1, Modified = 1 };
        return d;
    }

    [Fact]
    public void RenderTable_IncludesSummaryLine()
    {
        var output = DiffRenderer.Render(SampleDiff(), "table", maxRows: 20);
        Assert.Contains("walls", output);
        Assert.Contains("+1", output);
        Assert.Contains("~1", output);
    }

    [Fact]
    public void RenderTable_ShowsModifiedParamDelta()
    {
        var output = DiffRenderer.Render(SampleDiff(), "table", maxRows: 20);
        Assert.Contains("Mark", output);
        Assert.Contains("\"A\"", output);
    }

    [Fact]
    public void RenderJson_IsValidJson()
    {
        var output = DiffRenderer.Render(SampleDiff(), "json", maxRows: 20);
        var parsed = System.Text.Json.JsonSerializer.Deserialize<SnapshotDiff>(
            output, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal("a.json", parsed.From);
        Assert.Equal(1, parsed.Summary.PerCategory["walls"].Added);
    }

    [Fact]
    public void RenderMarkdown_StartsWithHeader()
    {
        var output = DiffRenderer.Render(SampleDiff(), "markdown", maxRows: 20);
        Assert.StartsWith("## Model changes", output);
        Assert.Contains("### Modified walls", output);
    }

    [Fact]
    public void RenderMarkdown_TruncatesAboveMaxRows()
    {
        var big = new SnapshotDiff { SchemaVersion = 1 };
        var cat = new CategoryDiff();
        for (int i = 0; i < 30; i++)
            cat.Added.Add(new AddedItem { Id = i, Key = $"walls:W{i}", Name = $"W{i}" });
        big.Categories["walls"] = cat;
        big.Summary.PerCategory["walls"] = new CategoryCount { Added = 30 };

        var output = DiffRenderer.Render(big, "markdown", maxRows: 5);
        Assert.Contains("+30", output);
        Assert.Contains("...and 25 more", output);
    }

    [Fact]
    public void Render_UnknownFormat_DefaultsToTable()
    {
        var output = DiffRenderer.Render(SampleDiff(), "xml-whatever", maxRows: 20);
        Assert.Contains("walls", output);
    }
}
```

- [ ] **Step 5.2: Run tests — verify fail**

```
dotnet test tests/RevitCli.Tests/ --filter "FullyQualifiedName~DiffRendererTests"
```

- [ ] **Step 5.3: Create `src/RevitCli/Output/DiffRenderer.cs`**

```csharp
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using RevitCli.Shared;

namespace RevitCli.Output;

public static class DiffRenderer
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static string Render(SnapshotDiff diff, string format, int maxRows)
    {
        return format?.ToLowerInvariant() switch
        {
            "json" => JsonSerializer.Serialize(diff, JsonOpts),
            "markdown" or "md" => RenderMarkdown(diff, maxRows),
            _ => RenderTable(diff, maxRows)
        };
    }

    private static string RenderTable(SnapshotDiff diff, int maxRows)
    {
        var sb = new StringBuilder();
        foreach (var w in diff.Warnings) sb.AppendLine($"[warn] {w}");

        foreach (var kv in diff.Summary.PerCategory)
        {
            var c = kv.Value;
            sb.AppendLine($"{kv.Key}: +{c.Added} / -{c.Removed} / ~{c.Modified}");
        }
        var sh = diff.Summary.Sheets;
        if (sh.Added + sh.Removed + sh.Modified > 0)
            sb.AppendLine($"sheets: +{sh.Added} / -{sh.Removed} / ~{sh.Modified}");
        var sc = diff.Summary.Schedules;
        if (sc.Added + sc.Removed + sc.Modified > 0)
            sb.AppendLine($"schedules: +{sc.Added} / -{sc.Removed} / ~{sc.Modified}");

        foreach (var kv in diff.Categories)
        {
            RenderCategorySection(sb, kv.Key, kv.Value, maxRows);
        }
        if (diff.Sheets.Added.Count + diff.Sheets.Removed.Count + diff.Sheets.Modified.Count > 0)
            RenderCategorySection(sb, "sheets", diff.Sheets, maxRows);
        if (diff.Schedules.Added.Count + diff.Schedules.Removed.Count + diff.Schedules.Modified.Count > 0)
            RenderCategorySection(sb, "schedules", diff.Schedules, maxRows);

        return sb.ToString().TrimEnd();
    }

    private static void RenderCategorySection(StringBuilder sb, string name, CategoryDiff d, int maxRows)
    {
        if (d.Added.Count == 0 && d.Removed.Count == 0 && d.Modified.Count == 0) return;
        sb.AppendLine();
        sb.AppendLine($"=== {name} ===");
        PrintList(sb, "+ ", d.Added.Count, maxRows, i => $"[{d.Added[i].Id}] {d.Added[i].Name}");
        PrintList(sb, "- ", d.Removed.Count, maxRows, i => $"[{d.Removed[i].Id}] {d.Removed[i].Name}");
        for (int i = 0; i < d.Modified.Count && i < maxRows; i++)
        {
            var m = d.Modified[i];
            sb.AppendLine($"~ [{m.Id}] {m.Key}");
            foreach (var ch in m.Changed)
                sb.AppendLine($"    {ch.Key}: \"{ch.Value.From}\" → \"{ch.Value.To}\"");
        }
        if (d.Modified.Count > maxRows)
            sb.AppendLine($"...and {d.Modified.Count - maxRows} more modified");
    }

    private static void PrintList(StringBuilder sb, string prefix, int count, int max, System.Func<int, string> fmt)
    {
        var shown = count < max ? count : max;
        for (int i = 0; i < shown; i++) sb.AppendLine(prefix + fmt(i));
        if (count > shown) sb.AppendLine($"{prefix}...and {count - shown} more");
    }

    private static string RenderMarkdown(SnapshotDiff diff, int maxRows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Model changes");
        foreach (var w in diff.Warnings) sb.AppendLine($"> ⚠ {w}").AppendLine();

        foreach (var kv in diff.Summary.PerCategory)
        {
            var c = kv.Value;
            sb.AppendLine($"**{kv.Key}**: +{c.Added} / -{c.Removed} / ~{c.Modified}");
        }
        if (diff.Summary.Sheets.Added + diff.Summary.Sheets.Removed + diff.Summary.Sheets.Modified > 0)
        {
            var s = diff.Summary.Sheets;
            sb.AppendLine($"**sheets**: +{s.Added} / -{s.Removed} / ~{s.Modified}");
        }

        foreach (var kv in diff.Categories)
        {
            RenderMdSection(sb, kv.Key, kv.Value, maxRows);
        }
        RenderMdSection(sb, "sheets", diff.Sheets, maxRows);
        RenderMdSection(sb, "schedules", diff.Schedules, maxRows);

        return sb.ToString().TrimEnd();
    }

    private static void RenderMdSection(StringBuilder sb, string name, CategoryDiff d, int maxRows)
    {
        if (d.Modified.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"### Modified {name}");
            sb.AppendLine("| Id | Key | Changed |");
            sb.AppendLine("|---|---|---|");
            var show = System.Math.Min(d.Modified.Count, maxRows);
            for (int i = 0; i < show; i++)
            {
                var m = d.Modified[i];
                var changes = new List<string>();
                foreach (var c in m.Changed)
                    changes.Add($"{c.Key}: \"{c.Value.From}\" → \"{c.Value.To}\"");
                sb.AppendLine($"| {m.Id} | {m.Key} | {string.Join("; ", changes)} |");
            }
            if (d.Modified.Count > show)
                sb.AppendLine($"\n...and {d.Modified.Count - show} more");
        }
        if (d.Added.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"### Added {name}");
            var show = System.Math.Min(d.Added.Count, maxRows);
            for (int i = 0; i < show; i++) sb.AppendLine($"- `{d.Added[i].Id}` {d.Added[i].Name}");
            if (d.Added.Count > show) sb.AppendLine($"...and {d.Added.Count - show} more");
        }
        if (d.Removed.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"### Removed {name}");
            var show = System.Math.Min(d.Removed.Count, maxRows);
            for (int i = 0; i < show; i++) sb.AppendLine($"- `{d.Removed[i].Id}` {d.Removed[i].Name}");
            if (d.Removed.Count > show) sb.AppendLine($"...and {d.Removed.Count - show} more");
        }
    }
}
```

- [ ] **Step 5.4: Run tests — verify pass**

```
dotnet test tests/RevitCli.Tests/ --filter "FullyQualifiedName~DiffRendererTests"
```

Expected: 6 passed.

- [ ] **Step 5.5: Commit**

```bash
git add src/RevitCli/Output/DiffRenderer.cs tests/RevitCli.Tests/Output/DiffRendererTests.cs
git commit -m "feat(cli): add DiffRenderer (table/json/markdown) (P1)"
```

---

## Task 6: DiffCommand (CLI)

**Files:**
- Create: `src/RevitCli/Commands/DiffCommand.cs`
- Modify: `src/RevitCli/Commands/CliCommandCatalog.cs`
- Create: `tests/RevitCli.Tests/Commands/DiffCommandTests.cs`

- [ ] **Step 6.1: Write failing command tests**

Create `tests/RevitCli.Tests/Commands/DiffCommandTests.cs`:

```csharp
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using RevitCli.Commands;
using RevitCli.Shared;
using Xunit;

namespace RevitCli.Tests.Commands;

public class DiffCommandTests
{
    private static string WriteSnapshot(string path, ModelSnapshot s)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(s));
        return path;
    }

    private static ModelSnapshot Fixture(params (long id, string mark, string hash)[] walls)
    {
        var s = new ModelSnapshot { SchemaVersion = 1,
            Revit = new SnapshotRevit { DocumentPath = "/a.rvt" } };
        if (walls.Length > 0)
        {
            var list = new List<SnapshotElement>();
            foreach (var (id, mark, hash) in walls)
                list.Add(new SnapshotElement { Id = id, Name = $"E{id}",
                    Parameters = new() { ["Mark"] = mark }, Hash = hash });
            s.Categories["walls"] = list;
        }
        return s;
    }

    [Fact]
    public async Task Diff_TwoSnapshots_PrintsSummary()
    {
        var dir = Path.Combine(Path.GetTempPath(), "revitcli-test-diff-" + System.Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var a = WriteSnapshot(Path.Combine(dir, "a.json"), Fixture((1, "A", "h1")));
        var b = WriteSnapshot(Path.Combine(dir, "b.json"), Fixture((1, "A", "h1"), (2, "B", "h2")));
        var writer = new StringWriter();

        var exitCode = await DiffCommand.ExecuteAsync(a, b, "table", null, null, 20, writer);

        Assert.Equal(0, exitCode);
        Assert.Contains("walls", writer.ToString());
        Assert.Contains("+1", writer.ToString());

        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public async Task Diff_JsonOutput_IsValidJson()
    {
        var dir = Path.Combine(Path.GetTempPath(), "revitcli-test-diff-" + System.Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var a = WriteSnapshot(Path.Combine(dir, "a.json"), Fixture((1, "A", "h1")));
        var b = WriteSnapshot(Path.Combine(dir, "b.json"), Fixture((1, "A", "h1")));
        var writer = new StringWriter();

        var exitCode = await DiffCommand.ExecuteAsync(a, b, "json", null, null, 20, writer);

        Assert.Equal(0, exitCode);
        var parsed = JsonSerializer.Deserialize<SnapshotDiff>(
            writer.ToString(),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.Equal(1, parsed.SchemaVersion);

        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public async Task Diff_FileNotExist_ReturnsOne()
    {
        var writer = new StringWriter();
        var exitCode = await DiffCommand.ExecuteAsync(
            "/does/not/exist/a.json", "/does/not/exist/b.json", "table", null, null, 20, writer);
        Assert.Equal(1, exitCode);
        Assert.Contains("not found", writer.ToString().ToLower());
    }

    [Fact]
    public async Task Diff_SchemaMismatch_ReturnsOne()
    {
        var dir = Path.Combine(Path.GetTempPath(), "revitcli-test-diff-" + System.Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var a = new ModelSnapshot { SchemaVersion = 1 };
        var b = new ModelSnapshot { SchemaVersion = 2 };
        var aPath = WriteSnapshot(Path.Combine(dir, "a.json"), a);
        var bPath = WriteSnapshot(Path.Combine(dir, "b.json"), b);
        var writer = new StringWriter();

        var exitCode = await DiffCommand.ExecuteAsync(aPath, bPath, "table", null, null, 20, writer);

        Assert.Equal(1, exitCode);
        Assert.Contains("schema mismatch", writer.ToString().ToLower());

        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public async Task Diff_WithReportPath_WritesFileAndReturnsZero()
    {
        var dir = Path.Combine(Path.GetTempPath(), "revitcli-test-diff-" + System.Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var a = WriteSnapshot(Path.Combine(dir, "a.json"), Fixture((1, "A", "h1")));
        var b = WriteSnapshot(Path.Combine(dir, "b.json"), Fixture((1, "B", "h2")));
        var reportPath = Path.Combine(dir, "out.md");
        var writer = new StringWriter();

        var exitCode = await DiffCommand.ExecuteAsync(a, b, "table", reportPath, null, 20, writer);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(reportPath));
        var content = File.ReadAllText(reportPath);
        Assert.StartsWith("## Model changes", content); // format inferred from .md extension

        Directory.Delete(dir, recursive: true);
    }
}
```

- [ ] **Step 6.2: Run tests — verify fail to compile**

```
dotnet test tests/RevitCli.Tests/ --filter "FullyQualifiedName~DiffCommandTests"
```

- [ ] **Step 6.3: Create `src/RevitCli/Commands/DiffCommand.cs`**

```csharp
using System;
using System.CommandLine;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using RevitCli.Output;
using RevitCli.Shared;

namespace RevitCli.Commands;

public static class DiffCommand
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public static Command Create()
    {
        var fromArg = new Argument<string>("from", "Baseline snapshot JSON file");
        var toArg = new Argument<string>("to", "Current snapshot JSON file");
        var outputOpt = new Option<string>("--output", () => "table", "Output format: table, json, markdown");
        var reportOpt = new Option<string?>("--report", "Write to file (format inferred from .md/.json extension)");
        var categoriesOpt = new Option<string?>("--categories", "Comma-separated category filter");
        var maxRowsOpt = new Option<int>("--max-rows", () => 20, "Rows shown per section in table/markdown");

        var command = new Command("diff", "Diff two snapshot JSON files")
        {
            fromArg, toArg, outputOpt, reportOpt, categoriesOpt, maxRowsOpt
        };

        command.SetHandler(async (string from, string to, string output,
                                  string? report, string? categories, int maxRows) =>
        {
            Environment.ExitCode = await ExecuteAsync(from, to, output, report, categories, maxRows, Console.Out);
        }, fromArg, toArg, outputOpt, reportOpt, categoriesOpt, maxRowsOpt);

        return command;
    }

    public static async Task<int> ExecuteAsync(
        string fromPath, string toPath,
        string outputFormat, string? reportPath, string? categoriesFilter, int maxRows,
        TextWriter output)
    {
        if (!File.Exists(fromPath))
        {
            await output.WriteLineAsync($"Error: snapshot not found: {fromPath}");
            return 1;
        }
        if (!File.Exists(toPath))
        {
            await output.WriteLineAsync($"Error: snapshot not found: {toPath}");
            return 1;
        }

        ModelSnapshot fromSnap, toSnap;
        try
        {
            fromSnap = JsonSerializer.Deserialize<ModelSnapshot>(File.ReadAllText(fromPath), JsonOpts)!;
            toSnap = JsonSerializer.Deserialize<ModelSnapshot>(File.ReadAllText(toPath), JsonOpts)!;
        }
        catch (JsonException ex)
        {
            await output.WriteLineAsync($"Error: invalid snapshot JSON: {ex.Message}");
            return 1;
        }

        SnapshotDiff diff;
        try
        {
            diff = SnapshotDiffer.Diff(fromSnap, toSnap, Path.GetFileName(fromPath), Path.GetFileName(toPath));
        }
        catch (InvalidOperationException ex)
        {
            await output.WriteLineAsync($"Error: {ex.Message}");
            return 1;
        }

        if (!string.IsNullOrWhiteSpace(categoriesFilter))
        {
            var allow = new System.Collections.Generic.HashSet<string>(
                categoriesFilter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                StringComparer.OrdinalIgnoreCase);
            foreach (var key in new System.Collections.Generic.List<string>(diff.Categories.Keys))
                if (!allow.Contains(key)) diff.Categories.Remove(key);
        }

        var effectiveFormat = outputFormat;
        if (reportPath != null)
        {
            var ext = Path.GetExtension(reportPath).ToLowerInvariant();
            if (ext == ".md") effectiveFormat = "markdown";
            else if (ext == ".json") effectiveFormat = "json";
        }

        var rendered = DiffRenderer.Render(diff, effectiveFormat, maxRows);

        if (reportPath != null)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(reportPath));
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(reportPath, rendered);
            await output.WriteLineAsync($"Diff saved to {reportPath}");
        }
        else
        {
            await output.WriteLineAsync(rendered);
        }

        return 0;
    }
}
```

- [ ] **Step 6.4: Register in `src/RevitCli/Commands/CliCommandCatalog.cs`**

Add to `TopLevelCommands` array (before `("interactive", ...)`):

```csharp
("diff", "Diff two snapshot JSON files"),
```

Add to `CreateRootCommand` method, before `if (includeBatchCommand)`:

```csharp
root.AddCommand(DiffCommand.Create());
```

- [ ] **Step 6.5: Run tests — verify all pass**

```
dotnet test tests/RevitCli.Tests/
```

Expected: existing + 5 new DiffCommand tests pass.

- [ ] **Step 6.6: Commit**

```bash
git add src/RevitCli/Commands/DiffCommand.cs \
        src/RevitCli/Commands/CliCommandCatalog.cs \
        tests/RevitCli.Tests/Commands/DiffCommandTests.cs
git commit -m "feat(cli): add diff command (reads two snapshot JSONs, renders output) (P1)"
```

---

## Task 7: RevitClient.CaptureSnapshotAsync (HTTP method)

**Files:**
- Modify: `src/RevitCli/Client/RevitClient.cs`
- Modify: `tests/RevitCli.Tests/Client/RevitClientTests.cs`

- [ ] **Step 7.1: Write failing HTTP client test**

Add to `tests/RevitCli.Tests/Client/RevitClientTests.cs` (within the existing class, before the closing brace of `RevitClientTests`):

```csharp
    [Fact]
    public async Task CaptureSnapshotAsync_PostsRequestAndParsesResponse()
    {
        var snapshot = new ModelSnapshot { SchemaVersion = 1, TakenAt = "2026-04-23T00:00:00Z" };
        var response = ApiResponse<ModelSnapshot>.Ok(snapshot);
        var handler = new FakeHttpHandler(JsonSerializer.Serialize(response));
        var client = new RevitClient(new HttpClient(handler) { BaseAddress = new System.Uri("http://localhost:17839") });

        var result = await client.CaptureSnapshotAsync(new SnapshotRequest());

        Assert.True(result.Success);
        Assert.Equal("2026-04-23T00:00:00Z", result.Data!.TakenAt);
        Assert.Equal("http://localhost:17839/api/snapshot", handler.LastRequestUri);
        Assert.Contains("IncludeSheets", handler.LastRequestBody ?? "", System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CaptureSnapshotAsync_ConnectionFailed_ReturnsFail()
    {
        var handler = new FakeHttpHandler(throwException: true);
        var client = new RevitClient(new HttpClient(handler) { BaseAddress = new System.Uri("http://localhost:17839") });

        var result = await client.CaptureSnapshotAsync(new SnapshotRequest());

        Assert.False(result.Success);
        Assert.Contains("not running", result.Error, System.StringComparison.OrdinalIgnoreCase);
    }
```

- [ ] **Step 7.2: Run test — verify fail to compile**

```
dotnet test tests/RevitCli.Tests/ --filter "FullyQualifiedName~CaptureSnapshotAsync"
```

- [ ] **Step 7.3: Add method to `src/RevitCli/Client/RevitClient.cs`**

Add at end of class, before closing brace (match pattern of `ExportAsync`):

```csharp
    public async Task<ApiResponse<ModelSnapshot>> CaptureSnapshotAsync(SnapshotRequest request)
    {
        try
        {
            var url = "/api/snapshot";
            var content = new StringContent(
                JsonSerializer.Serialize(request),
                Encoding.UTF8,
                "application/json");
            var response = await _http.PostAsync(url, content);
            var json = await SendAndRead(response, "POST", url);
            return JsonSerializer.Deserialize<ApiResponse<ModelSnapshot>>(json, JsonOptions)!;
        }
        catch (HttpRequestException ex)
        {
            if (Verbose) Console.Error.WriteLine($"[HTTP] Connection failed: {ex.Message}");
            return ApiResponse<ModelSnapshot>.Fail("Revit is not running or plugin is not loaded.");
        }
        catch (Exception ex) when (ex is TaskCanceledException or JsonException)
        {
            return ApiResponse<ModelSnapshot>.Fail($"Communication error: {ex.Message}");
        }
    }
```

- [ ] **Step 7.4: Run tests — verify pass**

```
dotnet test tests/RevitCli.Tests/ --filter "FullyQualifiedName~CaptureSnapshotAsync"
```

Expected: 2 passed.

- [ ] **Step 7.5: Commit**

```bash
git add src/RevitCli/Client/RevitClient.cs \
        tests/RevitCli.Tests/Client/RevitClientTests.cs
git commit -m "feat(cli): add RevitClient.CaptureSnapshotAsync (HTTP POST /api/snapshot) (P1)"
```

---

## Task 8: SnapshotCommand (CLI)

**Files:**
- Create: `src/RevitCli/Commands/SnapshotCommand.cs`
- Modify: `src/RevitCli/Commands/CliCommandCatalog.cs`
- Create: `tests/RevitCli.Tests/Commands/SnapshotCommandTests.cs`

- [ ] **Step 8.1: Write failing command tests**

Create `tests/RevitCli.Tests/Commands/SnapshotCommandTests.cs`:

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

        Assert.Contains("\"IncludeSheets\":false", handler.LastRequestBody);
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
```

- [ ] **Step 8.2: Run tests — verify fail**

```
dotnet test tests/RevitCli.Tests/ --filter "FullyQualifiedName~SnapshotCommandTests"
```

- [ ] **Step 8.3: Create `src/RevitCli/Commands/SnapshotCommand.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using RevitCli.Client;
using RevitCli.Shared;

namespace RevitCli.Commands;

public static class SnapshotCommand
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static Command Create(RevitClient client)
    {
        var outputOpt = new Option<string?>("--output", "Write JSON to file (default: stdout)");
        var categoriesOpt = new Option<string?>("--categories", "Comma-separated category list (default: built-in set)");
        var noSheetsOpt = new Option<bool>("--no-sheets", "Skip sheets section");
        var noSchedulesOpt = new Option<bool>("--no-schedules", "Skip schedules section");
        var summaryOnlyOpt = new Option<bool>("--summary-only", "Only output Summary section (fast)");

        var command = new Command("snapshot", "Capture model's semantic state as JSON")
        {
            outputOpt, categoriesOpt, noSheetsOpt, noSchedulesOpt, summaryOnlyOpt
        };

        command.SetHandler(async (string? output, string? categories,
                                  bool noSheets, bool noSchedules, bool summaryOnly) =>
        {
            Environment.ExitCode = await ExecuteAsync(
                client, output, categories, !noSheets, !noSchedules, summaryOnly, Console.Out);
        }, outputOpt, categoriesOpt, noSheetsOpt, noSchedulesOpt, summaryOnlyOpt);

        return command;
    }

    public static async Task<int> ExecuteAsync(
        RevitClient client,
        string? outputPath,
        string? categories,
        bool includeSheets,
        bool includeSchedules,
        bool summaryOnly,
        TextWriter output)
    {
        var request = new SnapshotRequest
        {
            IncludeSheets = includeSheets,
            IncludeSchedules = includeSchedules,
            SummaryOnly = summaryOnly
        };
        if (!string.IsNullOrWhiteSpace(categories))
        {
            request.IncludeCategories = new List<string>(
                categories.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        var result = await client.CaptureSnapshotAsync(request);
        if (!result.Success)
        {
            await output.WriteLineAsync($"Error: {result.Error}");
            return 1;
        }

        var json = JsonSerializer.Serialize(result.Data, JsonOpts);
        if (outputPath != null)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(outputPath, json);
            await output.WriteLineAsync($"Snapshot written to {outputPath}");
        }
        else
        {
            await output.WriteLineAsync(json);
        }
        return 0;
    }
}
```

- [ ] **Step 8.4: Register in `src/RevitCli/Commands/CliCommandCatalog.cs`**

Add to `TopLevelCommands` array (place next to `diff`):

```csharp
("snapshot", "Capture model's semantic state as JSON"),
```

Add to `CreateRootCommand`:

```csharp
root.AddCommand(SnapshotCommand.Create(client));
```

- [ ] **Step 8.5: Run tests — verify pass**

```
dotnet test tests/RevitCli.Tests/ --filter "FullyQualifiedName~SnapshotCommandTests"
```

Expected: 5 passed.

- [ ] **Step 8.6: Commit**

```bash
git add src/RevitCli/Commands/SnapshotCommand.cs \
        src/RevitCli/Commands/CliCommandCatalog.cs \
        tests/RevitCli.Tests/Commands/SnapshotCommandTests.cs
git commit -m "feat(cli): add snapshot command (POST /api/snapshot, write JSON) (P1)"
```

---

## Task 9: SnapshotController (addin HTTP endpoint)

**Files:**
- Create: `src/RevitCli.Addin/Handlers/SnapshotController.cs`
- Modify: `src/RevitCli.Addin/Server/ApiServer.cs`

- [ ] **Step 9.1: Create `src/RevitCli.Addin/Handlers/SnapshotController.cs`**

Follow the `SetController` pattern exactly (POST with JSON body → operations call → ApiResponse serialize with 400/409/500 error mapping):

```csharp
using System;
using System.Text.Json;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using RevitCli.Shared;

namespace RevitCli.Addin.Handlers;

public class SnapshotController : WebApiController
{
    private readonly IRevitOperations _operations;

    public SnapshotController(IRevitOperations operations)
    {
        _operations = operations;
    }

    [Route(HttpVerbs.Post, "/snapshot")]
    public async Task CaptureSnapshot()
    {
        HttpContext.Response.ContentType = "application/json";
        await using var writer = HttpContext.OpenResponseText();

        SnapshotRequest? request;
        try
        {
            var body = await HttpContext.GetRequestBodyAsStringAsync();
            request = string.IsNullOrWhiteSpace(body)
                ? new SnapshotRequest()
                : JsonSerializer.Deserialize<SnapshotRequest>(body,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            HttpContext.Response.StatusCode = 400;
            await writer.WriteAsync(JsonSerializer.Serialize(
                ApiResponse<ModelSnapshot>.Fail($"Invalid JSON: {ex.Message}")));
            return;
        }

        request ??= new SnapshotRequest();

        try
        {
            var data = await _operations.CaptureSnapshotAsync(request);
            await writer.WriteAsync(JsonSerializer.Serialize(
                ApiResponse<ModelSnapshot>.Ok(data)));
        }
        catch (ArgumentException ex)
        {
            HttpContext.Response.StatusCode = 400;
            await writer.WriteAsync(JsonSerializer.Serialize(
                ApiResponse<ModelSnapshot>.Fail(ex.Message)));
        }
        catch (InvalidOperationException ex)
        {
            HttpContext.Response.StatusCode = 409;
            await writer.WriteAsync(JsonSerializer.Serialize(
                ApiResponse<ModelSnapshot>.Fail(ex.Message)));
        }
        catch (Exception ex)
        {
            HttpContext.Response.StatusCode = 500;
            await writer.WriteAsync(JsonSerializer.Serialize(
                ApiResponse<ModelSnapshot>.Fail(ex.Message)));
        }
    }
}
```

- [ ] **Step 9.2: Register controller in `src/RevitCli.Addin/Server/ApiServer.cs`**

Inside `CreateServer` method's `.WithWebApi("/api", m => m ...)` block, add controller:

```csharp
                .WithController(() => new ScheduleController(_operations))
                .WithController(() => new SnapshotController(_operations)))
```

(Add after `ScheduleController`. Don't forget the existing `)` closes `WithWebApi`, so just add the line before it.)

- [ ] **Step 9.3: Verify shared + CLI still build**

```
dotnet build src/RevitCli/RevitCli.csproj
```

Expected: success. (Addin can't build on Linux — that's normal; Windows CI will verify it.)

- [ ] **Step 9.4: Commit**

```bash
git add src/RevitCli.Addin/Handlers/SnapshotController.cs \
        src/RevitCli.Addin/Server/ApiServer.cs
git commit -m "feat(addin): add SnapshotController (POST /api/snapshot) (P1)"
```

---

## Task 10: ProtocolTests — snapshot endpoint roundtrip

**Files:**
- Modify: `tests/RevitCli.Addin.Tests/Integration/ProtocolTests.cs`

Note: These tests can only be compiled/run on Windows with Revit installed. Include the test code so CI will validate on Windows runner.

- [ ] **Step 10.1: Add snapshot roundtrip test**

Add inside the `ProtocolTests` class (before closing brace):

```csharp
    [Fact]
    public async Task CaptureSnapshot_ReturnsPlaceholderSnapshot()
    {
        var result = await _client.CaptureSnapshotAsync(new SnapshotRequest());

        Assert.True(result.Success);
        Assert.NotNull(result.Data);
        Assert.Equal(1, result.Data.SchemaVersion);
        Assert.Equal("2025", result.Data.Revit.Version);
        Assert.True(result.Data.Categories.ContainsKey("walls"));
        Assert.Single(result.Data.Categories["walls"]);
        Assert.Equal(1001, result.Data.Categories["walls"][0].Id);
        Assert.Single(result.Data.Sheets);
        Assert.Single(result.Data.Schedules);
        Assert.Equal(1, result.Data.Summary.ElementCounts["walls"]);
    }

    [Fact]
    public async Task CaptureSnapshot_WithIncludeCategoriesFilter_RequestReaches()
    {
        var result = await _client.CaptureSnapshotAsync(new SnapshotRequest
        {
            IncludeCategories = new List<string> { "walls" },
            IncludeSheets = false
        });

        // Placeholder currently ignores filter; assert response is still well-formed.
        Assert.True(result.Success);
        Assert.NotNull(result.Data);
    }
```

Also add to the `using` block at top of file if missing:
```csharp
using System.Collections.Generic;
```

- [ ] **Step 10.2: Verify shared tests still pass on Linux**

```
dotnet test tests/RevitCli.Tests/
```

Expected: all green — addin tests are compiled/run separately on Windows.

- [ ] **Step 10.3: Commit**

```bash
git add tests/RevitCli.Addin.Tests/Integration/ProtocolTests.cs
git commit -m "test(addin): add snapshot endpoint protocol roundtrip tests (P1)"
```

---

## Task 11: RealRevitOperations.CaptureSnapshotAsync (Revit API implementation)

**Files:**
- Modify: `src/RevitCli.Addin/Services/RealRevitOperations.cs`

Note: This code only compiles on Windows with Revit installed. It will be verified in end-to-end validation (Task 13) on the user's Revit 2026 host.

- [ ] **Step 11.1: Add DefaultSnapshotCategories constant at top of class**

Find `private static readonly Dictionary<string, BuiltInCategory> CategoryAliases = BuildCategoryAliases();` and add right below:

```csharp
    private static readonly string[] DefaultSnapshotCategories = new[]
    {
        "walls", "doors", "windows", "rooms",
        "floors", "roofs", "stairs", "columns",
        "structuralcolumns", "ceilings", "furniture", "levels"
    };
```

- [ ] **Step 11.2: Add `CaptureSnapshotAsync` implementation**

Add at end of class, before the closing brace (after `CreateScheduleAsync`):

```csharp
    public Task<ModelSnapshot> CaptureSnapshotAsync(SnapshotRequest request)
    {
        return _bridge.InvokeAsync(app =>
        {
            var doc = RequireActiveDocument(app);

            var snapshot = new ModelSnapshot
            {
                SchemaVersion = 1,
                TakenAt = DateTime.UtcNow.ToString("o"),
                Revit = new SnapshotRevit
                {
                    Version = app.Application.VersionNumber ?? "",
                    Document = string.IsNullOrEmpty(doc.Title)
                        ? "" : System.IO.Path.GetFileNameWithoutExtension(doc.Title),
                    DocumentPath = doc.PathName ?? ""
                },
                Model = new SnapshotModel { SizeBytes = 0, FileHash = "" }
            };

            // Elements
            var requested = request.IncludeCategories ?? new List<string>(DefaultSnapshotCategories);
            foreach (var catName in requested)
            {
                BuiltInCategory bic;
                try { bic = ResolveCategory(doc, catName); }
                catch (ArgumentException) { continue; }

                var items = new List<SnapshotElement>();
                foreach (var element in new FilteredElementCollector(doc)
                    .OfCategory(bic).WhereElementIsNotElementType())
                {
                    if (request.SummaryOnly)
                    {
                        items.Add(new SnapshotElement { Id = element.Id.Value });
                    }
                    else
                    {
                        var info = MapElement(doc, element);
                        var snap = new SnapshotElement
                        {
                            Id = element.Id.Value,
                            Name = info.Name ?? "",
                            TypeName = info.TypeName ?? "",
                            Parameters = new Dictionary<string, string>(info.Parameters)
                        };
                        snap.Hash = SnapshotHasher.HashElement(snap);
                        items.Add(snap);
                    }
                }
                snapshot.Categories[catName] = items;
            }

            // Sheets
            if (request.IncludeSheets && !request.SummaryOnly)
            {
                foreach (var sheet in new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSheet)).Cast<ViewSheet>())
                {
                    var placedIds = new List<long>();
                    try
                    {
                        foreach (var viewId in sheet.GetAllPlacedViews())
                            placedIds.Add(viewId.Value);
                    }
                    catch { /* empty sheet or API edge case — leave placedIds empty */ }

                    var sheetSnap = new SnapshotSheet
                    {
                        Number = sheet.SheetNumber ?? "",
                        Name = sheet.Name ?? "",
                        ViewId = sheet.Id.Value,
                        PlacedViewIds = placedIds,
                        Parameters = ReadVisibleParameters(doc, sheet),
                        ContentHash = ""  // P2 will compute
                    };
                    sheetSnap.MetaHash = SnapshotHasher.HashSheetMeta(sheetSnap);
                    snapshot.Sheets.Add(sheetSnap);
                }
            }

            // Schedules
            if (request.IncludeSchedules && !request.SummaryOnly)
            {
                foreach (var vs in new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSchedule)).Cast<ViewSchedule>())
                {
                    if (vs.IsTitleblockRevisionSchedule) continue;

                    var columns = new List<string>();
                    var fieldCount = vs.Definition.GetFieldCount();
                    for (var i = 0; i < fieldCount; i++)
                    {
                        var f = vs.Definition.GetField(i);
                        if (!f.IsHidden) columns.Add(f.GetName());
                    }

                    var rows = new List<Dictionary<string, string>>();
                    var bodySection = vs.GetTableData().GetSectionData(SectionType.Body);
                    for (var r = 1; r < bodySection.NumberOfRows; r++)
                    {
                        var row = new Dictionary<string, string>();
                        for (var c = 0; c < columns.Count; c++)
                            row[columns[c]] = vs.GetCellText(SectionType.Body, r, c);
                        rows.Add(row);
                    }

                    var cat = "";
                    try { cat = vs.Definition.CategoryId is { } catId
                        ? Category.GetCategory(doc, catId)?.Name ?? "" : ""; }
                    catch { cat = ""; }

                    snapshot.Schedules.Add(new SnapshotSchedule
                    {
                        Id = vs.Id.Value,
                        Name = vs.Name ?? "",
                        Category = cat,
                        RowCount = rows.Count,
                        Hash = SnapshotHasher.HashSchedule(cat, vs.Name ?? "", columns, rows)
                    });
                }
            }

            // Summary
            snapshot.Summary = new SnapshotSummary
            {
                SheetCount = snapshot.Sheets.Count,
                ScheduleCount = snapshot.Schedules.Count
            };
            foreach (var kv in snapshot.Categories)
                snapshot.Summary.ElementCounts[kv.Key] = kv.Value.Count;

            // If SummaryOnly, clear bulky lists so the snapshot is light
            if (request.SummaryOnly)
            {
                // Keep counts; drop element lists, sheets, schedules
                foreach (var key in new List<string>(snapshot.Categories.Keys))
                    snapshot.Categories[key] = new List<SnapshotElement>();
                snapshot.Sheets = new List<SnapshotSheet>();
                snapshot.Schedules = new List<SnapshotSchedule>();
            }

            return snapshot;
        });
    }
```

- [ ] **Step 11.3: Add required using directives at top of file (if missing)**

Check that `RealRevitOperations.cs` top has:
```csharp
using System;
using System.Collections.Generic;
using System.Linq;
```

These likely already present. Also the file already uses `Autodesk.Revit.DB.*` types, so no new `using` needed for ViewSheet/ViewSchedule/BuiltInCategory.

- [ ] **Step 11.4: Commit (code only — Windows validation in Task 13)**

```bash
git add src/RevitCli.Addin/Services/RealRevitOperations.cs
git commit -m "feat(addin): implement CaptureSnapshotAsync on Revit main thread (P1)"
```

---

## Task 12: Completions + CliCommandCatalog tests

**Files:**
- Modify: `src/RevitCli/Commands/CompletionsCommand.cs`
- Modify: `tests/RevitCli.Tests/Commands/CompletionsCommandTests.cs`
- Modify: `tests/RevitCli.Tests/Commands/CliCommandCatalogTests.cs`

- [ ] **Step 12.1: Inspect CompletionsCommand for current shape**

```
grep -n "snapshot\|diff\|ScheduleCommand\|\"status\"" src/RevitCli/Commands/CompletionsCommand.cs
```

The generator emits static shell-completion scripts listing top-level commands. It should enumerate from `CliCommandCatalog.TopLevelCommands`. If it uses a static switch-case per command, manually add `snapshot` and `diff` entries there. If it uses the catalog, the update in Task 6 + Task 8 is already sufficient.

- [ ] **Step 12.2: Write failing completions test**

Edit `tests/RevitCli.Tests/Commands/CompletionsCommandTests.cs` to add:

```csharp
    [Fact]
    public async Task BashCompletions_Include_Snapshot_And_Diff()
    {
        var writer = new StringWriter();
        var exitCode = await CompletionsCommand.ExecuteAsync("bash", writer);
        Assert.Equal(0, exitCode);
        var output = writer.ToString();
        Assert.Contains("snapshot", output);
        Assert.Contains("diff", output);
    }

    [Fact]
    public async Task ZshCompletions_Include_Snapshot_And_Diff()
    {
        var writer = new StringWriter();
        var exitCode = await CompletionsCommand.ExecuteAsync("zsh", writer);
        Assert.Equal(0, exitCode);
        Assert.Contains("snapshot", writer.ToString());
        Assert.Contains("diff", writer.ToString());
    }
```

- [ ] **Step 12.3: Run test — verify fail or pass based on current code**

```
dotnet test tests/RevitCli.Tests/ --filter "FullyQualifiedName~CompletionsCommandTests"
```

If fails, open `src/RevitCli/Commands/CompletionsCommand.cs` and add `"snapshot"` and `"diff"` to whichever array/switch lists top-level commands. Typical shape (adapt to your file):

```csharp
// example — actual location varies
var commands = new[] { "status", "query", "export", "set", "config", "audit",
    "completions", "batch", "doctor", "check", "publish", "init", "score",
    "coverage", "schedule", "snapshot", "diff", "interactive" };
```

- [ ] **Step 12.4: Update CliCommandCatalogTests**

Edit `tests/RevitCli.Tests/Commands/CliCommandCatalogTests.cs` to include assertion that `snapshot` and `diff` are in the top-level commands:

```csharp
    [Fact]
    public void TopLevelCommands_Contains_SnapshotAndDiff()
    {
        var names = CliCommandCatalog.TopLevelCommandNames;
        Assert.Contains("snapshot", names);
        Assert.Contains("diff", names);
    }
```

- [ ] **Step 12.5: Run full test suite**

```
dotnet test tests/RevitCli.Tests/
```

Expected: all green.

- [ ] **Step 12.6: Commit**

```bash
git add src/RevitCli/Commands/CompletionsCommand.cs \
        tests/RevitCli.Tests/Commands/CompletionsCommandTests.cs \
        tests/RevitCli.Tests/Commands/CliCommandCatalogTests.cs
git commit -m "feat(cli): update shell completions + catalog tests for snapshot/diff (P1)"
```

---

## Task 13: End-to-end validation on Revit 2026 + CHANGELOG + tag

**Files:**
- Modify: `CHANGELOG.md`

This task happens on the Windows host with Revit 2026. It validates Task 11 (RealRevitOperations) since that code has no unit tests.

- [ ] **Step 13.1: Build + publish CLI and Addin on Windows**

From a Windows PowerShell at the synced source copy:

```powershell
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
cd <source-root>
dotnet publish src\RevitCli\RevitCli.csproj -c Release -o .\publish-cli --verbosity minimal
$env:RevitInstallDir = 'D:\revit2026\Revit 2026'   # adjust to host
dotnet publish src\RevitCli.Addin\RevitCli.Addin.csproj -c Release -f net8.0-windows -p:RevitYear=2026 --verbosity minimal
```

Expected: both succeed. Addin output in `src\RevitCli.Addin\bin\Release\2026\publish\`.

- [ ] **Step 13.2: Deploy addin**

Close Revit. Clear and copy:

```powershell
$dest = "$env:APPDATA\Autodesk\Revit\Addins\2026"
Get-ChildItem $dest -File | Remove-Item -Force
Copy-Item 'src\RevitCli.Addin\bin\Release\2026\publish\*' $dest -Force
# Regenerate .addin with absolute path
$dllPath = Join-Path $dest 'RevitCli.Addin.dll'
$addinXml = @"
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns>
  <AddIn Type="Application">
    <Name>RevitCli</Name>
    <Assembly>$dllPath</Assembly>
    <FullClassName>RevitCli.Addin.RevitCliApp</FullClassName>
    <AddInId>A1B2C3D4-E5F6-7890-ABCD-EF1234567890</AddInId>
    <VendorId>RevitCli</VendorId>
    <VendorDescription>https://github.com/xiaodream551-a11y/revitcli</VendorDescription>
  </AddIn>
</RevitAddIns>
"@
[System.IO.File]::WriteAllText((Join-Path $dest 'RevitCli.addin'),
    $addinXml, (New-Object System.Text.UTF8Encoding $false))
```

- [ ] **Step 13.3: Start Revit 2026, open test .rvt, and run smoke tests**

```
revitcli status         # expect: Revit version: 2026 + Document line
revitcli snapshot --summary-only
# expect: JSON with schemaVersion=1 and Summary.ElementCounts populated

revitcli snapshot --output snap-a.json
# expect: "Snapshot written to snap-a.json"

# In Revit, change one wall's Mark parameter
revitcli snapshot --output snap-b.json
revitcli diff snap-a.json snap-b.json
# expect: walls: ~1 (or more) with the Mark change shown

revitcli diff snap-a.json snap-b.json --output markdown --report diff.md
# expect: diff.md exists, starts with "## Model changes"
```

- [ ] **Step 13.4: Verify snapshot contains sheets + schedules**

```
cat snap-a.json | python -c "import json,sys; d=json.load(sys.stdin); print('sheets=', len(d['sheets']), 'schedules=', len(d['schedules']), 'walls=', len(d['categories'].get('walls',[])))"
```

Expected: `sheets=16 schedules=16 walls=...` for the test model (numbers match what `schedule list` + `query walls` show).

- [ ] **Step 13.5: Idempotency check**

Run snapshot twice without changing the model:

```
revitcli snapshot --output s1.json
revitcli snapshot --output s2.json
# Strip TakenAt (always differs) and compare the rest
python -c "
import json
a = json.load(open('s1.json')); a['takenAt'] = ''
b = json.load(open('s2.json')); b['takenAt'] = ''
print('equal=', json.dumps(a, sort_keys=True) == json.dumps(b, sort_keys=True))
"
```

Expected: `equal= True`. If False, element hashes or schedule row order are non-deterministic — investigate before shipping.

- [ ] **Step 13.6: Update `CHANGELOG.md`**

Add at top:

```markdown
## [1.1.0] - 2026-XX-XX

### Added
- `revitcli snapshot` — capture model semantic state as JSON (elements by category, sheets, schedules, summary). Supports `--categories`, `--no-sheets`, `--no-schedules`, `--summary-only`, `--output FILE`.
- `revitcli diff FROM TO` — diff two snapshot JSONs. Supports `--output table|json|markdown`, `--report FILE`, `--categories LIST`, `--max-rows N`.
- Shared DTOs: `ModelSnapshot`, `SnapshotRequest`, `SnapshotDiff`, and `SnapshotHasher` (stable SHA256-16).
- Addin endpoint: `POST /api/snapshot`.

### Phase 1 of Model-as-Code
This release is the foundation for incremental publish (v1.2) and CSV import (v1.3). See `docs/superpowers/specs/2026-04-23-model-as-code-design.md`.
```

Fix the date after all testing passes.

- [ ] **Step 13.7: Commit CHANGELOG and tag v1.1**

```bash
git add CHANGELOG.md
git commit -m "docs: update CHANGELOG for v1.1 (snapshot + diff)"
git tag -a v1.1.0 -m "v1.1.0 - snapshot + diff (Model-as-Code P1)"
```

- [ ] **Step 13.8: Push branch and tag**

```bash
git push origin main
git push origin v1.1.0
```

Expected: push succeeds via persisted credential helper (configured in earlier session).

---

## Self-Review Notes

**Spec coverage check:**
- ✅ ModelSnapshot / SnapshotRequest / SnapshotDiff DTOs — Task 1
- ✅ Stable element / sheet MetaHash / schedule hash — Task 2, Task 11
- ✅ `Task<ModelSnapshot> CaptureSnapshotAsync` interface — Task 3
- ✅ Placeholder fixture — Task 3
- ✅ SnapshotDiffer (added/removed/modified, schema mismatch, DocumentPath warning, sheet key by Number) — Task 4
- ✅ DiffRenderer (table/json/markdown) — Task 5
- ✅ `revitcli diff` CLI — Task 6
- ✅ `revitcli snapshot` CLI + `--categories/--no-sheets/--no-schedules/--summary-only/--output` — Task 8
- ✅ POST /api/snapshot controller — Task 9
- ✅ Protocol test — Task 10
- ✅ RealRevitOperations (element traversal + sheet MetaHash + schedule hash + summary) — Task 11
- ✅ Completions + catalog tests — Task 12
- ✅ End-to-end validation + CHANGELOG + tag — Task 13
- ✅ ContentHash deferred to P2 (left empty in P1) — Task 11 explicitly sets `ContentHash = ""`
- ✅ Idempotency test — Task 13 Step 13.5

**Out of scope in P1** (matches spec Non-goals):
- `publish --since` — Not in any task
- Profile `incremental` flag — Not in any task
- CSV import — Not in any task

**Type consistency check:**
- `SnapshotElement.Id` is `long` throughout (DTO + diff + renderer + tests).
- `SnapshotHasher` method names consistent: `HashElement`, `HashSheetMeta`, `HashSchedule`.
- `SnapshotDiffer.Diff(from, to, fromLabel, toLabel)` signature stable across tests and CLI.
- `CaptureSnapshotAsync(SnapshotRequest)` signature consistent between interface, Placeholder, Real, HTTP client, controller.

**Risks flagged for execution:**
- Task 11 `sheet.GetAllPlacedViews()` API — wrapped in try/catch so a malformed sheet won't abort snapshot. If the API doesn't exist at all on Revit 2026 (unlikely), compile will fail; check against Revit SDK docs before starting Task 11.
- Task 11 `vs.Definition.CategoryId` — may be null for schedules without a category (e.g., "A_材料明细表"). Handled by try/catch and default-empty string.
- Task 13 idempotency failure would indicate a real bug in `SnapshotHasher` or schedule row enumeration that must be fixed before tagging.

---

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-04-23-snapshot-and-diff.md`.

Two execution options:

1. **Subagent-Driven (recommended)** — Fresh subagent per task, two-stage review between tasks, fast iteration. Safe because each task has a narrow blast radius and independent tests.

2. **Inline Execution** — Execute tasks in this session, batch checkpoints for review. Slower but keeps full context in one transcript.

Which approach?
