# Import CSV (Model-as-Code Phase 3) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add `revitcli import FILE.csv` — declarative CSV → Revit parameter writeback with auto-encoding (UTF-8/GBK), parameter mapping, dry-run, missing/duplicate policies, and aggregated batch transactions.

**Architecture:** New CLI-only command. Reads CSV bytes (BOM → UTF-8 strict → GBK fallback), maps CSV columns to Revit parameters, fetches all elements in target category once via existing `QueryElementsAsync`, builds in-memory index by `--match-by` parameter, groups changes by `(param, value)` and submits batched `SetRequest` calls (ElementIds list — one round-trip per group, chunked by `--batch-size`). No new addin endpoint. Exit codes: 0 = success/dry-run; 1 = setup/IO error; 2 = partial row failures.

**Tech Stack:** .NET 8 console (`System.CommandLine` + Spectre.Console), existing `RevitClient` HTTP layer, GB18030 via `System.Text.Encoding.CodePages` package (already-known WSL/Linux limitation: `Encoding.GetEncoding("gbk")` requires explicit registration).

---

## Spec reference

`docs/superpowers/specs/2026-04-23-model-as-code-design.md` lines 394–425 (CLI surface), 484–491 (workflow C), 506–513 (error matrix rows for `import`), 538 (test counts).

## Open questions answered up-front

1. **Empty cell policy** — A blank CSV cell **skips** the parameter for that row (does not write empty string). Reason: Excel users routinely leave cells blank to mean "don't touch"; an explicit empty value is rare and ambiguous. If users need to clear a parameter, document the workaround `--map "col:Param"` with literal text `""` in the column.
2. **Batch grouping** — Group all `(rowIdx, elementId, revitParam, value)` tuples by `(revitParam, value)`. Each unique pair becomes one `SetRequest` with the matching ElementIds list, chunked by `--batch-size` (default 100). Rationale: minimizes addin round-trips while keeping each transaction small enough to roll back cleanly on Revit-side failure.
3. **`--batch-size` semantics** — Maximum ElementIds per `SetRequest`. Each `SetRequest` is one Revit transaction. Smaller batches = more granular failure isolation but more round-trips. Default 100 picked empirically for 5k-element models.
4. **GBK on Linux** — `System.Text.Encoding.CodePages` NuGet must be added to `src/RevitCli/RevitCli.csproj`, plus a `Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)` call in `Program.cs` startup. Without this, `Encoding.GetEncoding("gbk")` throws on .NET 8 / Linux.
5. **Header normalization** — Headers and `--match-by` argument are matched **case-sensitively, whitespace-trimmed**. Reason: Revit parameter names are case-sensitive; surprising users with case-insensitive matching will hide bugs.
6. **`--match-by` value comparison** — String-equal, **whitespace-trimmed**, **case-sensitive**. Document explicitly so 'W01' vs 'w01' doesn't silently mismatch.
7. **Multiple changes to one element across rows** — If two CSV rows share the same `--match-by` value but specify different values for the same param, the **last row wins** within a single import (warning emitted). Reason: deterministic, traceable, surfaces user error.
8. **CSV with no data rows** — Exit 0, "No rows to import" message. Same as `find . -empty` style.

---

## File Structure

```
NEW    src/RevitCli/Output/CsvParser.cs
       Pure-C# RFC 4180 CSV parser. BOM-aware encoding detection
       (UTF-8 BOM, UTF-16 LE/BE BOM, then strict UTF-8, then GBK fallback).
       Returns CsvData { Encoding, Headers, Rows }.

NEW    src/RevitCli/Output/CsvMapping.cs
       Parses --map "col:Param,col2:Param2" into Dictionary<string,string>.
       Builds final csvCol → revitParam mapping, defaulting to identity for
       columns not in --map. Excludes the --match-by column from writes.

NEW    src/RevitCli/Output/ImportPlanner.cs
       Pure-C#. Takes CsvData + element index + mapping + match-by + policies,
       returns ImportPlan { Groups, Misses, Duplicates, Skipped, Warnings }.
       Groups = List<ImportGroup{ Param, Value, ElementIds, Sources[] }>.

NEW    src/RevitCli/Commands/ImportCommand.cs
       Orchestrates: parse CSV → fetch elements → build index → plan →
       (dry-run or batch SetRequest per group) → render result + exit code.
       Both Create() (Spectre table for TTY) and ExecuteAsync(TextWriter)
       paths, mirroring SetCommand.cs / PublishCommand.cs shape.

MOD    src/RevitCli/Commands/CliCommandCatalog.cs
       Add `import` to TopLevelCommands and CreateRootCommand.

MOD    src/RevitCli/Commands/CompletionsCommand.cs
       Add bash/zsh/pwsh blocks for `import` flags.

MOD    src/RevitCli/Program.cs
       Register CodePagesEncodingProvider once at startup.

MOD    src/RevitCli/RevitCli.csproj
       <PackageReference Include="System.Text.Encoding.CodePages" Version="9.0.0" />
       <Version>1.3.0</Version>

NEW    tests/RevitCli.Tests/Output/CsvParserTests.cs               (~10 facts)
NEW    tests/RevitCli.Tests/Output/CsvMappingTests.cs              (~6 facts)
NEW    tests/RevitCli.Tests/Output/ImportPlannerTests.cs           (~10 facts)
NEW    tests/RevitCli.Tests/Commands/ImportCommandTests.cs         (~12 facts)
MOD    tests/RevitCli.Tests/Commands/CompletionsCommandTests.cs    (+3 import asserts)

MOD    CHANGELOG.md
       v1.3.0 entry.
```

---

## Why these splits

- **CsvParser** isolated so encoding/quoting bugs are testable without any Revit context.
- **CsvMapping** isolated so `--map` parsing and identity-default semantics have their own tests.
- **ImportPlanner** isolated so the matching + grouping algorithm is testable with hand-built `ElementInfo[]` and `CsvData` — no HTTP, no Revit. This is the most error-prone piece (missing / duplicate / mapping interactions); pure-C# with dense unit tests pays back fast.
- **ImportCommand** stays thin: parses flags, calls the three above, drives `RevitClient`, formats output. Tests only need a `FakeHttpHandler` for the HTTP edge.

---

## Task 1: CSV parser — encoding detection + RFC 4180 quoting

**Files:**
- Create: `src/RevitCli/Output/CsvParser.cs`
- Create: `tests/RevitCli.Tests/Output/CsvParserTests.cs`

### Step 1: Write CsvData container and CsvParser shell

- [ ] Create `src/RevitCli/Output/CsvParser.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace RevitCli.Output;

public class CsvData
{
    public Encoding Encoding { get; init; } = Encoding.UTF8;
    public string EncodingName { get; init; } = "utf-8";
    public List<string> Headers { get; init; } = new();
    public List<List<string>> Rows { get; init; } = new();
}

public static class CsvParser
{
    /// <summary>
    /// Detect encoding (BOM → strict UTF-8 → GBK), decode, parse RFC 4180.
    /// </summary>
    public static CsvData Parse(byte[] bytes, string encodingHint = "auto")
    {
        var (text, encoding, encName) = Decode(bytes, encodingHint);
        var (headers, rows) = ParseText(text);
        return new CsvData
        {
            Encoding = encoding,
            EncodingName = encName,
            Headers = headers,
            Rows = rows
        };
    }

    public static CsvData ParseFile(string path, string encodingHint = "auto")
    {
        var bytes = File.ReadAllBytes(path);
        return Parse(bytes, encodingHint);
    }

    private static (string Text, Encoding Encoding, string Name) Decode(byte[] bytes, string hint)
    {
        if (string.Equals(hint, "utf-8", StringComparison.OrdinalIgnoreCase))
            return (StripBom(new UTF8Encoding(false, true).GetString(bytes)), Encoding.UTF8, "utf-8");

        if (string.Equals(hint, "gbk", StringComparison.OrdinalIgnoreCase))
        {
            var gbk = Encoding.GetEncoding("gbk");
            return (gbk.GetString(bytes), gbk, "gbk");
        }

        // auto
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return (Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3), Encoding.UTF8, "utf-8");
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return (Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), Encoding.Unicode, "utf-16le");
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return (Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2), Encoding.BigEndianUnicode, "utf-16be");

        try
        {
            var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            return (strict.GetString(bytes), Encoding.UTF8, "utf-8");
        }
        catch (DecoderFallbackException)
        {
            try
            {
                var gbk = Encoding.GetEncoding("gbk");
                return (gbk.GetString(bytes), gbk, "gbk");
            }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException(
                    "GBK decoding unavailable. Run with --encoding utf-8 after re-saving the file as UTF-8, " +
                    "or ensure CodePagesEncodingProvider is registered.", ex);
            }
        }
    }

    private static string StripBom(string s) =>
        (s.Length > 0 && s[0] == '﻿') ? s.Substring(1) : s;

    /// <summary>
    /// Parse RFC 4180 CSV text. Supports double-quoted values, escaped quotes ("" inside ""),
    /// embedded commas and newlines inside quotes, CRLF or LF line endings.
    /// </summary>
    private static (List<string> Headers, List<List<string>> Rows) ParseText(string text)
    {
        var rows = new List<List<string>>();
        var current = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var i = 0;
        var emittedAnyField = false;

        while (i < text.Length)
        {
            var c = text[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i += 2;
                        continue;
                    }
                    inQuotes = false;
                    i++;
                    continue;
                }
                field.Append(c);
                i++;
                continue;
            }

            if (c == '"')
            {
                inQuotes = true;
                i++;
                continue;
            }
            if (c == ',')
            {
                current.Add(field.ToString());
                field.Clear();
                emittedAnyField = true;
                i++;
                continue;
            }
            if (c == '\r')
            {
                i++;
                continue;
            }
            if (c == '\n')
            {
                current.Add(field.ToString());
                field.Clear();
                if (current.Count > 0 && !(current.Count == 1 && current[0].Length == 0))
                    rows.Add(current);
                current = new List<string>();
                emittedAnyField = false;
                i++;
                continue;
            }
            field.Append(c);
            i++;
        }

        if (field.Length > 0 || emittedAnyField)
        {
            current.Add(field.ToString());
        }
        if (current.Count > 0 && !(current.Count == 1 && current[0].Length == 0))
            rows.Add(current);

        if (rows.Count == 0)
            return (new List<string>(), new List<List<string>>());

        var headers = rows[0].Select(h => h.Trim()).ToList();
        var dataRows = rows.Skip(1).ToList();
        return (headers, dataRows);
    }
}
```

- [ ] Run: `dotnet build src/RevitCli/RevitCli.csproj`
  Expected: SUCCESS (one new file, builds clean).

### Step 2: Write the failing parser tests

- [ ] Create `tests/RevitCli.Tests/Output/CsvParserTests.cs`:

```csharp
using System.Text;
using RevitCli.Output;
using Xunit;

namespace RevitCli.Tests.Output;

public class CsvParserTests
{
    [Fact]
    public void Parse_Utf8WithBom_StripsBom_AndReportsUtf8()
    {
        var bom = new byte[] { 0xEF, 0xBB, 0xBF };
        var body = Encoding.UTF8.GetBytes("Mark,Lock\nW01,YALE-500\n");
        var bytes = new byte[bom.Length + body.Length];
        bom.CopyTo(bytes, 0);
        body.CopyTo(bytes, bom.Length);

        var data = CsvParser.Parse(bytes);

        Assert.Equal("utf-8", data.EncodingName);
        Assert.Equal(new[] { "Mark", "Lock" }, data.Headers);
        Assert.Single(data.Rows);
        Assert.Equal(new[] { "W01", "YALE-500" }, data.Rows[0]);
    }

    [Fact]
    public void Parse_Utf8NoBom_AutoDetectsUtf8_WithChinese()
    {
        var bytes = Encoding.UTF8.GetBytes("Mark,锁具型号,耐火等级\nW01,YALE-500,甲级\nW02,YALE-700,乙级\n");
        var data = CsvParser.Parse(bytes);

        Assert.Equal("utf-8", data.EncodingName);
        Assert.Equal(new[] { "Mark", "锁具型号", "耐火等级" }, data.Headers);
        Assert.Equal(2, data.Rows.Count);
        Assert.Equal("甲级", data.Rows[0][2]);
    }

    [Fact]
    public void Parse_GbkChinese_FallsBackToGbk_WhenStrictUtf8Fails()
    {
        var gbk = Encoding.GetEncoding("gbk");
        var bytes = gbk.GetBytes("Mark,锁具型号\nW01,YALE-500\n");
        var data = CsvParser.Parse(bytes);

        Assert.Equal("gbk", data.EncodingName);
        Assert.Equal(new[] { "Mark", "锁具型号" }, data.Headers);
        Assert.Equal("YALE-500", data.Rows[0][1]);
    }

    [Fact]
    public void Parse_QuotedValueWithComma_PreservesComma()
    {
        var bytes = Encoding.UTF8.GetBytes("Mark,Notes\nW01,\"a, b, c\"\n");
        var data = CsvParser.Parse(bytes);
        Assert.Equal("a, b, c", data.Rows[0][1]);
    }

    [Fact]
    public void Parse_EscapedDoubleQuote_DecodesToSingleQuote()
    {
        var bytes = Encoding.UTF8.GetBytes("Mark,Notes\nW01,\"say \"\"hi\"\"\"\n");
        var data = CsvParser.Parse(bytes);
        Assert.Equal("say \"hi\"", data.Rows[0][1]);
    }

    [Fact]
    public void Parse_QuotedValueWithNewline_PreservesNewline()
    {
        var bytes = Encoding.UTF8.GetBytes("Mark,Notes\nW01,\"line1\nline2\"\n");
        var data = CsvParser.Parse(bytes);
        Assert.Equal("line1\nline2", data.Rows[0][1]);
    }

    [Fact]
    public void Parse_CrlfLineEndings_HandledLikeLf()
    {
        var bytes = Encoding.UTF8.GetBytes("Mark,Lock\r\nW01,A\r\nW02,B\r\n");
        var data = CsvParser.Parse(bytes);
        Assert.Equal(2, data.Rows.Count);
        Assert.Equal(new[] { "W01", "A" }, data.Rows[0]);
        Assert.Equal(new[] { "W02", "B" }, data.Rows[1]);
    }

    [Fact]
    public void Parse_TrailingNewlineMissing_LastRowStillEmitted()
    {
        var bytes = Encoding.UTF8.GetBytes("Mark,Lock\nW01,A");
        var data = CsvParser.Parse(bytes);
        Assert.Single(data.Rows);
        Assert.Equal("A", data.Rows[0][1]);
    }

    [Fact]
    public void Parse_EmptyCells_PreservedAsEmptyString()
    {
        var bytes = Encoding.UTF8.GetBytes("Mark,Lock,Fire\nW01,,甲级\n");
        var data = CsvParser.Parse(bytes);
        Assert.Equal(new[] { "W01", "", "甲级" }, data.Rows[0]);
    }

    [Fact]
    public void Parse_OnlyHeader_ReturnsEmptyRows()
    {
        var bytes = Encoding.UTF8.GetBytes("Mark,Lock\n");
        var data = CsvParser.Parse(bytes);
        Assert.Equal(new[] { "Mark", "Lock" }, data.Headers);
        Assert.Empty(data.Rows);
    }
}
```

### Step 3: Register CodePagesEncodingProvider so GBK works on Linux

- [ ] Read `src/RevitCli/Program.cs` and locate the entry point (Main / top-level statements).
- [ ] Add NuGet package to `src/RevitCli/RevitCli.csproj` inside the existing `<ItemGroup>` that holds package references:

```xml
<PackageReference Include="System.Text.Encoding.CodePages" Version="9.0.0" />
```

- [ ] In `Program.cs`, add **as the very first executable line** of `Main` (or top-level statements):

```csharp
System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
```

- [ ] Run: `dotnet build src/RevitCli/RevitCli.csproj`
  Expected: SUCCESS, restores `System.Text.Encoding.CodePages 9.0.0`.

### Step 4: Run the parser tests

- [ ] Run: `dotnet test tests/RevitCli.Tests/ --filter "FullyQualifiedName~CsvParserTests"`
  Expected: 10 PASS.

### Step 5: Commit

- [ ] Commit:

```bash
git add src/RevitCli/Output/CsvParser.cs \
        src/RevitCli/Program.cs \
        src/RevitCli/RevitCli.csproj \
        tests/RevitCli.Tests/Output/CsvParserTests.cs
git commit -m "feat(cli): CSV parser with BOM + UTF-8 + GBK auto-detection (P3)"
```

---

## Task 2: CSV → Revit parameter mapping

**Files:**
- Create: `src/RevitCli/Output/CsvMapping.cs`
- Create: `tests/RevitCli.Tests/Output/CsvMappingTests.cs`

### Step 1: Write the failing tests

- [ ] Create `tests/RevitCli.Tests/Output/CsvMappingTests.cs`:

```csharp
using System.Collections.Generic;
using RevitCli.Output;
using Xunit;

namespace RevitCli.Tests.Output;

public class CsvMappingTests
{
    [Fact]
    public void Build_NullMap_DefaultsToIdentity_ExcludesMatchByColumn()
    {
        var headers = new List<string> { "Mark", "锁具型号", "耐火等级" };
        var map = CsvMapping.Build(rawMap: null, headers, matchBy: "Mark");

        Assert.False(map.ContainsKey("Mark"));
        Assert.Equal("锁具型号", map["锁具型号"]);
        Assert.Equal("耐火等级", map["耐火等级"]);
        Assert.Equal(2, map.Count);
    }

    [Fact]
    public void Build_WithMap_OverridesColumnsExplicitly_KeepsUnmappedAsIdentity()
    {
        var headers = new List<string> { "Mark", "锁具型号", "耐火等级" };
        var map = CsvMapping.Build(rawMap: "锁具型号:Lock", headers, matchBy: "Mark");

        Assert.Equal("Lock", map["锁具型号"]);
        Assert.Equal("耐火等级", map["耐火等级"]);
    }

    [Fact]
    public void Build_MapWithMultiplePairs_AllApplied()
    {
        var headers = new List<string> { "Mark", "A", "B" };
        var map = CsvMapping.Build("A:ParamA,B:ParamB", headers, "Mark");
        Assert.Equal("ParamA", map["A"]);
        Assert.Equal("ParamB", map["B"]);
    }

    [Fact]
    public void Build_MapPairWithoutColon_Throws()
    {
        var headers = new List<string> { "Mark", "A" };
        var ex = Assert.Throws<System.InvalidOperationException>(
            () => CsvMapping.Build("A=ParamA", headers, "Mark"));
        Assert.Contains("--map", ex.Message);
    }

    [Fact]
    public void Build_MapReferencesUnknownColumn_Throws()
    {
        var headers = new List<string> { "Mark", "A" };
        var ex = Assert.Throws<System.InvalidOperationException>(
            () => CsvMapping.Build("Z:ParamZ", headers, "Mark"));
        Assert.Contains("Z", ex.Message);
    }

    [Fact]
    public void Build_MatchByMissingFromHeaders_Throws()
    {
        var headers = new List<string> { "A", "B" };
        var ex = Assert.Throws<System.InvalidOperationException>(
            () => CsvMapping.Build(null, headers, "Mark"));
        Assert.Contains("Mark", ex.Message);
    }
}
```

### Step 2: Run to verify they fail

- [ ] Run: `dotnet test tests/RevitCli.Tests/ --filter "FullyQualifiedName~CsvMappingTests"`
  Expected: FAIL — `CsvMapping` undefined.

### Step 3: Implement CsvMapping

- [ ] Create `src/RevitCli/Output/CsvMapping.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitCli.Output;

public static class CsvMapping
{
    /// <summary>
    /// Build CSV-column → Revit-parameter mapping.
    /// - matchBy column is excluded (it is the lookup key, not a writable target).
    /// - Columns mentioned in rawMap get the explicit Revit-parameter name.
    /// - Columns not in rawMap default to identity (csv column name == Revit param name).
    /// </summary>
    public static Dictionary<string, string> Build(
        string? rawMap,
        IReadOnlyList<string> headers,
        string matchBy)
    {
        if (!headers.Contains(matchBy))
            throw new InvalidOperationException(
                $"--match-by column '{matchBy}' not found in CSV headers: {string.Join(", ", headers)}");

        var explicitMap = ParseRawMap(rawMap, headers);

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var header in headers)
        {
            if (header == matchBy) continue;
            result[header] = explicitMap.TryGetValue(header, out var revitParam) ? revitParam : header;
        }
        return result;
    }

    private static Dictionary<string, string> ParseRawMap(string? raw, IReadOnlyList<string> headers)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(raw))
            return map;

        var headerSet = new HashSet<string>(headers, StringComparer.Ordinal);
        foreach (var pair in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = pair.Trim();
            var idx = trimmed.IndexOf(':');
            if (idx <= 0 || idx == trimmed.Length - 1)
                throw new InvalidOperationException(
                    $"--map: invalid pair '{trimmed}'. Expected 'csvColumn:revitParam'.");

            var csvCol = trimmed.Substring(0, idx).Trim();
            var revitParam = trimmed.Substring(idx + 1).Trim();

            if (!headerSet.Contains(csvCol))
                throw new InvalidOperationException(
                    $"--map: CSV column '{csvCol}' not found in headers: {string.Join(", ", headers)}");

            map[csvCol] = revitParam;
        }
        return map;
    }
}
```

### Step 4: Run mapping tests

- [ ] Run: `dotnet test tests/RevitCli.Tests/ --filter "FullyQualifiedName~CsvMappingTests"`
  Expected: 6 PASS.

### Step 5: Commit

- [ ] Commit:

```bash
git add src/RevitCli/Output/CsvMapping.cs \
        tests/RevitCli.Tests/Output/CsvMappingTests.cs
git commit -m "feat(cli): CsvMapping (--map parsing + identity default + match-by exclude) (P3)"
```

---

## Task 3: ImportPlanner — match, group, classify

**Files:**
- Create: `src/RevitCli/Output/ImportPlanner.cs`
- Create: `tests/RevitCli.Tests/Output/ImportPlannerTests.cs`

### Step 1: Write the failing tests

- [ ] Create `tests/RevitCli.Tests/Output/ImportPlannerTests.cs`:

```csharp
using System.Collections.Generic;
using RevitCli.Output;
using RevitCli.Shared;
using Xunit;

namespace RevitCli.Tests.Output;

public class ImportPlannerTests
{
    private static List<ElementInfo> Elements(params (long Id, string Mark, string? Lock)[] items)
    {
        var result = new List<ElementInfo>();
        foreach (var (id, mark, lck) in items)
        {
            var p = new Dictionary<string, string> { ["Mark"] = mark };
            if (lck != null) p["Lock"] = lck;
            result.Add(new ElementInfo
            {
                Id = id,
                Name = $"E{id}",
                Category = "doors",
                TypeName = "Door",
                Parameters = p
            });
        }
        return result;
    }

    private static CsvData Csv(List<string> headers, params List<string>[] rows) =>
        new() { Headers = headers, Rows = new List<List<string>>(rows) };

    [Fact]
    public void Plan_AllRowsMatch_OneGroupPerUniqueParamValuePair()
    {
        var elements = Elements((101, "W01", null), (102, "W02", null));
        var csv = Csv(
            new() { "Mark", "Lock" },
            new() { "W01", "YALE-500" },
            new() { "W02", "YALE-500" });
        var mapping = new Dictionary<string, string> { ["Lock"] = "Lock" };

        var plan = ImportPlanner.Plan(csv, elements, mapping, "Mark", "warn", "error");

        Assert.Single(plan.Groups);
        var g = plan.Groups[0];
        Assert.Equal("Lock", g.Param);
        Assert.Equal("YALE-500", g.Value);
        Assert.Equal(new[] { 101L, 102L }, g.ElementIds);
        Assert.Empty(plan.Misses);
        Assert.Empty(plan.Duplicates);
    }

    [Fact]
    public void Plan_DifferentValuesPerRow_ProducesGroupPerValue()
    {
        var elements = Elements((101, "W01", null), (102, "W02", null));
        var csv = Csv(
            new() { "Mark", "Lock" },
            new() { "W01", "A" },
            new() { "W02", "B" });
        var mapping = new Dictionary<string, string> { ["Lock"] = "Lock" };

        var plan = ImportPlanner.Plan(csv, elements, mapping, "Mark", "warn", "error");

        Assert.Equal(2, plan.Groups.Count);
        Assert.Contains(plan.Groups, g => g.Value == "A" && g.ElementIds[0] == 101);
        Assert.Contains(plan.Groups, g => g.Value == "B" && g.ElementIds[0] == 102);
    }

    [Fact]
    public void Plan_EmptyCell_SkipsThatColumnForRow()
    {
        var elements = Elements((101, "W01", null));
        var csv = Csv(
            new() { "Mark", "Lock", "Fire" },
            new() { "W01", "", "甲级" });
        var mapping = new Dictionary<string, string> { ["Lock"] = "Lock", ["Fire"] = "FireRating" };

        var plan = ImportPlanner.Plan(csv, elements, mapping, "Mark", "warn", "error");

        Assert.Single(plan.Groups);
        Assert.Equal("FireRating", plan.Groups[0].Param);
        Assert.Equal("甲级", plan.Groups[0].Value);
        Assert.Single(plan.Skipped);
        Assert.Equal("W01", plan.Skipped[0].MatchByValue);
        Assert.Equal("Lock", plan.Skipped[0].Param);
    }

    [Fact]
    public void Plan_RowMatchByValueNotInRevit_OnMissingError_RecordsMiss()
    {
        var elements = Elements((101, "W01", null));
        var csv = Csv(
            new() { "Mark", "Lock" },
            new() { "W99", "X" });
        var mapping = new Dictionary<string, string> { ["Lock"] = "Lock" };

        var plan = ImportPlanner.Plan(csv, elements, mapping, "Mark", "error", "error");

        Assert.Empty(plan.Groups);
        Assert.Single(plan.Misses);
        Assert.Equal("W99", plan.Misses[0].MatchByValue);
        Assert.Equal(2, plan.Misses[0].RowNumber); // header is row 1, first data row is row 2
    }

    [Fact]
    public void Plan_MultipleElementsShareMatchByValue_OnDuplicateError_RecordsDuplicate()
    {
        var elements = Elements((101, "W01", null), (102, "W01", null));
        var csv = Csv(
            new() { "Mark", "Lock" },
            new() { "W01", "X" });
        var mapping = new Dictionary<string, string> { ["Lock"] = "Lock" };

        var plan = ImportPlanner.Plan(csv, elements, mapping, "Mark", "warn", "error");

        Assert.Empty(plan.Groups);
        Assert.Single(plan.Duplicates);
        Assert.Equal(new[] { 101L, 102L }, plan.Duplicates[0].ElementIds);
    }

    [Fact]
    public void Plan_OnDuplicateFirst_PicksLowestId()
    {
        var elements = Elements((102, "W01", null), (101, "W01", null));
        var csv = Csv(
            new() { "Mark", "Lock" },
            new() { "W01", "X" });
        var mapping = new Dictionary<string, string> { ["Lock"] = "Lock" };

        var plan = ImportPlanner.Plan(csv, elements, mapping, "Mark", "warn", "first");

        Assert.Single(plan.Groups);
        Assert.Equal(new[] { 101L }, plan.Groups[0].ElementIds);
        Assert.Empty(plan.Duplicates);
    }

    [Fact]
    public void Plan_OnDuplicateAll_AppliesToAllMatches()
    {
        var elements = Elements((101, "W01", null), (102, "W01", null));
        var csv = Csv(
            new() { "Mark", "Lock" },
            new() { "W01", "X" });
        var mapping = new Dictionary<string, string> { ["Lock"] = "Lock" };

        var plan = ImportPlanner.Plan(csv, elements, mapping, "Mark", "warn", "all");

        Assert.Single(plan.Groups);
        Assert.Equal(new[] { 101L, 102L }, plan.Groups[0].ElementIds);
        Assert.Empty(plan.Duplicates);
    }

    [Fact]
    public void Plan_TwoRowsSameElementSameParam_LastWins_WithWarning()
    {
        var elements = Elements((101, "W01", null));
        var csv = Csv(
            new() { "Mark", "Lock" },
            new() { "W01", "A" },
            new() { "W01", "B" });
        var mapping = new Dictionary<string, string> { ["Lock"] = "Lock" };

        var plan = ImportPlanner.Plan(csv, elements, mapping, "Mark", "warn", "error");

        Assert.Single(plan.Groups);
        Assert.Equal("B", plan.Groups[0].Value);
        Assert.Single(plan.Warnings);
        Assert.Contains("W01", plan.Warnings[0]);
        Assert.Contains("Lock", plan.Warnings[0]);
    }

    [Fact]
    public void Plan_MatchByValueWhitespace_TrimmedBeforeCompare()
    {
        var elements = Elements((101, "W01", null));
        var csv = Csv(
            new() { "Mark", "Lock" },
            new() { "  W01  ", "X" });
        var mapping = new Dictionary<string, string> { ["Lock"] = "Lock" };

        var plan = ImportPlanner.Plan(csv, elements, mapping, "Mark", "warn", "error");

        Assert.Single(plan.Groups);
        Assert.Equal(new[] { 101L }, plan.Groups[0].ElementIds);
    }

    [Fact]
    public void Plan_RowMissingMatchByCell_RecordedAsSkippedWithReason()
    {
        var elements = Elements((101, "W01", null));
        var csv = Csv(
            new() { "Mark", "Lock" },
            new() { "", "X" });
        var mapping = new Dictionary<string, string> { ["Lock"] = "Lock" };

        var plan = ImportPlanner.Plan(csv, elements, mapping, "Mark", "warn", "error");

        Assert.Empty(plan.Groups);
        Assert.Empty(plan.Misses);
        Assert.Single(plan.Skipped);
        Assert.Contains("empty", plan.Skipped[0].Reason);
    }
}
```

### Step 2: Run to verify they fail

- [ ] Run: `dotnet test tests/RevitCli.Tests/ --filter "FullyQualifiedName~ImportPlannerTests"`
  Expected: FAIL — `ImportPlanner` undefined.

### Step 3: Implement ImportPlanner

- [ ] Create `src/RevitCli/Output/ImportPlanner.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using RevitCli.Shared;

namespace RevitCli.Output;

public class ImportGroup
{
    public string Param { get; init; } = "";
    public string Value { get; init; } = "";
    public List<long> ElementIds { get; init; } = new();
    public List<ImportSource> Sources { get; init; } = new();
}

public class ImportSource
{
    public int RowNumber { get; init; }            // 1-based, header = row 1
    public string MatchByValue { get; init; } = "";
}

public class ImportMiss
{
    public int RowNumber { get; init; }
    public string MatchByValue { get; init; } = "";
}

public class ImportDuplicate
{
    public int RowNumber { get; init; }
    public string MatchByValue { get; init; } = "";
    public List<long> ElementIds { get; init; } = new();
}

public class ImportSkip
{
    public int RowNumber { get; init; }
    public string MatchByValue { get; init; } = "";
    public string Param { get; init; } = "";
    public string Reason { get; init; } = "";
}

public class ImportPlan
{
    public List<ImportGroup> Groups { get; init; } = new();
    public List<ImportMiss> Misses { get; init; } = new();
    public List<ImportDuplicate> Duplicates { get; init; } = new();
    public List<ImportSkip> Skipped { get; init; } = new();
    public List<string> Warnings { get; init; } = new();
}

public static class ImportPlanner
{
    /// <summary>
    /// Build an import plan: match each CSV row to Revit elements via matchBy parameter,
    /// then group write operations by (revitParam, value) for batched submission.
    /// </summary>
    /// <param name="onMissing">"error" | "warn" | "skip" — only "error" and "warn" record a Miss; "skip" silently drops.</param>
    /// <param name="onDuplicate">"error" | "first" | "all"</param>
    public static ImportPlan Plan(
        CsvData csv,
        IReadOnlyList<ElementInfo> elements,
        IReadOnlyDictionary<string, string> mapping,
        string matchBy,
        string onMissing,
        string onDuplicate)
    {
        var plan = new ImportPlan();

        // Index elements by matchBy parameter value.
        var index = new Dictionary<string, List<long>>(StringComparer.Ordinal);
        foreach (var el in elements)
        {
            if (!el.Parameters.TryGetValue(matchBy, out var key)) continue;
            var trimmed = key?.Trim() ?? "";
            if (trimmed.Length == 0) continue;
            if (!index.TryGetValue(trimmed, out var list))
            {
                list = new List<long>();
                index[trimmed] = list;
            }
            list.Add(el.Id);
        }
        foreach (var list in index.Values) list.Sort();

        // Resolve column index for matchBy and each mapped column.
        var matchByCol = csv.Headers.IndexOf(matchBy);
        if (matchByCol < 0)
            throw new InvalidOperationException(
                $"--match-by column '{matchBy}' not found in CSV headers.");

        var mappedColumns = csv.Headers
            .Select((h, idx) => (Header: h, Index: idx))
            .Where(t => mapping.ContainsKey(t.Header))
            .ToList();

        // (elementId, revitParam) -> latest (value, rowNumber, matchByValue) so "last wins" + warning.
        var assignments = new Dictionary<(long Id, string Param), (string Value, int Row, string MatchKey)>();
        var warnedKeys = new HashSet<(long Id, string Param)>();

        for (var rowIdx = 0; rowIdx < csv.Rows.Count; rowIdx++)
        {
            var rowNum = rowIdx + 2; // header + 0-based → 1-based data row
            var row = csv.Rows[rowIdx];
            if (matchByCol >= row.Count) continue;

            var matchKey = row[matchByCol]?.Trim() ?? "";
            if (matchKey.Length == 0)
            {
                plan.Skipped.Add(new ImportSkip
                {
                    RowNumber = rowNum,
                    MatchByValue = "",
                    Param = matchBy,
                    Reason = "match-by cell is empty"
                });
                continue;
            }

            if (!index.TryGetValue(matchKey, out var matched))
            {
                if (onMissing != "skip")
                    plan.Misses.Add(new ImportMiss { RowNumber = rowNum, MatchByValue = matchKey });
                continue;
            }

            List<long> targets;
            if (matched.Count > 1)
            {
                targets = onDuplicate switch
                {
                    "first" => new List<long> { matched[0] },
                    "all" => matched,
                    _ => null!
                };
                if (targets == null!)
                {
                    plan.Duplicates.Add(new ImportDuplicate
                    {
                        RowNumber = rowNum,
                        MatchByValue = matchKey,
                        ElementIds = matched
                    });
                    continue;
                }
            }
            else
            {
                targets = matched;
            }

            foreach (var (header, colIdx) in mappedColumns)
            {
                var revitParam = mapping[header];
                var raw = colIdx < row.Count ? (row[colIdx] ?? "") : "";
                if (raw.Length == 0)
                {
                    plan.Skipped.Add(new ImportSkip
                    {
                        RowNumber = rowNum,
                        MatchByValue = matchKey,
                        Param = revitParam,
                        Reason = "cell is empty"
                    });
                    continue;
                }

                foreach (var id in targets)
                {
                    var key = (id, revitParam);
                    if (assignments.TryGetValue(key, out var prev))
                    {
                        if (prev.Value != raw && warnedKeys.Add(key))
                            plan.Warnings.Add(
                                $"Row {rowNum}: '{matchKey}' / '{revitParam}' " +
                                $"overrides earlier value from row {prev.Row} ('{prev.Value}' → '{raw}').");
                    }
                    assignments[key] = (raw, rowNum, matchKey);
                }
            }
        }

        // Group by (param, value) → ImportGroup.
        var grouped = assignments
            .GroupBy(kv => (kv.Key.Param, kv.Value.Value))
            .OrderBy(g => g.Key.Param, StringComparer.Ordinal)
            .ThenBy(g => g.Key.Value, StringComparer.Ordinal);

        foreach (var grp in grouped)
        {
            var ids = grp.Select(kv => kv.Key.Id).Distinct().OrderBy(x => x).ToList();
            var sources = grp
                .Select(kv => new ImportSource { RowNumber = kv.Value.Row, MatchByValue = kv.Value.MatchKey })
                .GroupBy(s => s.RowNumber)
                .Select(g => g.First())
                .OrderBy(s => s.RowNumber)
                .ToList();
            plan.Groups.Add(new ImportGroup
            {
                Param = grp.Key.Param,
                Value = grp.Key.Value,
                ElementIds = ids,
                Sources = sources
            });
        }

        return plan;
    }
}
```

### Step 4: Run planner tests

- [ ] Run: `dotnet test tests/RevitCli.Tests/ --filter "FullyQualifiedName~ImportPlannerTests"`
  Expected: 10 PASS.

### Step 5: Commit

- [ ] Commit:

```bash
git add src/RevitCli/Output/ImportPlanner.cs \
        tests/RevitCli.Tests/Output/ImportPlannerTests.cs
git commit -m "feat(cli): ImportPlanner — match + group + missing/duplicate policies (P3)"
```

---

## Task 4: ImportCommand — wire CSV → planner → SetParameter (no real apply yet)

**Files:**
- Create: `src/RevitCli/Commands/ImportCommand.cs`
- Create: `tests/RevitCli.Tests/Commands/ImportCommandTests.cs`

### Step 1: Add a minimal ImportCommand with flag parsing and ExecuteAsync skeleton

- [ ] Create `src/RevitCli/Commands/ImportCommand.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using RevitCli.Client;
using RevitCli.Output;
using RevitCli.Shared;
using Spectre.Console;

namespace RevitCli.Commands;

public static class ImportCommand
{
    public static Command Create(RevitClient client)
    {
        var fileArg = new Argument<string>("file", "Path to CSV file");
        var categoryOpt = new Option<string>("--category", "Revit category (walls, doors, 墙, 门, etc.)") { IsRequired = true };
        var matchByOpt = new Option<string>("--match-by", "Parameter name linking CSV row to Revit element (e.g. Mark)") { IsRequired = true };
        var mapOpt = new Option<string?>("--map", "Explicit column→param mapping (e.g. \"col:Param,col2:Param2\")");
        var dryRunOpt = new Option<bool>("--dry-run", "Preview changes without committing");
        var onMissingOpt = new Option<string>("--on-missing", () => "warn", "error|warn|skip — when CSV row has no Revit match");
        var onDuplicateOpt = new Option<string>("--on-duplicate", () => "error", "error|first|all — when Revit has multiple matches");
        var encodingOpt = new Option<string>("--encoding", () => "auto", "utf-8|gbk|auto");
        var batchSizeOpt = new Option<int>("--batch-size", () => 100, "Max ElementIds per SetRequest (1..1000)");

        var command = new Command("import", "Batch-write Revit element parameters from a CSV file")
        {
            fileArg, categoryOpt, matchByOpt, mapOpt, dryRunOpt,
            onMissingOpt, onDuplicateOpt, encodingOpt, batchSizeOpt
        };

        command.SetHandler(async (file, category, matchBy, map, dryRun, onMissing, onDup, encoding, batchSize) =>
        {
            Environment.ExitCode = await ExecuteAsync(
                client, file, category, matchBy, map, dryRun, onMissing, onDup, encoding, batchSize, Console.Out);
        }, fileArg, categoryOpt, matchByOpt, mapOpt, dryRunOpt, onMissingOpt, onDuplicateOpt, encodingOpt, batchSizeOpt);

        return command;
    }

    public static async Task<int> ExecuteAsync(
        RevitClient client,
        string file,
        string category,
        string matchBy,
        string? rawMap,
        bool dryRun,
        string onMissing,
        string onDuplicate,
        string encodingHint,
        int batchSize,
        TextWriter output)
    {
        if (!ValidatePolicies(onMissing, onDuplicate, batchSize, output))
            return 1;

        if (!File.Exists(file))
        {
            await output.WriteLineAsync($"Error: CSV file not found: {file}");
            return 1;
        }

        CsvData csv;
        try
        {
            csv = CsvParser.ParseFile(file, encodingHint);
        }
        catch (Exception ex)
        {
            await output.WriteLineAsync($"Error: failed to parse CSV: {ex.Message}");
            return 1;
        }

        if (csv.Rows.Count == 0)
        {
            await output.WriteLineAsync($"No rows to import (encoding={csv.EncodingName}, headers={csv.Headers.Count}).");
            return 0;
        }

        Dictionary<string, string> mapping;
        try
        {
            mapping = CsvMapping.Build(rawMap, csv.Headers, matchBy);
        }
        catch (InvalidOperationException ex)
        {
            await output.WriteLineAsync($"Error: {ex.Message}");
            return 1;
        }

        if (mapping.Count == 0)
        {
            await output.WriteLineAsync(
                $"Error: no writable columns. CSV has only the --match-by column '{matchBy}' " +
                "or all other columns are excluded by --map.");
            return 1;
        }

        var query = await client.QueryElementsAsync(category, filter: null);
        if (!query.Success)
        {
            await output.WriteLineAsync($"Error: {query.Error}");
            return 1;
        }

        var plan = ImportPlanner.Plan(csv, query.Data!, mapping, matchBy, onMissing, onDuplicate);

        await EmitPlanSummary(plan, csv, mapping, matchBy, dryRun, output);

        if (HasFatalPlanIssue(plan, onMissing, onDuplicate))
            return 1;

        if (dryRun || plan.Groups.Count == 0)
            return 0;

        var (totalAffected, failures) = await ApplyPlan(client, plan, batchSize, output);

        await output.WriteLineAsync($"Modified {totalAffected} element-parameter pair(s) across {plan.Groups.Count} group(s).");
        if (failures.Count > 0)
        {
            await output.WriteLineAsync($"Failed: {failures.Count} group(s):");
            foreach (var (g, msg) in failures)
                await output.WriteLineAsync($"  - {g.Param}={g.Value} (ids={string.Join(",", g.ElementIds)}): {msg}");
            return 2;
        }
        return 0;
    }

    private static bool ValidatePolicies(string onMissing, string onDuplicate, int batchSize, TextWriter output)
    {
        if (onMissing != "error" && onMissing != "warn" && onMissing != "skip")
        {
            output.WriteLine($"Error: --on-missing must be one of: error, warn, skip (got '{onMissing}').");
            return false;
        }
        if (onDuplicate != "error" && onDuplicate != "first" && onDuplicate != "all")
        {
            output.WriteLine($"Error: --on-duplicate must be one of: error, first, all (got '{onDuplicate}').");
            return false;
        }
        if (batchSize < 1 || batchSize > 1000)
        {
            output.WriteLine($"Error: --batch-size must be between 1 and 1000 (got {batchSize}).");
            return false;
        }
        return true;
    }

    private static async Task EmitPlanSummary(
        ImportPlan plan, CsvData csv, IReadOnlyDictionary<string, string> mapping,
        string matchBy, bool dryRun, TextWriter output)
    {
        var totalIds = plan.Groups.Sum(g => g.ElementIds.Count);
        var prefix = dryRun ? "Dry run:" : "Plan:";
        await output.WriteLineAsync(
            $"{prefix} encoding={csv.EncodingName}, csvRows={csv.Rows.Count}, mappedColumns={mapping.Count}, " +
            $"matchBy={matchBy}, groups={plan.Groups.Count}, elementWrites={totalIds}.");

        if (plan.Skipped.Count > 0)
            await output.WriteLineAsync($"  Skipped cells: {plan.Skipped.Count} (empty values or empty match-by).");
        if (plan.Misses.Count > 0)
        {
            await output.WriteLineAsync($"  Misses: {plan.Misses.Count}");
            foreach (var m in plan.Misses.Take(10))
                await output.WriteLineAsync($"    row {m.RowNumber}: '{m.MatchByValue}' has no match in Revit.");
            if (plan.Misses.Count > 10)
                await output.WriteLineAsync($"    ... and {plan.Misses.Count - 10} more.");
        }
        if (plan.Duplicates.Count > 0)
        {
            await output.WriteLineAsync($"  Duplicates: {plan.Duplicates.Count}");
            foreach (var d in plan.Duplicates.Take(10))
                await output.WriteLineAsync(
                    $"    row {d.RowNumber}: '{d.MatchByValue}' matches {d.ElementIds.Count} elements ({string.Join(",", d.ElementIds)}).");
            if (plan.Duplicates.Count > 10)
                await output.WriteLineAsync($"    ... and {plan.Duplicates.Count - 10} more.");
        }
        foreach (var w in plan.Warnings)
            await output.WriteLineAsync($"  Warning: {w}");

        if (dryRun)
        {
            foreach (var g in plan.Groups.Take(20))
                await output.WriteLineAsync(
                    $"  [{g.Param}] = '{g.Value}' on {g.ElementIds.Count} element(s): {string.Join(",", g.ElementIds.Take(20))}{(g.ElementIds.Count > 20 ? ",..." : "")}");
            if (plan.Groups.Count > 20)
                await output.WriteLineAsync($"  ... and {plan.Groups.Count - 20} more groups.");
        }
    }

    private static bool HasFatalPlanIssue(ImportPlan plan, string onMissing, string onDuplicate)
    {
        if (onMissing == "error" && plan.Misses.Count > 0) return true;
        if (onDuplicate == "error" && plan.Duplicates.Count > 0) return true;
        return false;
    }

    private static async Task<(int Affected, List<(ImportGroup, string)> Failures)> ApplyPlan(
        RevitClient client, ImportPlan plan, int batchSize, TextWriter output)
    {
        var failures = new List<(ImportGroup, string)>();
        var affected = 0;

        foreach (var group in plan.Groups)
        {
            for (var off = 0; off < group.ElementIds.Count; off += batchSize)
            {
                var slice = group.ElementIds.GetRange(off, Math.Min(batchSize, group.ElementIds.Count - off));
                var req = new SetRequest
                {
                    ElementIds = slice,
                    Param = group.Param,
                    Value = group.Value,
                    DryRun = false
                };
                var resp = await client.SetParameterAsync(req);
                if (!resp.Success)
                {
                    failures.Add((group, resp.Error ?? "unknown"));
                    break; // skip remaining slices of this group
                }
                affected += resp.Data?.Affected ?? 0;
            }
        }

        return (affected, failures);
    }
}
```

- [ ] Run: `dotnet build src/RevitCli/RevitCli.csproj`
  Expected: SUCCESS.

### Step 2: Write the failing command tests (FakeHttpHandler-driven)

- [ ] Create `tests/RevitCli.Tests/Commands/ImportCommandTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RevitCli.Client;
using RevitCli.Commands;
using RevitCli.Shared;
using Xunit;

namespace RevitCli.Tests.Commands;

public class ImportCommandTests : IDisposable
{
    private readonly string _tempDir;

    public ImportCommandTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"revitcli-import-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private string WriteCsv(string name, string contents, Encoding? enc = null)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllBytes(path, (enc ?? new UTF8Encoding(false)).GetBytes(contents));
        return path;
    }

    private (RevitClient client, FakeHandler handler) MakeClient(
        ElementInfo[] queryElements,
        Func<SetRequest, SetResult>? setHandler = null)
    {
        var handler = new FakeHandler(queryElements, setHandler);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        return (new RevitClient(http), handler);
    }

    [Fact]
    public async Task Execute_FileMissing_Returns1()
    {
        var (client, _) = MakeClient(Array.Empty<ElementInfo>());
        var sw = new StringWriter();
        var code = await ImportCommand.ExecuteAsync(
            client, file: Path.Combine(_tempDir, "nope.csv"),
            category: "doors", matchBy: "Mark",
            rawMap: null, dryRun: false,
            onMissing: "warn", onDuplicate: "error",
            encodingHint: "auto", batchSize: 100,
            output: sw);
        Assert.Equal(1, code);
        Assert.Contains("not found", sw.ToString());
    }

    [Fact]
    public async Task Execute_InvalidOnMissing_Returns1()
    {
        var path = WriteCsv("a.csv", "Mark,Lock\nW01,X\n");
        var (client, _) = MakeClient(Array.Empty<ElementInfo>());
        var sw = new StringWriter();
        var code = await ImportCommand.ExecuteAsync(
            client, path, "doors", "Mark", null, false, "wrong", "error", "auto", 100, sw);
        Assert.Equal(1, code);
        Assert.Contains("--on-missing", sw.ToString());
    }

    [Fact]
    public async Task Execute_InvalidBatchSize_Returns1()
    {
        var path = WriteCsv("a.csv", "Mark,Lock\nW01,X\n");
        var (client, _) = MakeClient(Array.Empty<ElementInfo>());
        var sw = new StringWriter();
        var code = await ImportCommand.ExecuteAsync(
            client, path, "doors", "Mark", null, false, "warn", "error", "auto", 0, sw);
        Assert.Equal(1, code);
        Assert.Contains("--batch-size", sw.ToString());
    }

    [Fact]
    public async Task Execute_MatchByMissingFromHeaders_Returns1()
    {
        var path = WriteCsv("a.csv", "Tag,Lock\nW01,X\n");
        var (client, _) = MakeClient(Array.Empty<ElementInfo>());
        var sw = new StringWriter();
        var code = await ImportCommand.ExecuteAsync(
            client, path, "doors", "Mark", null, false, "warn", "error", "auto", 100, sw);
        Assert.Equal(1, code);
        Assert.Contains("Mark", sw.ToString());
    }

    [Fact]
    public async Task Execute_OnlyMatchByColumn_Returns1_NoWritableColumns()
    {
        var path = WriteCsv("a.csv", "Mark\nW01\n");
        var elements = new[]
        {
            new ElementInfo { Id = 101, Parameters = new() { ["Mark"] = "W01" } }
        };
        var (client, _) = MakeClient(elements);
        var sw = new StringWriter();
        var code = await ImportCommand.ExecuteAsync(
            client, path, "doors", "Mark", null, false, "warn", "error", "auto", 100, sw);
        Assert.Equal(1, code);
        Assert.Contains("no writable columns", sw.ToString());
    }

    [Fact]
    public async Task Execute_DryRun_NoSetCalls_Returns0_WithPreview()
    {
        var path = WriteCsv("a.csv", "Mark,Lock\nW01,YALE-500\nW02,YALE-500\n");
        var elements = new[]
        {
            new ElementInfo { Id = 101, Parameters = new() { ["Mark"] = "W01" } },
            new ElementInfo { Id = 102, Parameters = new() { ["Mark"] = "W02" } }
        };
        var (client, handler) = MakeClient(elements);
        var sw = new StringWriter();
        var code = await ImportCommand.ExecuteAsync(
            client, path, "doors", "Mark", null, dryRun: true,
            "warn", "error", "auto", 100, sw);

        Assert.Equal(0, code);
        Assert.Equal(0, handler.SetCalls);
        Assert.Contains("Dry run:", sw.ToString());
        Assert.Contains("[Lock] = 'YALE-500'", sw.ToString());
    }

    [Fact]
    public async Task Execute_RealRun_GroupsAndIssuesBatchSetRequests()
    {
        var path = WriteCsv("a.csv", "Mark,Lock\nW01,A\nW02,A\nW03,B\n");
        var elements = new[]
        {
            new ElementInfo { Id = 101, Parameters = new() { ["Mark"] = "W01" } },
            new ElementInfo { Id = 102, Parameters = new() { ["Mark"] = "W02" } },
            new ElementInfo { Id = 103, Parameters = new() { ["Mark"] = "W03" } }
        };
        var captured = new List<SetRequest>();
        var (client, handler) = MakeClient(elements, req =>
        {
            captured.Add(req);
            return new SetResult { Affected = req.ElementIds!.Count };
        });
        var sw = new StringWriter();
        var code = await ImportCommand.ExecuteAsync(
            client, path, "doors", "Mark", null, false, "warn", "error", "auto", 100, sw);

        Assert.Equal(0, code);
        Assert.Equal(2, captured.Count);
        var groupA = captured.Find(r => r.Value == "A")!;
        Assert.Equal(new List<long> { 101, 102 }, groupA.ElementIds);
        var groupB = captured.Find(r => r.Value == "B")!;
        Assert.Equal(new List<long> { 103 }, groupB.ElementIds);
    }

    [Fact]
    public async Task Execute_BatchSizeChunksLargeGroup()
    {
        var sb = new StringBuilder("Mark,Lock\n");
        for (var i = 0; i < 5; i++) sb.Append($"W{i:D2},A\n");
        var path = WriteCsv("a.csv", sb.ToString());

        var elements = new ElementInfo[5];
        for (var i = 0; i < 5; i++)
            elements[i] = new ElementInfo { Id = 100 + i, Parameters = new() { ["Mark"] = $"W{i:D2}" } };

        var captured = new List<SetRequest>();
        var (client, _) = MakeClient(elements, req =>
        {
            captured.Add(req);
            return new SetResult { Affected = req.ElementIds!.Count };
        });
        var sw = new StringWriter();
        var code = await ImportCommand.ExecuteAsync(
            client, path, "doors", "Mark", null, false, "warn", "error", "auto", batchSize: 2, sw);

        Assert.Equal(0, code);
        Assert.Equal(3, captured.Count); // 5 ids / batch=2 → 2,2,1
        Assert.Equal(2, captured[0].ElementIds!.Count);
        Assert.Equal(2, captured[1].ElementIds!.Count);
        Assert.Equal(1, captured[2].ElementIds!.Count);
    }

    [Fact]
    public async Task Execute_OnMissingError_WithMiss_Returns1_NoSetCalls()
    {
        var path = WriteCsv("a.csv", "Mark,Lock\nW99,X\n");
        var elements = new[]
        {
            new ElementInfo { Id = 101, Parameters = new() { ["Mark"] = "W01" } }
        };
        var (client, handler) = MakeClient(elements);
        var sw = new StringWriter();
        var code = await ImportCommand.ExecuteAsync(
            client, path, "doors", "Mark", null, false, "error", "error", "auto", 100, sw);

        Assert.Equal(1, code);
        Assert.Equal(0, handler.SetCalls);
        Assert.Contains("Misses", sw.ToString());
    }

    [Fact]
    public async Task Execute_OnDuplicateFirst_PicksLowestId_Succeeds()
    {
        var path = WriteCsv("a.csv", "Mark,Lock\nW01,X\n");
        var elements = new[]
        {
            new ElementInfo { Id = 102, Parameters = new() { ["Mark"] = "W01" } },
            new ElementInfo { Id = 101, Parameters = new() { ["Mark"] = "W01" } }
        };
        var captured = new List<SetRequest>();
        var (client, _) = MakeClient(elements, req =>
        {
            captured.Add(req);
            return new SetResult { Affected = req.ElementIds!.Count };
        });
        var sw = new StringWriter();
        var code = await ImportCommand.ExecuteAsync(
            client, path, "doors", "Mark", null, false, "warn", "first", "auto", 100, sw);

        Assert.Equal(0, code);
        Assert.Single(captured);
        Assert.Equal(new List<long> { 101 }, captured[0].ElementIds);
    }

    [Fact]
    public async Task Execute_PartialFailure_Returns2_AggregatesFailedGroups()
    {
        var path = WriteCsv("a.csv", "Mark,Lock\nW01,A\nW02,B\n");
        var elements = new[]
        {
            new ElementInfo { Id = 101, Parameters = new() { ["Mark"] = "W01" } },
            new ElementInfo { Id = 102, Parameters = new() { ["Mark"] = "W02" } }
        };
        var (client, _) = MakeClient(elements, req =>
        {
            if (req.Value == "B") throw new InvalidOperationException("locked");
            return new SetResult { Affected = req.ElementIds!.Count };
        });
        var sw = new StringWriter();
        var code = await ImportCommand.ExecuteAsync(
            client, path, "doors", "Mark", null, false, "warn", "error", "auto", 100, sw);

        Assert.Equal(2, code);
        var text = sw.ToString();
        Assert.Contains("Failed:", text);
        Assert.Contains("Lock=B", text);
    }

    [Fact]
    public async Task Execute_GbkChineseFile_ParsedAndApplied()
    {
        var gbk = Encoding.GetEncoding("gbk");
        var path = WriteCsv("gbk.csv", "Mark,锁具型号\nW01,YALE-500\n", gbk);

        var elements = new[]
        {
            new ElementInfo { Id = 101, Parameters = new() { ["Mark"] = "W01" } }
        };
        var captured = new List<SetRequest>();
        var (client, _) = MakeClient(elements, req =>
        {
            captured.Add(req);
            return new SetResult { Affected = req.ElementIds!.Count };
        });
        var sw = new StringWriter();
        var code = await ImportCommand.ExecuteAsync(
            client, path, "doors", "Mark", null, false, "warn", "error", "auto", 100, sw);

        Assert.Equal(0, code);
        Assert.Single(captured);
        Assert.Equal("锁具型号", captured[0].Param);
        Assert.Equal("YALE-500", captured[0].Value);
        Assert.Contains("encoding=gbk", sw.ToString());
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly ElementInfo[] _elements;
        private readonly Func<SetRequest, SetResult>? _setHandler;
        public int SetCalls { get; private set; }

        public FakeHandler(ElementInfo[] elements, Func<SetRequest, SetResult>? setHandler)
        {
            _elements = elements;
            _setHandler = setHandler;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var jsonOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

            if (path == "/api/elements" && request.Method == HttpMethod.Get)
            {
                var resp = new ApiResponse<ElementInfo[]> { Success = true, Data = _elements };
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(resp))
                };
            }
            if (path == "/api/elements/set" && request.Method == HttpMethod.Post)
            {
                SetCalls++;
                var body = await request.Content!.ReadAsStringAsync(ct);
                var req = JsonSerializer.Deserialize<SetRequest>(body, jsonOpts)!;
                try
                {
                    var data = _setHandler != null ? _setHandler(req) : new SetResult();
                    var resp = new ApiResponse<SetResult> { Success = true, Data = data };
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(JsonSerializer.Serialize(resp))
                    };
                }
                catch (Exception ex)
                {
                    var resp = new ApiResponse<SetResult> { Success = false, Error = ex.Message };
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(JsonSerializer.Serialize(resp))
                    };
                }
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }
}
```

### Step 3: Run command tests

- [ ] Run: `dotnet test tests/RevitCli.Tests/ --filter "FullyQualifiedName~ImportCommandTests"`
  Expected: 12 PASS.

### Step 4: Commit

- [ ] Commit:

```bash
git add src/RevitCli/Commands/ImportCommand.cs \
        tests/RevitCli.Tests/Commands/ImportCommandTests.cs
git commit -m "feat(cli): import command — CSV → grouped batch SetRequest with policies (P3)"
```

---

## Task 5: Register `import` in CLI catalog

**Files:**
- Modify: `src/RevitCli/Commands/CliCommandCatalog.cs`

### Step 1: Locate registration sites

- [ ] Run: `grep -n "snapshot\|TopLevelCommands\|CreateRootCommand" src/RevitCli/Commands/CliCommandCatalog.cs`
  Use the snapshot/diff entries as the template (they were added in P1 and follow the canonical pattern).

### Step 2: Add `import` registration

- [ ] In `src/RevitCli/Commands/CliCommandCatalog.cs`:
  - In the `TopLevelCommands` array, add `"import"` next to `"snapshot"` and `"diff"` (alphabetical or grouped — match the file's existing convention).
  - In `CreateRootCommand`, immediately after the line that adds `DiffCommand.Create(client)` (or wherever snapshot/diff register), add:

```csharp
root.AddCommand(ImportCommand.Create(client));
```

- [ ] Run: `dotnet build src/RevitCli/RevitCli.csproj`
  Expected: SUCCESS.

- [ ] Run: `dotnet test tests/RevitCli.Tests/`
  Expected: ALL PASS (the 38+ new facts plus all previous pass).

### Step 3: Commit

- [ ] Commit:

```bash
git add src/RevitCli/Commands/CliCommandCatalog.cs
git commit -m "feat(cli): register import in command catalog (P3)"
```

---

## Task 6: Shell completions for `import`

**Files:**
- Modify: `src/RevitCli/Commands/CompletionsCommand.cs`
- Modify: `tests/RevitCli.Tests/Commands/CompletionsCommandTests.cs`

### Step 1: Add failing assertions

- [ ] Read `tests/RevitCli.Tests/Commands/CompletionsCommandTests.cs` to find existing per-shell test methods (one each for bash/zsh/pwsh, judging from P2 history).
- [ ] Append three Facts (one per shell) like:

```csharp
[Fact]
public void Bash_Includes_ImportFlags()
{
    var script = CompletionsCommand.GenerateBash();
    Assert.Contains("import", script);
    Assert.Contains("--category", script);
    Assert.Contains("--match-by", script);
    Assert.Contains("--map", script);
    Assert.Contains("--dry-run", script);
    Assert.Contains("--on-missing", script);
    Assert.Contains("--on-duplicate", script);
    Assert.Contains("--encoding", script);
    Assert.Contains("--batch-size", script);
}

[Fact]
public void Zsh_Includes_ImportFlags()
{
    var script = CompletionsCommand.GenerateZsh();
    Assert.Contains("import)", script);
    Assert.Contains("--match-by", script);
    Assert.Contains("--on-missing", script);
}

[Fact]
public void Pwsh_Includes_ImportFlags()
{
    var script = CompletionsCommand.GeneratePwsh();
    Assert.Contains("'import'", script);
    Assert.Contains("--match-by", script);
    Assert.Contains("--encoding", script);
}
```

(If the existing tests use `Generate(string shell)` instead of three methods, mirror that. Adapt the assertions to whatever naming convention is already in place.)

- [ ] Run: `dotnet test tests/RevitCli.Tests/ --filter "FullyQualifiedName~CompletionsCommandTests"`
  Expected: NEW FAIL — import block missing.

### Step 2: Implement the completions

- [ ] In `src/RevitCli/Commands/CompletionsCommand.cs`, find the `case "publish":` branches in each shell generator and add a sibling `case "import":` block right after.

For bash (mirror the existing publish case style):

```bash
        import)
            COMPREPLY=( $(compgen -W "--category --match-by --map --dry-run --on-missing --on-duplicate --encoding --batch-size --help" -- "$cur") )
            case "$prev" in
                --on-missing) COMPREPLY=( $(compgen -W "error warn skip" -- "$cur") ) ;;
                --on-duplicate) COMPREPLY=( $(compgen -W "error first all" -- "$cur") ) ;;
                --encoding) COMPREPLY=( $(compgen -W "auto utf-8 gbk" -- "$cur") ) ;;
            esac
            return 0
            ;;
```

For zsh (mirror existing pattern; adjust to actual style in file):

```zsh
        import)
            _arguments \
                '--category[Revit category]:category:' \
                '--match-by[CSV column → Revit param key]:param:' \
                '--map[col:Param,col2:Param2]:mapping:' \
                '--dry-run[Preview only]' \
                '--on-missing[Behavior on missing match]:mode:(error warn skip)' \
                '--on-duplicate[Behavior on duplicate match]:mode:(error first all)' \
                '--encoding[CSV encoding]:enc:(auto utf-8 gbk)' \
                '--batch-size[ElementIds per SetRequest]:n:'
            ;;
```

For pwsh:

```powershell
        'import' {
            @('--category','--match-by','--map','--dry-run','--on-missing','--on-duplicate','--encoding','--batch-size','--help') |
                Where-Object { $_ -like "$wordToComplete*" } |
                ForEach-Object { [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterName', $_) }
        }
```

(Use whatever exact data structures the file already uses for the publish case; the goal is one new arm that matches the existing pattern.)

### Step 3: Run completions tests

- [ ] Run: `dotnet test tests/RevitCli.Tests/ --filter "FullyQualifiedName~CompletionsCommandTests"`
  Expected: ALL PASS.

### Step 4: Commit

- [ ] Commit:

```bash
git add src/RevitCli/Commands/CompletionsCommand.cs \
        tests/RevitCli.Tests/Commands/CompletionsCommandTests.cs
git commit -m "feat(cli): shell completions for import command (P3)"
```

---

## Task 7: CHANGELOG and version bump

**Files:**
- Modify: `CHANGELOG.md`
- Modify: `src/RevitCli/RevitCli.csproj`

### Step 1: Bump version

- [ ] In `src/RevitCli/RevitCli.csproj` change `<Version>1.2.0</Version>` to `<Version>1.3.0</Version>`.

### Step 2: Add CHANGELOG entry

- [ ] Read `CHANGELOG.md` head — find the "1.2.0" entry to use as a template (P2 entry style).
- [ ] Add an entry above it:

```markdown
## [1.3.0] — 2026-04-XX

### Added — Model-as-Code Phase 3 (CSV import)

- `revitcli import FILE.csv` — declarative bulk parameter writeback from CSV.
  - `--category` / `--match-by` required (e.g. `--match-by Mark` keys CSV rows to Revit elements).
  - `--map "col:Param,col2:Param2"` for explicit column → Revit parameter mapping; defaults to identity for unspecified columns.
  - `--dry-run` previews per-group changes without applying.
  - `--on-missing error|warn|skip` (default `warn`) — CSV row whose key is not in Revit.
  - `--on-duplicate error|first|all` (default `error`) — Revit has multiple elements matching one CSV key.
  - `--encoding utf-8|gbk|auto` (default `auto`) — auto detection: BOM → strict UTF-8 → GBK fallback. Required for Excel-exported Chinese CSV.
  - `--batch-size N` (default 100) — chunk size when batching ElementIds per `SetRequest`.
- `CsvParser` (`src/RevitCli/Output/CsvParser.cs`): RFC 4180 quoting + escape handling, BOM detection, GBK fallback via `System.Text.Encoding.CodePages` (registered at startup).
- `CsvMapping` (`src/RevitCli/Output/CsvMapping.cs`): `--map` parsing + identity-default fallback, excludes `--match-by` column from writes.
- `ImportPlanner` (`src/RevitCli/Output/ImportPlanner.cs`): builds `(param, value) → ElementIds[]` groups for batched submission; classifies misses, duplicates, skips; emits "last-write-wins" warnings for repeated keys.
- Shell completions for `import` (bash/zsh/pwsh) — `--on-missing`, `--on-duplicate`, `--encoding` value-completed.

### Changed

- `src/RevitCli/Program.cs`: registers `CodePagesEncodingProvider` at startup so `Encoding.GetEncoding("gbk")` works on Linux/.NET 8.
- `src/RevitCli/RevitCli.csproj`: adds `System.Text.Encoding.CodePages 9.0.0`.

### Backward compatibility

- No changes to existing commands, profiles, DTOs, or addin endpoints. `import` reuses the existing `/api/elements` (query) and `/api/elements/set` (write) endpoints — no addin upgrade required.

### Test count

- 38 new facts (`CsvParserTests` × 10, `CsvMappingTests` × 6, `ImportPlannerTests` × 10, `ImportCommandTests` × 12) + 3 completion asserts.
- Total CLI test suite: 249+ facts (was 211 after P2).

### E2E verification (manual, on Windows + Revit 2026)

1. Build addin and CLI on Windows; restart Revit so addin loads.
2. In Revit: create or open a model with at least 3 doors; set Mark = `W01`, `W02`, `W03`.
3. Write `doors.csv` (UTF-8):
   ```
   Mark,锁具型号
   W01,YALE-500
   W02,YALE-700
   W03,YALE-500
   ```
4. `revitcli import doors.csv --category doors --match-by Mark --dry-run` → expect 2 groups, 3 element writes.
5. `revitcli import doors.csv --category doors --match-by Mark` → expect "Modified 3 element-parameter pair(s)".
6. `revitcli query doors` → confirm `锁具型号` populated per CSV.
7. Re-save the same CSV from Excel as GBK and re-run; expect `encoding=gbk` line and same outcome.

### Known carry-forward

- Items already noted in v1.2.0 (Revit 2024 build compat for `new ElementId(long)`, `--verbose` / `--severity` unimplemented, no progress signal during snapshot, README not updated).
- `import` writes only existing parameters; if CSV references a Revit parameter that does not exist on the target element, the addin's `set` returns success with `Affected = 0` for that element. Treat 0 affected as a soft signal; future enhancement may surface this distinctly.
- No interactive Spectre.Console table rendering for `import` results — TTY and pipe both use plain text. Acceptable because `import` output is mostly counts; if needed later, mirror SetCommand.cs Spectre branch.
```

### Step 3: Run full suite once more

- [ ] Run: `dotnet build src/RevitCli/RevitCli.csproj && dotnet test tests/RevitCli.Tests/`
  Expected: BUILD SUCCESS, all tests PASS.

### Step 4: Commit

- [ ] Commit:

```bash
git add CHANGELOG.md src/RevitCli/RevitCli.csproj
git commit -m "docs(cli): CHANGELOG + version bump for v1.3.0 (Model-as-Code P3)"
```

---

## Task 8: Hand-off — push branch + open PR

**Files:** none (git/gh).

### Step 1: Verify clean working tree

- [ ] Run: `git status`
  Expected: only untracked user-test artifacts (`.revitcli.yml`, `walls.json`, `exports/`, etc.). Working tree clean of P3 files.

### Step 2: Push branch

- [ ] Run: `git push -u origin feat/model-as-code-p3`

### Step 3: Open PR

- [ ] Run:

```bash
gh pr create --base main --head feat/model-as-code-p3 \
  --title "feat: model-as-code phase 3 — import csv (v1.3.0)" \
  --body "$(cat <<'EOF'
## Summary
- Adds `revitcli import FILE.csv` for declarative CSV → Revit parameter writeback.
- New: `CsvParser` (UTF-8 BOM + strict UTF-8 + GBK fallback), `CsvMapping` (`--map` + identity), `ImportPlanner` (match + group + missing/duplicate policies), `ImportCommand` (orchestration + batched SetRequest).
- Reuses existing `/api/elements` and `/api/elements/set` — no addin changes.
- 38 new facts + 3 completions assertions; full CLI suite green.

## Test plan
- [ ] CI build-and-test passes.
- [ ] On Windows + Revit 2026: import a UTF-8 doors.csv with 3 rows / 2 unique values → confirm "Modified 3 element-parameter pair(s)" and verify in `revitcli query doors`.
- [ ] Re-save same CSV as GBK from Excel; re-run; confirm `encoding=gbk` line and identical outcome.
- [ ] `revitcli import doors.csv --dry-run` → no SetRequest issued, preview lists groups.

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

### Step 4: Report PR URL

- [ ] Print the URL returned by `gh pr create`.

---

## Self-review checklist (run after writing the plan, before handing off)

**Spec coverage:**
- ✅ `revitcli import FILE.csv` command — Task 4–5.
- ✅ `--category`, `--match-by` required — Task 4 (Create flags + ExecuteAsync validation).
- ✅ `--map` — Task 2 (`CsvMapping`) + Task 4 (flag pass-through).
- ✅ `--dry-run` — Task 4 (ExecuteAsync branch).
- ✅ `--on-missing error|warn|skip` — Task 3 (planner) + Task 4 (HasFatalPlanIssue).
- ✅ `--on-duplicate error|first|all` — Task 3 + Task 4.
- ✅ `--encoding utf-8|gbk|auto` (BOM → UTF-8 → GBK) — Task 1.
- ✅ `--batch-size N` (default 100) — Task 4 (ApplyPlan chunking).
- ✅ Exit codes 0 / 1 / 2 — Task 4 (ExecuteAsync return values).
- ✅ Reuses `set` endpoint — Task 4 (`SetParameterAsync`).
- ✅ Chinese params (UTF-8 + GBK roundtrip) — Task 1 + Task 4 tests.
- ✅ Catalog registration — Task 5.
- ✅ Completions — Task 6.
- ✅ CHANGELOG + version — Task 7.
- ✅ E2E verification list — Task 7 CHANGELOG body.

**Placeholder scan:** No "TBD", no "TODO", no "implement later", no "similar to Task N" — every task contains the exact code or commands. The completion code in Task 6 deliberately says "mirror existing pattern" because the exact existing structure is what to copy; that is concrete, not a placeholder.

**Type consistency:**
- `CsvData { Encoding, EncodingName, Headers, Rows }` defined Task 1 → consumed Task 3 (`Plan(csv, ...)`) and Task 4 (`csv.EncodingName`).
- `ImportPlan { Groups, Misses, Duplicates, Skipped, Warnings }` defined Task 3 → consumed Task 4.
- `ImportGroup { Param, Value, ElementIds, Sources }` defined Task 3 → consumed Task 4 (`group.Param`, `group.Value`, `group.ElementIds`).
- `ImportSkip { RowNumber, MatchByValue, Param, Reason }` defined Task 3 → consumed Task 4 (`Skipped` count printed).
- `CsvMapping.Build(rawMap, headers, matchBy)` signature consistent across Tasks 2 / 4 (`CsvMapping.Build(rawMap, csv.Headers, matchBy)`).
- `ImportPlanner.Plan(csv, elements, mapping, matchBy, onMissing, onDuplicate)` signature consistent Tasks 3 / 4.
- `SetRequest`/`SetResult` types are existing in `shared/`; usage in Task 4 matches `SetCommand.cs` shape (ElementIds + Param + Value + DryRun=false).

Plan complete.

---

## Execution Handoff

Plan saved to `docs/superpowers/plans/2026-04-24-import-csv.md`. Two execution options:

1. **Subagent-Driven (recommended)** — dispatch a fresh subagent per task, two-stage review between tasks.
2. **Inline Execution** — execute tasks here using `superpowers:executing-plans`, batch with checkpoints.

Which approach?
