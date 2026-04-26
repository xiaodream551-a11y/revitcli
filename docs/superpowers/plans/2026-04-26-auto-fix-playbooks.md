# Auto-fix Playbooks Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the v1.5 parameter-only auto-fix loop: `check -> fix --dry-run -> fix --apply -> check -> rollback`.

**Architecture:** Keep `check` read-only, add `fix` and `rollback` as top-level write-intent commands, and reuse `/api/elements/set` for all model writes. Extend `AuditIssue` with nullable structured metadata, add a CLI-side planner with explicit recipe matching and guarded inference, write a snapshot plus fix journal before apply, and use the journal to rollback only touched parameters.

**Tech Stack:** C#/.NET 8 CLI, Revit Add-in multi-targeting (`net48`, `net8.0-windows`), shared DTOs in `shared/RevitCli.Shared`, xUnit, YamlDotNet, PowerShell 5.1, Autodesk Revit 2026 API DLLs from `D:\revit2026\Revit 2026`.

---

## Source Documents

- Spec: `docs/superpowers/specs/2026-04-26-auto-fix-playbooks-design.md`
- Roadmap: `docs/roadmap-2026q2-q3.md` section `v1.5 - Auto-fix Playbooks`
- Existing command patterns:
  - `src/RevitCli/Commands/CheckCommand.cs`
  - `src/RevitCli/Commands/SetCommand.cs`
  - `src/RevitCli/Commands/SnapshotCommand.cs`
  - `src/RevitCli/Commands/CliCommandCatalog.cs`
- Existing profile model:
  - `src/RevitCli/Profile/ProjectProfile.cs`
  - `src/RevitCli/Profile/ProfileLoader.cs`

## PR Breakdown

- PR 1: Shared/profile schema and reusable check runner.
- PR 2: Pure CLI fix planner, recipe matcher, template renderer, and strategy tests.
- PR 3: `fix` command dry-run/apply safety gates, snapshot, journal, registration, completions, interactive help.
- PR 4: `rollback` command and journal-scoped reverse writes.
- PR 5: Add-in structured `AuditIssue` metadata for required parameter, naming, and selected audit rules.
- PR 6: Starter profile examples, README/CHANGELOG, and Revit 2026 smoke extension.

Each PR must compile, run the targeted tests listed in its tasks, and end with a scoped Conventional Commit.

## File Structure

### Shared DTO

- Modify `shared/RevitCli.Shared/AuditResult.cs`
  - Add nullable `Category`, `Parameter`, `Target`, `CurrentValue`, `ExpectedValue`, and `Source` fields to `AuditIssue`.

### Profile

- Modify `src/RevitCli/Profile/ProjectProfile.cs`
  - Add `List<FixRecipe> Fixes`.
  - Add `FixRecipe` class.
- Modify `src/RevitCli/Profile/ProfileLoader.cs`
  - Validate `fixes` recipe shape.
  - Merge inherited `fixes` by appending base then child.
- Create `tests/RevitCli.Tests/Profile/FixRecipeProfileTests.cs`
  - Profile schema and validation tests.

### Check Runner

- Create `src/RevitCli/Checks/CheckRunner.cs`
  - Load profile, find check set, execute audit request, apply suppressions, compute display counts.
- Create `src/RevitCli/Checks/CheckRunResult.cs`
  - Data object returned by `CheckRunner`.
- Modify `src/RevitCli/Commands/CheckCommand.cs`
  - Delegate profile/audit/suppression logic to `CheckRunner`.
  - Keep rendering, history save, notification, and exit-code policy in the command.
- Create `tests/RevitCli.Tests/Checks/CheckRunnerTests.cs`
  - Runner behavior tests independent of command rendering.

### Fix Core

- Create `src/RevitCli/Fix/FixAction.cs`
- Create `src/RevitCli/Fix/FixPlan.cs`
- Create `src/RevitCli/Fix/FixSkippedIssue.cs`
- Create `src/RevitCli/Fix/FixPlanOptions.cs`
- Create `src/RevitCli/Fix/FixRecipeMatcher.cs`
- Create `src/RevitCli/Fix/FixTemplateRenderer.cs`
- Create `src/RevitCli/Fix/FixPlanner.cs`
- Create `src/RevitCli/Fix/FixPlanSafety.cs`
- Create `src/RevitCli/Fix/FixPlanRenderer.cs`
- Create `src/RevitCli/Fix/FixJournal.cs`
- Create `src/RevitCli/Fix/FixJournalStore.cs`
- Create `src/RevitCli/Fix/Strategies/IFixStrategy.cs`
- Create `src/RevitCli/Fix/Strategies/SetParamStrategy.cs`
- Create `src/RevitCli/Fix/Strategies/RenameByPatternStrategy.cs`
- Create tests:
  - `tests/RevitCli.Tests/Fix/FixRecipeMatcherTests.cs`
  - `tests/RevitCli.Tests/Fix/FixTemplateRendererTests.cs`
  - `tests/RevitCli.Tests/Fix/SetParamStrategyTests.cs`
  - `tests/RevitCli.Tests/Fix/RenameByPatternStrategyTests.cs`
  - `tests/RevitCli.Tests/Fix/FixPlannerTests.cs`
  - `tests/RevitCli.Tests/Fix/FixPlanSafetyTests.cs`
  - `tests/RevitCli.Tests/Fix/FixJournalStoreTests.cs`

### Commands

- Create `src/RevitCli/Commands/FixCommand.cs`
  - Run check runner, build plan, render dry-run, enforce apply safety gates, snapshot, journal, set calls.
- Create `src/RevitCli/Commands/RollbackCommand.cs`
  - Load baseline and journal, plan reverse writes, detect conflicts, apply via set calls.
- Modify `src/RevitCli/Commands/CliCommandCatalog.cs`
  - Register `fix` and `rollback`.
  - Add command names to top-level and interactive help lists.
- Modify `src/RevitCli/Commands/CompletionsCommand.cs`
  - Include `fix` and `rollback` in completion output.
- Create tests:
  - `tests/RevitCli.Tests/Commands/FixCommandTests.cs`
  - `tests/RevitCli.Tests/Commands/RollbackCommandTests.cs`
  - Extend `tests/RevitCli.Tests/Commands/CliCommandCatalogTests.cs`
  - Extend completions tests if present; otherwise test through `CompletionsCommand.ExecuteAsync`.

### Add-in

- Modify `src/RevitCli.Addin/Services/RealRevitOperations.cs`
  - Populate structured `AuditIssue` metadata where the Add-in already knows category, parameter, and value.
- Modify `src/RevitCli.Addin/Services/PlaceholderRevitOperations.cs`
  - Keep placeholder audit issues explicit and structured for protocol tests.
- Modify `tests/RevitCli.Addin.Tests/Bridge/RevitBridgeTests.cs` or create `tests/RevitCli.Addin.Tests/Services/AuditIssueMetadataTests.cs`
  - Verify metadata on operations that can be tested without a live model.

### Docs and Smoke

- Modify `profiles/architectural-issue.yml`
- Modify `profiles/interior-room-data.yml`
- Modify `profiles/general-publish.yml`
  - Add commented `fixes` recipe examples only. Do not silently enable broad writes.
- Modify `README.md`
  - Add `fix` and `rollback` to command table and a short v1.5 usage sample.
- Modify `CHANGELOG.md`
  - Add v1.5 entry.
- Modify `scripts/smoke-revit2026.ps1`
  - Add optional fix dry-run/apply/rollback smoke sequence guarded by explicit parameters.

## Test Matrix

| Area | Test fact |
|---|---|
| DTO | `AuditIssue` JSON includes structured fields when non-null. |
| DTO | `AuditIssue` JSON omits nullable structured fields when null. |
| Profile | Profile without `fixes` still loads. |
| Profile | Valid `setParam` recipe loads. |
| Profile | Valid `renameByPattern` recipe loads. |
| Profile | Missing strategy throws `InvalidOperationException`. |
| Profile | Unsupported strategy throws `InvalidOperationException`. |
| Profile | Missing `setParam.value` throws. |
| Profile | Missing `renameByPattern.match` throws. |
| Profile | Invalid regex throws with recipe index. |
| Profile | Inherited profile appends base fixes then child fixes. |
| Check runner | Unknown check set reports available names. |
| Check runner | Suppressions are applied before `fix` planning. |
| Check runner | Audit API failure returns failure without renderer side effects. |
| Matcher | `rule + category + parameter` wins over broader matches. |
| Matcher | Same-priority duplicate recipes fail as ambiguous. |
| Template | `{element.id}`, `{category}`, `{parameter}`, `{currentValue}`, `{expectedValue}` render. |
| Template | Unknown token fails planning rather than silently writing literal garbage. |
| `setParam` | Missing element id is skipped. |
| `setParam` | Empty rendered value is skipped. |
| `renameByPattern` | Non-matching current value is skipped. |
| `renameByPattern` | Unchanged replacement is skipped. |
| Planner | Issue with explicit recipe creates high-confidence action. |
| Planner | Issue without recipe but structured data creates medium-confidence inferred action. |
| Planner | Message fallback creates low-confidence dry-run-only action. |
| Safety | Inferred action cannot apply without `--allow-inferred`. |
| Safety | Low-confidence action cannot apply even with `--allow-inferred`. |
| Safety | `--max-changes` blocks before snapshot. |
| Fix command | `fix --dry-run` prints plan and does not call set. |
| Fix command | `fix --apply --yes` writes snapshot and journal before set. |
| Fix command | Snapshot failure prevents set calls. |
| Fix command | Journal failure prevents set calls. |
| Fix command | Set failure prints rollback guidance. |
| Rollback | Missing baseline exits `1`. |
| Rollback | Missing journal exits `1`. |
| Rollback | Dry-run prints reverse actions. |
| Rollback | Apply writes old values through set endpoint. |
| Rollback | Current value mismatch is conflict and is not overwritten. |
| Add-in | Required parameter issues include category, parameter, current value, and `source=structured`. |
| Add-in | Naming issues include target/category, current value, and `source=structured`. |
| Smoke | Revit 2026 sequence records check, fix dry-run, fix apply, rollback, and final check. |

## Task 1: Shared AuditIssue Metadata

**Files:**
- Modify: `shared/RevitCli.Shared/AuditResult.cs`
- Create: `tests/RevitCli.Tests/Shared/AuditIssueMetadataTests.cs`

- [ ] **Step 1: Write failing serialization tests**

Create `tests/RevitCli.Tests/Shared/AuditIssueMetadataTests.cs`:

```csharp
using System.Text.Json;
using RevitCli.Shared;
using Xunit;

namespace RevitCli.Tests.Shared;

public class AuditIssueMetadataTests
{
    [Fact]
    public void AuditIssue_SerializesStructuredMetadata_WhenPresent()
    {
        var issue = new AuditIssue
        {
            Rule = "required-parameter",
            Severity = "warning",
            Message = "Door Mark is missing",
            ElementId = 123,
            Category = "doors",
            Parameter = "Mark",
            Target = "doors",
            CurrentValue = "",
            ExpectedValue = "D-123",
            Source = "structured"
        };

        var json = JsonSerializer.Serialize(issue);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("doors", root.GetProperty("category").GetString());
        Assert.Equal("Mark", root.GetProperty("parameter").GetString());
        Assert.Equal("doors", root.GetProperty("target").GetString());
        Assert.Equal("", root.GetProperty("currentValue").GetString());
        Assert.Equal("D-123", root.GetProperty("expectedValue").GetString());
        Assert.Equal("structured", root.GetProperty("source").GetString());
    }

    [Fact]
    public void AuditIssue_ExistingFieldsStillRoundTrip_WhenMetadataMissing()
    {
        var json = """
        {
          "rule": "naming",
          "severity": "warning",
          "message": "Bad name",
          "elementId": 42
        }
        """;

        var issue = JsonSerializer.Deserialize<AuditIssue>(json)!;

        Assert.Equal("naming", issue.Rule);
        Assert.Equal("warning", issue.Severity);
        Assert.Equal("Bad name", issue.Message);
        Assert.Equal(42, issue.ElementId);
        Assert.Null(issue.Category);
        Assert.Null(issue.Parameter);
        Assert.Null(issue.Target);
        Assert.Null(issue.CurrentValue);
        Assert.Null(issue.ExpectedValue);
        Assert.Null(issue.Source);
    }
}
```

- [ ] **Step 2: Run tests and verify they fail**

Run:

```powershell
dotnet test tests\RevitCli.Tests\RevitCli.Tests.csproj --filter FullyQualifiedName~AuditIssueMetadataTests --verbosity minimal
```

Expected: compile failure because `AuditIssue.Category`, `Parameter`, `Target`, `CurrentValue`, `ExpectedValue`, and `Source` do not exist.

- [ ] **Step 3: Add nullable structured fields**

Modify `shared/RevitCli.Shared/AuditResult.cs` so `AuditIssue` is:

```csharp
public class AuditIssue
{
    [JsonPropertyName("rule")]
    public string Rule { get; set; } = "";

    [JsonPropertyName("severity")]
    public string Severity { get; set; } = "";  // "error", "warning", "info"

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("elementId")]
    public long? ElementId { get; set; }

    [JsonPropertyName("category")]
    public string? Category { get; set; }

    [JsonPropertyName("parameter")]
    public string? Parameter { get; set; }

    [JsonPropertyName("target")]
    public string? Target { get; set; }

    [JsonPropertyName("currentValue")]
    public string? CurrentValue { get; set; }

    [JsonPropertyName("expectedValue")]
    public string? ExpectedValue { get; set; }

    [JsonPropertyName("source")]
    public string? Source { get; set; }
}
```

- [ ] **Step 4: Run DTO tests**

Run:

```powershell
dotnet test tests\RevitCli.Tests\RevitCli.Tests.csproj --filter FullyQualifiedName~AuditIssueMetadataTests --verbosity minimal
```

Expected: pass.

- [ ] **Step 5: Commit**

```powershell
git add shared/RevitCli.Shared/AuditResult.cs tests/RevitCli.Tests/Shared/AuditIssueMetadataTests.cs
git commit -m "feat: add structured audit issue metadata"
```

## Task 2: Profile Fix Recipe Schema

**Files:**
- Modify: `src/RevitCli/Profile/ProjectProfile.cs`
- Modify: `src/RevitCli/Profile/ProfileLoader.cs`
- Create: `tests/RevitCli.Tests/Profile/FixRecipeProfileTests.cs`

- [ ] **Step 1: Write failing profile schema tests**

Create `tests/RevitCli.Tests/Profile/FixRecipeProfileTests.cs`:

```csharp
using System;
using System.IO;
using RevitCli.Profile;
using Xunit;

namespace RevitCli.Tests.Profile;

public class FixRecipeProfileTests
{
    private static ProjectProfile LoadYaml(string yaml)
    {
        var path = Path.Combine(Path.GetTempPath(), $"revitcli_fix_profile_{Guid.NewGuid():N}.yml");
        File.WriteAllText(path, yaml);
        try { return ProfileLoader.Load(path); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Profile_WithoutFixes_Loads()
    {
        var profile = LoadYaml("""
version: 1
checks:
  default:
    failOn: error
""");

        Assert.Empty(profile.Fixes);
    }

    [Fact]
    public void Profile_SetParamRecipe_Loads()
    {
        var profile = LoadYaml("""
version: 1
fixes:
  - rule: required-parameter
    category: doors
    parameter: Mark
    strategy: setParam
    value: "{category}-{element.id}"
    maxChanges: 20
""");

        var recipe = Assert.Single(profile.Fixes);
        Assert.Equal("required-parameter", recipe.Rule);
        Assert.Equal("doors", recipe.Category);
        Assert.Equal("Mark", recipe.Parameter);
        Assert.Equal("setParam", recipe.Strategy);
        Assert.Equal("{category}-{element.id}", recipe.Value);
        Assert.Equal(20, recipe.MaxChanges);
    }

    [Fact]
    public void Profile_RenameByPatternRecipe_Loads()
    {
        var profile = LoadYaml("""
version: 1
fixes:
  - rule: naming
    category: rooms
    parameter: Name
    strategy: renameByPattern
    match: "^Room (.+)$"
    replace: "$1"
""");

        var recipe = Assert.Single(profile.Fixes);
        Assert.Equal("renameByPattern", recipe.Strategy);
        Assert.Equal("^Room (.+)$", recipe.Match);
        Assert.Equal("$1", recipe.Replace);
    }

    [Fact]
    public void Profile_MissingStrategy_Fails()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => LoadYaml("""
version: 1
fixes:
  - rule: naming
    parameter: Name
"""));

        Assert.Contains("fixes[0].strategy", ex.Message);
    }

    [Fact]
    public void Profile_UnsupportedStrategy_Fails()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => LoadYaml("""
version: 1
fixes:
  - rule: unplaced-rooms
    strategy: purgeUnplaced
"""));

        Assert.Contains("supported", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Profile_SetParamMissingValue_Fails()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => LoadYaml("""
version: 1
fixes:
  - rule: required-parameter
    parameter: Mark
    strategy: setParam
"""));

        Assert.Contains("value", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Profile_RenameInvalidRegex_Fails()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => LoadYaml("""
version: 1
fixes:
  - rule: naming
    parameter: Name
    strategy: renameByPattern
    match: "["
    replace: "$1"
"""));

        Assert.Contains("fixes[0].match", ex.Message);
    }
}
```

- [ ] **Step 2: Run tests and verify they fail**

Run:

```powershell
dotnet test tests\RevitCli.Tests\RevitCli.Tests.csproj --filter FullyQualifiedName~FixRecipeProfileTests --verbosity minimal
```

Expected: compile failure because `ProjectProfile.Fixes` and `FixRecipe` do not exist.

- [ ] **Step 3: Add `FixRecipe` model**

Modify `src/RevitCli/Profile/ProjectProfile.cs`:

```csharp
[YamlMember(Alias = "fixes")]
public List<FixRecipe> Fixes { get; set; } = new();
```

Add this class near other profile DTO classes:

```csharp
public class FixRecipe
{
    [YamlMember(Alias = "rule")]
    public string? Rule { get; set; }

    [YamlMember(Alias = "category")]
    public string? Category { get; set; }

    [YamlMember(Alias = "parameter")]
    public string? Parameter { get; set; }

    [YamlMember(Alias = "strategy")]
    public string Strategy { get; set; } = "";

    [YamlMember(Alias = "value")]
    public string? Value { get; set; }

    [YamlMember(Alias = "match")]
    public string? Match { get; set; }

    [YamlMember(Alias = "replace")]
    public string? Replace { get; set; }

    [YamlMember(Alias = "maxChanges")]
    public int? MaxChanges { get; set; }
}
```

- [ ] **Step 4: Add validation**

Modify `src/RevitCli/Profile/ProfileLoader.cs`:

```csharp
private static readonly HashSet<string> ValidFixStrategies = new(StringComparer.OrdinalIgnoreCase)
    { "setParam", "renameByPattern" };
```

Add this call at the end of `ValidateProfile`:

```csharp
ValidateFixes(profile, path);
```

Add this method:

```csharp
private static void ValidateFixes(ProjectProfile profile, string path)
{
    for (var i = 0; i < profile.Fixes.Count; i++)
    {
        var fix = profile.Fixes[i];
        var prefix = $"Profile {path}: fixes[{i}]";

        if (string.IsNullOrWhiteSpace(fix.Strategy))
            throw new InvalidOperationException($"{prefix}.strategy is required");

        if (!ValidFixStrategies.Contains(fix.Strategy))
            throw new InvalidOperationException(
                $"{prefix}.strategy '{fix.Strategy}' is not supported. Supported strategies: setParam, renameByPattern");

        if (fix.MaxChanges.HasValue && fix.MaxChanges.Value <= 0)
            throw new InvalidOperationException($"{prefix}.maxChanges must be greater than 0");

        if (string.Equals(fix.Strategy, "setParam", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(fix.Parameter))
                throw new InvalidOperationException($"{prefix}.parameter is required for setParam");
            if (fix.Value == null)
                throw new InvalidOperationException($"{prefix}.value is required for setParam");
        }

        if (string.Equals(fix.Strategy, "renameByPattern", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(fix.Parameter))
                throw new InvalidOperationException($"{prefix}.parameter is required for renameByPattern");
            if (string.IsNullOrWhiteSpace(fix.Match))
                throw new InvalidOperationException($"{prefix}.match is required for renameByPattern");
            if (fix.Replace == null)
                throw new InvalidOperationException($"{prefix}.replace is required for renameByPattern");
            try
            {
                _ = new System.Text.RegularExpressions.Regex(fix.Match);
            }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException($"{prefix}.match is invalid regex: {ex.Message}", ex);
            }
        }
    }
}
```

- [ ] **Step 5: Merge inherited fixes**

Modify `Merge` in `src/RevitCli/Profile/ProfileLoader.cs` to include:

```csharp
merged.Fixes.AddRange(baseProfile.Fixes);
merged.Fixes.AddRange(child.Fixes);
```

- [ ] **Step 6: Run profile tests**

Run:

```powershell
dotnet test tests\RevitCli.Tests\RevitCli.Tests.csproj --filter FullyQualifiedName~FixRecipeProfileTests --verbosity minimal
```

Expected: pass.

- [ ] **Step 7: Run existing profile/check tests**

Run:

```powershell
dotnet test tests\RevitCli.Tests\RevitCli.Tests.csproj --filter "FullyQualifiedName~Profile|FullyQualifiedName~CheckCommandTests" --verbosity minimal
```

Expected: pass.

- [ ] **Step 8: Commit**

```powershell
git add src/RevitCli/Profile/ProjectProfile.cs src/RevitCli/Profile/ProfileLoader.cs tests/RevitCli.Tests/Profile/FixRecipeProfileTests.cs
git commit -m "feat: add fix recipe profile schema"
```

## Task 3: Reusable Check Runner

**Files:**
- Create: `src/RevitCli/Checks/CheckRunResult.cs`
- Create: `src/RevitCli/Checks/CheckRunner.cs`
- Modify: `src/RevitCli/Commands/CheckCommand.cs`
- Create: `tests/RevitCli.Tests/Checks/CheckRunnerTests.cs`

- [ ] **Step 1: Write failing check runner tests**

Create `tests/RevitCli.Tests/Checks/CheckRunnerTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using RevitCli.Checks;
using RevitCli.Client;
using RevitCli.Shared;
using RevitCli.Tests.Client;
using Xunit;

namespace RevitCli.Tests.Checks;

public class CheckRunnerTests
{
    private static RevitClient ClientFor(AuditResult auditResult)
    {
        var response = ApiResponse<AuditResult>.Ok(auditResult);
        var handler = new FakeHttpHandler(JsonSerializer.Serialize(response));
        return new RevitClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:17839") });
    }

    private static string WriteProfile(string yaml)
    {
        var path = Path.Combine(Path.GetTempPath(), $"revitcli_check_runner_{Guid.NewGuid():N}.yml");
        File.WriteAllText(path, yaml);
        return path;
    }

    [Fact]
    public async Task RunAsync_AppliesSuppressionsBeforeReturningIssues()
    {
        var profilePath = WriteProfile("""
version: 1
checks:
  default:
    failOn: error
    auditRules:
      - rule: room-bounds
    suppressions:
      - rule: room-bounds
        elementIds: [100]
        reason: accepted
""");
        try
        {
            var client = ClientFor(new AuditResult
            {
                Passed = 0,
                Failed = 2,
                Issues = new List<AuditIssue>
                {
                    new() { Rule = "room-bounds", Severity = "error", Message = "A", ElementId = 100 },
                    new() { Rule = "room-bounds", Severity = "error", Message = "B", ElementId = 200 }
                }
            });

            var result = await CheckRunner.RunAsync(client, "default", profilePath);

            Assert.True(result.Success);
            Assert.Equal(1, result.Data!.SuppressedCount);
            var issue = Assert.Single(result.Data.Issues);
            Assert.Equal(200, issue.ElementId);
        }
        finally
        {
            File.Delete(profilePath);
        }
    }

    [Fact]
    public async Task RunAsync_UnknownCheckSet_ReturnsFailureWithAvailableNames()
    {
        var profilePath = WriteProfile("""
version: 1
checks:
  model-health:
    failOn: error
""");
        try
        {
            var result = await CheckRunner.RunAsync(ClientFor(new AuditResult()), "missing", profilePath);

            Assert.False(result.Success);
            Assert.Contains("model-health", result.Error);
        }
        finally
        {
            File.Delete(profilePath);
        }
    }
}
```

- [ ] **Step 2: Run tests and verify they fail**

Run:

```powershell
dotnet test tests\RevitCli.Tests\RevitCli.Tests.csproj --filter FullyQualifiedName~CheckRunnerTests --verbosity minimal
```

Expected: compile failure because `RevitCli.Checks.CheckRunner` does not exist.

- [ ] **Step 3: Add result DTO**

Create `src/RevitCli/Checks/CheckRunResult.cs`:

```csharp
using System.Collections.Generic;
using RevitCli.Profile;
using RevitCli.Shared;

namespace RevitCli.Checks;

internal sealed class CheckRunResult
{
    public ProjectProfile Profile { get; init; } = new();
    public CheckDefinition CheckDefinition { get; init; } = new();
    public string CheckName { get; init; } = "default";
    public string? ProfilePath { get; init; }
    public List<AuditIssue> Issues { get; init; } = new();
    public int SuppressedCount { get; init; }
    public int DisplayPassed { get; init; }
    public int DisplayFailed { get; init; }
}
```

- [ ] **Step 4: Add runner**

Create `src/RevitCli/Checks/CheckRunner.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using RevitCli.Client;
using RevitCli.Profile;
using RevitCli.Shared;

namespace RevitCli.Checks;

internal static class CheckRunner
{
    public static async Task<CheckRunnerResponse> RunAsync(
        RevitClient client,
        string? name,
        string? profilePath)
    {
        ProjectProfile? profile;
        string? resolvedProfilePath = profilePath;

        try
        {
            if (profilePath != null)
            {
                profile = ProfileLoader.Load(profilePath);
                resolvedProfilePath = Path.GetFullPath(profilePath);
            }
            else
            {
                resolvedProfilePath = ProfileLoader.Discover();
                profile = resolvedProfilePath == null ? null : ProfileLoader.Load(resolvedProfilePath);
            }
        }
        catch (Exception ex)
        {
            return CheckRunnerResponse.Fail($"Error loading profile: {ex.Message}");
        }

        if (profile == null)
        {
            return CheckRunnerResponse.Fail(
                $"Error: no {ProfileLoader.FileName} found.{Environment.NewLine}" +
                "  Create one in your project root, or copy from .revitcli.example.yml");
        }

        var checkName = name ?? "default";
        if (!profile.Checks.TryGetValue(checkName, out var checkDef))
        {
            var message = $"Error: check set '{checkName}' not found in profile.";
            if (profile.Checks.Count > 0)
                message += $"{Environment.NewLine}  Available check sets: {string.Join(", ", profile.Checks.Keys)}";
            else
                message += $"{Environment.NewLine}  Your profile has no check sets defined. Add a 'checks:' section.";
            return CheckRunnerResponse.Fail(message);
        }

        var request = new AuditRequest
        {
            Rules = checkDef.AuditRules.Select(r => r.Rule).ToList(),
            RequiredParameters = checkDef.RequiredParameters.Select(r => new RequiredParameterSpec
            {
                Category = r.Category,
                Parameter = r.Parameter,
                RequireNonEmpty = r.RequireNonEmpty,
                Severity = r.Severity
            }).ToList(),
            NamingPatterns = checkDef.Naming.Select(n => new NamingPatternSpec
            {
                Target = n.Target,
                Pattern = n.Pattern,
                Severity = n.Severity
            }).ToList()
        };

        var result = await client.AuditAsync(request);
        if (!result.Success)
            return CheckRunnerResponse.Fail($"Error: {result.Error}");

        var allIssues = result.Data!.Issues.ToList();
        var suppressedCount = 0;
        if (checkDef.Suppressions.Count > 0)
        {
            var activeSuppressions = checkDef.Suppressions
                .Where(s => !CheckSuppressionRules.IsExpired(s.Expires))
                .ToList();

            var filtered = new List<AuditIssue>();
            foreach (var issue in allIssues)
            {
                if (CheckSuppressionRules.IsSuppressed(issue, activeSuppressions))
                    suppressedCount++;
                else
                    filtered.Add(issue);
            }
            allIssues = filtered;
        }

        var displayFailed = allIssues.Count(i => i.Severity is "error" or "warning");
        var displayPassed = Math.Max(0, result.Data.Passed + result.Data.Failed - displayFailed);

        return CheckRunnerResponse.Ok(new CheckRunResult
        {
            Profile = profile,
            CheckDefinition = checkDef,
            CheckName = checkName,
            ProfilePath = resolvedProfilePath,
            Issues = allIssues,
            SuppressedCount = suppressedCount,
            DisplayPassed = displayPassed,
            DisplayFailed = displayFailed
        });
    }
}

internal sealed class CheckRunnerResponse
{
    public bool Success { get; init; }
    public CheckRunResult? Data { get; init; }
    public string Error { get; init; } = "";

    public static CheckRunnerResponse Ok(CheckRunResult data) => new() { Success = true, Data = data };
    public static CheckRunnerResponse Fail(string error) => new() { Success = false, Error = error };
}
```

- [ ] **Step 5: Extract suppression helpers**

In `src/RevitCli/Commands/CheckCommand.cs`, move the current private suppression methods into a new internal static helper in the same file or a new file `src/RevitCli/Checks/CheckSuppressionRules.cs`.

If using a new file, create:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using RevitCli.Profile;
using RevitCli.Shared;

namespace RevitCli.Checks;

internal static class CheckSuppressionRules
{
    public static bool IsSuppressed(AuditIssue issue, List<Suppression> suppressions)
    {
        foreach (var s in suppressions)
        {
            if (!string.Equals(s.Rule, issue.Rule, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!string.IsNullOrEmpty(s.Category) && !ContainsWord(issue.Message, s.Category))
                continue;

            if (!string.IsNullOrEmpty(s.Parameter) && !ContainsWord(issue.Message, s.Parameter))
                continue;

            if (s.ElementIds != null && s.ElementIds.Count > 0)
            {
                if (issue.ElementId.HasValue && s.ElementIds.Contains(issue.ElementId.Value))
                    return true;
                continue;
            }

            return true;
        }

        return false;
    }

    public static bool IsExpired(string? expires)
    {
        if (string.IsNullOrWhiteSpace(expires))
            return false;

        return DateTime.TryParse(expires, out var date) && date.Date < DateTime.Today;
    }

    private static bool ContainsWord(string text, string word)
    {
        return Regex.IsMatch(text, $@"\b{Regex.Escape(word)}\b", RegexOptions.IgnoreCase);
    }
}
```

- [ ] **Step 6: Wire `CheckCommand` to `CheckRunner`**

Modify `CheckCommand.ExecuteAsync` to call:

```csharp
var run = await CheckRunner.RunAsync(client, name, profilePath);
if (!run.Success)
{
    await output.WriteLineAsync(run.Error);
    if (run.Error.Contains("not running", StringComparison.OrdinalIgnoreCase))
        await output.WriteLineAsync("  Run 'revitcli doctor' to diagnose connection issues.");
    return 1;
}

var checkName = run.Data!.CheckName;
var profile = run.Data.Profile;
var checkDef = run.Data.CheckDefinition;
var allIssues = run.Data.Issues;
var suppressedCount = run.Data.SuppressedCount;
var displayPassed = run.Data.DisplayPassed;
var displayFailed = run.Data.DisplayFailed;
```

Keep existing rendering, report save, history save, failOn exit-code policy, and webhook notification after those variables are set.

- [ ] **Step 7: Run runner and command tests**

Run:

```powershell
dotnet test tests\RevitCli.Tests\RevitCli.Tests.csproj --filter "FullyQualifiedName~CheckRunnerTests|FullyQualifiedName~CheckCommandTests" --verbosity minimal
```

Expected: pass.

- [ ] **Step 8: Commit**

```powershell
git add src/RevitCli/Checks src/RevitCli/Commands/CheckCommand.cs tests/RevitCli.Tests/Checks/CheckRunnerTests.cs
git commit -m "refactor: extract reusable check runner"
```

## Task 4: Fix Core Models, Matcher, and Template Renderer

**Files:**
- Create: `src/RevitCli/Fix/FixAction.cs`
- Create: `src/RevitCli/Fix/FixPlan.cs`
- Create: `src/RevitCli/Fix/FixSkippedIssue.cs`
- Create: `src/RevitCli/Fix/FixPlanOptions.cs`
- Create: `src/RevitCli/Fix/FixRecipeMatcher.cs`
- Create: `src/RevitCli/Fix/FixTemplateRenderer.cs`
- Create: `tests/RevitCli.Tests/Fix/FixRecipeMatcherTests.cs`
- Create: `tests/RevitCli.Tests/Fix/FixTemplateRendererTests.cs`

- [ ] **Step 1: Write failing matcher tests**

Create `tests/RevitCli.Tests/Fix/FixRecipeMatcherTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using RevitCli.Fix;
using RevitCli.Profile;
using RevitCli.Shared;
using Xunit;

namespace RevitCli.Tests.Fix;

public class FixRecipeMatcherTests
{
    [Fact]
    public void Match_PrefersRuleCategoryParameter()
    {
        var issue = new AuditIssue
        {
            Rule = "required-parameter",
            Category = "doors",
            Parameter = "Mark",
            ElementId = 10
        };
        var recipes = new List<FixRecipe>
        {
            new() { Rule = "required-parameter", Strategy = "setParam", Parameter = "Comments", Value = "broad" },
            new() { Rule = "required-parameter", Category = "doors", Strategy = "setParam", Parameter = "Mark", Value = "category" },
            new() { Rule = "required-parameter", Category = "doors", Parameter = "Mark", Strategy = "setParam", Value = "exact" }
        };

        var match = FixRecipeMatcher.Match(issue, recipes);

        Assert.True(match.Success);
        Assert.Equal("exact", match.Recipe!.Value);
        Assert.False(match.Inferred);
    }

    [Fact]
    public void Match_DuplicateSamePriority_ReturnsAmbiguousFailure()
    {
        var issue = new AuditIssue { Rule = "naming", Category = "rooms", Parameter = "Name", ElementId = 20 };
        var recipes = new List<FixRecipe>
        {
            new() { Rule = "naming", Category = "rooms", Parameter = "Name", Strategy = "renameByPattern", Match = "^A$", Replace = "B" },
            new() { Rule = "naming", Category = "rooms", Parameter = "Name", Strategy = "renameByPattern", Match = "^C$", Replace = "D" }
        };

        var match = FixRecipeMatcher.Match(issue, recipes);

        Assert.False(match.Success);
        Assert.Contains("ambiguous", match.Error, StringComparison.OrdinalIgnoreCase);
    }
}
```

- [ ] **Step 2: Write failing template tests**

Create `tests/RevitCli.Tests/Fix/FixTemplateRendererTests.cs`:

```csharp
using System;
using RevitCli.Fix;
using RevitCli.Shared;
using Xunit;

namespace RevitCli.Tests.Fix;

public class FixTemplateRendererTests
{
    [Fact]
    public void Render_ReplacesSupportedTokens()
    {
        var issue = new AuditIssue
        {
            ElementId = 123,
            Category = "doors",
            Parameter = "Mark",
            CurrentValue = "",
            ExpectedValue = "D-123"
        };

        var rendered = FixTemplateRenderer.Render("{category}-{element.id}-{parameter}-{expectedValue}", issue, "Mark");

        Assert.Equal("doors-123-Mark-D-123", rendered);
    }

    [Fact]
    public void Render_UnknownToken_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            FixTemplateRenderer.Render("{unknown}", new AuditIssue { ElementId = 1 }, "Mark"));

        Assert.Contains("unknown", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
```

- [ ] **Step 3: Run tests and verify they fail**

Run:

```powershell
dotnet test tests\RevitCli.Tests\RevitCli.Tests.csproj --filter "FullyQualifiedName~FixRecipeMatcherTests|FullyQualifiedName~FixTemplateRendererTests" --verbosity minimal
```

Expected: compile failure because `RevitCli.Fix` classes do not exist.

- [ ] **Step 4: Add core model files**

Create `src/RevitCli/Fix/FixAction.cs`:

```csharp
namespace RevitCli.Fix;

internal sealed class FixAction
{
    public string Rule { get; init; } = "";
    public string Strategy { get; init; } = "";
    public long ElementId { get; init; }
    public string Category { get; init; } = "";
    public string Parameter { get; init; } = "";
    public string? OldValue { get; init; }
    public string NewValue { get; init; } = "";
    public bool Inferred { get; init; }
    public string Confidence { get; init; } = "high";
    public string Reason { get; init; } = "";
}
```

Create `src/RevitCli/Fix/FixSkippedIssue.cs`:

```csharp
namespace RevitCli.Fix;

internal sealed class FixSkippedIssue
{
    public string Rule { get; init; } = "";
    public string Severity { get; init; } = "";
    public long? ElementId { get; init; }
    public string Reason { get; init; } = "";
}
```

Create `src/RevitCli/Fix/FixPlan.cs`:

```csharp
using System.Collections.Generic;

namespace RevitCli.Fix;

internal sealed class FixPlan
{
    public string CheckName { get; init; } = "default";
    public List<FixAction> Actions { get; init; } = new();
    public List<FixSkippedIssue> Skipped { get; init; } = new();
    public List<string> Warnings { get; init; } = new();
}
```

Create `src/RevitCli/Fix/FixPlanOptions.cs`:

```csharp
using System.Collections.Generic;

namespace RevitCli.Fix;

internal sealed class FixPlanOptions
{
    public HashSet<string> Rules { get; init; } = new(System.StringComparer.OrdinalIgnoreCase);
    public string? Severity { get; init; }
}
```

- [ ] **Step 5: Add recipe matcher**

Create `src/RevitCli/Fix/FixRecipeMatcher.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using RevitCli.Profile;
using RevitCli.Shared;

namespace RevitCli.Fix;

internal static class FixRecipeMatcher
{
    public static FixRecipeMatch Match(AuditIssue issue, IReadOnlyList<FixRecipe> recipes)
    {
        var candidates = recipes
            .Select(recipe => new { Recipe = recipe, Priority = Priority(issue, recipe) })
            .Where(x => x.Priority > 0)
            .OrderByDescending(x => x.Priority)
            .ToList();

        if (candidates.Count == 0)
            return FixRecipeMatch.NoMatch();

        var bestPriority = candidates[0].Priority;
        var best = candidates.Where(x => x.Priority == bestPriority).ToList();
        if (best.Count > 1)
            return FixRecipeMatch.Fail($"Ambiguous fix recipes for rule '{issue.Rule}' at priority {bestPriority}");

        return FixRecipeMatch.Ok(best[0].Recipe, inferred: false);
    }

    private static int Priority(AuditIssue issue, FixRecipe recipe)
    {
        if (!Matches(recipe.Rule, issue.Rule))
            return 0;

        var categorySpecified = !string.IsNullOrWhiteSpace(recipe.Category);
        var parameterSpecified = !string.IsNullOrWhiteSpace(recipe.Parameter);
        var categoryMatches = !categorySpecified || Matches(recipe.Category, issue.Category);
        var parameterMatches = !parameterSpecified || Matches(recipe.Parameter, issue.Parameter);

        if (!categoryMatches || !parameterMatches)
            return 0;

        if (categorySpecified && parameterSpecified)
            return 5;
        if (categorySpecified)
            return 4;
        if (parameterSpecified)
            return 3;
        return 2;
    }

    private static bool Matches(string? expected, string? actual)
    {
        if (string.IsNullOrWhiteSpace(expected))
            return true;
        return string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class FixRecipeMatch
{
    public bool Success { get; init; }
    public bool HasRecipe => Recipe != null;
    public FixRecipe? Recipe { get; init; }
    public bool Inferred { get; init; }
    public string Error { get; init; } = "";

    public static FixRecipeMatch Ok(FixRecipe recipe, bool inferred) => new()
    {
        Success = true,
        Recipe = recipe,
        Inferred = inferred
    };

    public static FixRecipeMatch NoMatch() => new() { Success = true };
    public static FixRecipeMatch Fail(string error) => new() { Success = false, Error = error };
}
```

- [ ] **Step 6: Add template renderer**

Create `src/RevitCli/Fix/FixTemplateRenderer.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using RevitCli.Shared;

namespace RevitCli.Fix;

internal static class FixTemplateRenderer
{
    private static readonly Regex TokenPattern = new(@"\{(?<token>[^}]+)\}", RegexOptions.Compiled);

    public static string Render(string template, AuditIssue issue, string parameter)
    {
        return TokenPattern.Replace(template, match =>
        {
            var token = match.Groups["token"].Value;
            return token switch
            {
                "element.id" => issue.ElementId?.ToString() ?? "",
                "category" => issue.Category ?? "",
                "parameter" => parameter,
                "currentValue" => issue.CurrentValue ?? "",
                "expectedValue" => issue.ExpectedValue ?? "",
                _ => throw new InvalidOperationException($"Unknown fix template token '{token}'")
            };
        });
    }
}
```

- [ ] **Step 7: Run tests**

Run:

```powershell
dotnet test tests\RevitCli.Tests\RevitCli.Tests.csproj --filter "FullyQualifiedName~FixRecipeMatcherTests|FullyQualifiedName~FixTemplateRendererTests" --verbosity minimal
```

Expected: pass.

- [ ] **Step 8: Commit**

```powershell
git add src/RevitCli/Fix tests/RevitCli.Tests/Fix/FixRecipeMatcherTests.cs tests/RevitCli.Tests/Fix/FixTemplateRendererTests.cs
git commit -m "feat: add fix planner core models"
```

## Task 5: Fix Strategies and Planner

**Files:**
- Create: `src/RevitCli/Fix/Strategies/IFixStrategy.cs`
- Create: `src/RevitCli/Fix/Strategies/SetParamStrategy.cs`
- Create: `src/RevitCli/Fix/Strategies/RenameByPatternStrategy.cs`
- Create: `src/RevitCli/Fix/FixPlanner.cs`
- Create: `tests/RevitCli.Tests/Fix/SetParamStrategyTests.cs`
- Create: `tests/RevitCli.Tests/Fix/RenameByPatternStrategyTests.cs`
- Create: `tests/RevitCli.Tests/Fix/FixPlannerTests.cs`

- [ ] **Step 1: Write strategy tests**

Create `tests/RevitCli.Tests/Fix/SetParamStrategyTests.cs`:

```csharp
using RevitCli.Fix.Strategies;
using RevitCli.Profile;
using RevitCli.Shared;
using Xunit;

namespace RevitCli.Tests.Fix;

public class SetParamStrategyTests
{
    [Fact]
    public void Plan_CreatesHighConfidenceAction()
    {
        var strategy = new SetParamStrategy();
        var issue = new AuditIssue
        {
            Rule = "required-parameter",
            ElementId = 123,
            Category = "doors",
            Parameter = "Mark",
            CurrentValue = ""
        };
        var recipe = new FixRecipe { Strategy = "setParam", Parameter = "Mark", Value = "{category}-{element.id}" };

        var result = strategy.Plan(issue, recipe, inferred: false, confidence: "high");

        Assert.True(result.Success);
        var action = Assert.Single(result.Actions);
        Assert.Equal(123, action.ElementId);
        Assert.Equal("Mark", action.Parameter);
        Assert.Equal("doors-123", action.NewValue);
        Assert.False(action.Inferred);
        Assert.Equal("high", action.Confidence);
    }

    [Fact]
    public void Plan_MissingElementId_Skips()
    {
        var strategy = new SetParamStrategy();
        var result = strategy.Plan(
            new AuditIssue { Rule = "required-parameter", Parameter = "Mark" },
            new FixRecipe { Strategy = "setParam", Parameter = "Mark", Value = "X" },
            inferred: false,
            confidence: "high");

        Assert.False(result.Success);
        Assert.Contains("element id", result.Error.ToLowerInvariant());
    }
}
```

Create `tests/RevitCli.Tests/Fix/RenameByPatternStrategyTests.cs`:

```csharp
using RevitCli.Fix.Strategies;
using RevitCli.Profile;
using RevitCli.Shared;
using Xunit;

namespace RevitCli.Tests.Fix;

public class RenameByPatternStrategyTests
{
    [Fact]
    public void Plan_ReplacesCurrentValue()
    {
        var strategy = new RenameByPatternStrategy();
        var issue = new AuditIssue
        {
            Rule = "naming",
            ElementId = 20,
            Category = "rooms",
            Parameter = "Name",
            CurrentValue = "Room Lobby"
        };
        var recipe = new FixRecipe
        {
            Strategy = "renameByPattern",
            Parameter = "Name",
            Match = "^Room (.+)$",
            Replace = "$1"
        };

        var result = strategy.Plan(issue, recipe, inferred: false, confidence: "high");

        Assert.True(result.Success);
        var action = Assert.Single(result.Actions);
        Assert.Equal("Lobby", action.NewValue);
        Assert.Equal("Room Lobby", action.OldValue);
    }

    [Fact]
    public void Plan_NonMatchingValue_Skips()
    {
        var strategy = new RenameByPatternStrategy();
        var result = strategy.Plan(
            new AuditIssue { Rule = "naming", ElementId = 20, Parameter = "Name", CurrentValue = "Lobby" },
            new FixRecipe { Strategy = "renameByPattern", Parameter = "Name", Match = "^Room (.+)$", Replace = "$1" },
            inferred: false,
            confidence: "high");

        Assert.False(result.Success);
        Assert.Contains("does not match", result.Error);
    }
}
```

- [ ] **Step 2: Write planner tests**

Create `tests/RevitCli.Tests/Fix/FixPlannerTests.cs`:

```csharp
using System.Collections.Generic;
using RevitCli.Fix;
using RevitCli.Profile;
using RevitCli.Shared;
using Xunit;

namespace RevitCli.Tests.Fix;

public class FixPlannerTests
{
    [Fact]
    public void Plan_ExplicitRecipe_CreatesAction()
    {
        var issues = new List<AuditIssue>
        {
            new()
            {
                Rule = "required-parameter",
                Severity = "warning",
                ElementId = 1,
                Category = "doors",
                Parameter = "Mark",
                CurrentValue = "",
                Source = "structured"
            }
        };
        var profile = new ProjectProfile
        {
            Fixes = new List<FixRecipe>
            {
                new() { Rule = "required-parameter", Category = "doors", Parameter = "Mark", Strategy = "setParam", Value = "D-{element.id}" }
            }
        };

        var plan = FixPlanner.Plan("default", issues, profile, new FixPlanOptions());

        var action = Assert.Single(plan.Actions);
        Assert.Equal("D-1", action.NewValue);
        Assert.Equal("high", action.Confidence);
    }

    [Fact]
    public void Plan_SeverityFilter_SkipsNonMatchingSeverity()
    {
        var issues = new List<AuditIssue>
        {
            new() { Rule = "required-parameter", Severity = "info", ElementId = 1, Category = "doors", Parameter = "Mark" }
        };

        var plan = FixPlanner.Plan(
            "default",
            issues,
            new ProjectProfile(),
            new FixPlanOptions { Severity = "error" });

        Assert.Empty(plan.Actions);
        Assert.Single(plan.Skipped);
    }
}
```

- [ ] **Step 3: Run tests and verify they fail**

Run:

```powershell
dotnet test tests\RevitCli.Tests\RevitCli.Tests.csproj --filter "FullyQualifiedName~SetParamStrategyTests|FullyQualifiedName~RenameByPatternStrategyTests|FullyQualifiedName~FixPlannerTests" --verbosity minimal
```

Expected: compile failure because strategy and planner classes do not exist.

- [ ] **Step 4: Add strategy interface and result**

Create `src/RevitCli/Fix/Strategies/IFixStrategy.cs`:

```csharp
using System.Collections.Generic;
using RevitCli.Profile;
using RevitCli.Shared;

namespace RevitCli.Fix.Strategies;

internal interface IFixStrategy
{
    string Name { get; }
    FixStrategyPlanResult Plan(AuditIssue issue, FixRecipe recipe, bool inferred, string confidence);
}

internal sealed class FixStrategyPlanResult
{
    public bool Success { get; init; }
    public List<FixAction> Actions { get; init; } = new();
    public string Error { get; init; } = "";

    public static FixStrategyPlanResult Ok(FixAction action) => new()
    {
        Success = true,
        Actions = new List<FixAction> { action }
    };

    public static FixStrategyPlanResult Skip(string error) => new()
    {
        Success = false,
        Error = error
    };
}
```

- [ ] **Step 5: Add `SetParamStrategy`**

Create `src/RevitCli/Fix/Strategies/SetParamStrategy.cs`:

```csharp
using System;
using RevitCli.Profile;
using RevitCli.Shared;

namespace RevitCli.Fix.Strategies;

internal sealed class SetParamStrategy : IFixStrategy
{
    public string Name => "setParam";

    public FixStrategyPlanResult Plan(AuditIssue issue, FixRecipe recipe, bool inferred, string confidence)
    {
        if (!issue.ElementId.HasValue)
            return FixStrategyPlanResult.Skip("Issue has no element id.");

        var parameter = recipe.Parameter ?? issue.Parameter;
        if (string.IsNullOrWhiteSpace(parameter))
            return FixStrategyPlanResult.Skip("No target parameter.");

        if (recipe.Value == null)
            return FixStrategyPlanResult.Skip("setParam recipe has no value.");

        var newValue = FixTemplateRenderer.Render(recipe.Value, issue, parameter);
        if (string.IsNullOrWhiteSpace(newValue))
            return FixStrategyPlanResult.Skip("Rendered value is empty.");

        return FixStrategyPlanResult.Ok(new FixAction
        {
            Rule = issue.Rule,
            Strategy = Name,
            ElementId = issue.ElementId.Value,
            Category = recipe.Category ?? issue.Category ?? "",
            Parameter = parameter,
            OldValue = issue.CurrentValue,
            NewValue = newValue,
            Inferred = inferred,
            Confidence = confidence,
            Reason = inferred ? "Inferred setParam action." : "Explicit setParam recipe."
        });
    }
}
```

- [ ] **Step 6: Add `RenameByPatternStrategy`**

Create `src/RevitCli/Fix/Strategies/RenameByPatternStrategy.cs`:

```csharp
using System.Text.RegularExpressions;
using RevitCli.Profile;
using RevitCli.Shared;

namespace RevitCli.Fix.Strategies;

internal sealed class RenameByPatternStrategy : IFixStrategy
{
    public string Name => "renameByPattern";

    public FixStrategyPlanResult Plan(AuditIssue issue, FixRecipe recipe, bool inferred, string confidence)
    {
        if (!issue.ElementId.HasValue)
            return FixStrategyPlanResult.Skip("Issue has no element id.");

        var parameter = recipe.Parameter ?? issue.Parameter;
        if (string.IsNullOrWhiteSpace(parameter))
            return FixStrategyPlanResult.Skip("No target parameter.");

        if (issue.CurrentValue == null)
            return FixStrategyPlanResult.Skip("No current value to rename.");

        if (string.IsNullOrWhiteSpace(recipe.Match))
            return FixStrategyPlanResult.Skip("renameByPattern recipe has no match regex.");

        if (recipe.Replace == null)
            return FixStrategyPlanResult.Skip("renameByPattern recipe has no replace value.");

        if (!Regex.IsMatch(issue.CurrentValue, recipe.Match))
            return FixStrategyPlanResult.Skip($"Current value '{issue.CurrentValue}' does not match '{recipe.Match}'.");

        var newValue = Regex.Replace(issue.CurrentValue, recipe.Match, recipe.Replace);
        if (newValue == issue.CurrentValue)
            return FixStrategyPlanResult.Skip("Replacement does not change the current value.");

        return FixStrategyPlanResult.Ok(new FixAction
        {
            Rule = issue.Rule,
            Strategy = Name,
            ElementId = issue.ElementId.Value,
            Category = recipe.Category ?? issue.Category ?? "",
            Parameter = parameter,
            OldValue = issue.CurrentValue,
            NewValue = newValue,
            Inferred = inferred,
            Confidence = confidence,
            Reason = inferred ? "Inferred renameByPattern action." : "Explicit renameByPattern recipe."
        });
    }
}
```

- [ ] **Step 7: Add planner**

Create `src/RevitCli/Fix/FixPlanner.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using RevitCli.Fix.Strategies;
using RevitCli.Profile;
using RevitCli.Shared;

namespace RevitCli.Fix;

internal static class FixPlanner
{
    private static readonly Dictionary<string, IFixStrategy> Strategies = new(StringComparer.OrdinalIgnoreCase)
    {
        ["setParam"] = new SetParamStrategy(),
        ["renameByPattern"] = new RenameByPatternStrategy()
    };

    public static FixPlan Plan(
        string checkName,
        IReadOnlyList<AuditIssue> issues,
        ProjectProfile profile,
        FixPlanOptions options)
    {
        var plan = new FixPlan { CheckName = checkName };

        foreach (var issue in issues)
        {
            if (options.Rules.Count > 0 && !options.Rules.Contains(issue.Rule))
            {
                AddSkipped(plan, issue, $"Filtered by --rule.");
                continue;
            }

            if (!string.IsNullOrWhiteSpace(options.Severity) &&
                !string.Equals(options.Severity, issue.Severity, StringComparison.OrdinalIgnoreCase))
            {
                AddSkipped(plan, issue, $"Filtered by --severity.");
                continue;
            }

            var match = FixRecipeMatcher.Match(issue, profile.Fixes);
            if (!match.Success)
            {
                plan.Warnings.Add(match.Error);
                AddSkipped(plan, issue, match.Error);
                continue;
            }

            var recipe = match.Recipe ?? InferRecipe(issue);
            if (recipe == null)
            {
                AddSkipped(plan, issue, "No matching fix recipe and no safe inference.");
                continue;
            }

            var inferred = match.Recipe == null;
            var confidence = inferred
                ? string.Equals(issue.Source, "structured", StringComparison.OrdinalIgnoreCase) ? "medium" : "low"
                : "high";

            if (!Strategies.TryGetValue(recipe.Strategy, out var strategy))
            {
                plan.Warnings.Add($"Unsupported fix strategy '{recipe.Strategy}'.");
                AddSkipped(plan, issue, $"Unsupported fix strategy '{recipe.Strategy}'.");
                continue;
            }

            var result = strategy.Plan(issue, recipe, inferred, confidence);
            if (!result.Success)
            {
                AddSkipped(plan, issue, result.Error);
                continue;
            }

            plan.Actions.AddRange(result.Actions);
        }

        return plan;
    }

    private static FixRecipe? InferRecipe(AuditIssue issue)
    {
        if (!issue.ElementId.HasValue || string.IsNullOrWhiteSpace(issue.Parameter))
            return null;

        if (!string.IsNullOrWhiteSpace(issue.ExpectedValue))
        {
            return new FixRecipe
            {
                Rule = issue.Rule,
                Category = issue.Category,
                Parameter = issue.Parameter,
                Strategy = "setParam",
                Value = "{expectedValue}"
            };
        }

        if (string.Equals(issue.Rule, "required-parameter", StringComparison.OrdinalIgnoreCase))
        {
            return new FixRecipe
            {
                Rule = issue.Rule,
                Category = issue.Category,
                Parameter = issue.Parameter,
                Strategy = "setParam",
                Value = "{category}-{element.id}"
            };
        }

        return null;
    }

    private static void AddSkipped(FixPlan plan, AuditIssue issue, string reason)
    {
        plan.Skipped.Add(new FixSkippedIssue
        {
            Rule = issue.Rule,
            Severity = issue.Severity,
            ElementId = issue.ElementId,
            Reason = reason
        });
    }
}
```

- [ ] **Step 8: Run planner tests**

Run:

```powershell
dotnet test tests\RevitCli.Tests\RevitCli.Tests.csproj --filter "FullyQualifiedName~SetParamStrategyTests|FullyQualifiedName~RenameByPatternStrategyTests|FullyQualifiedName~FixPlannerTests" --verbosity minimal
```

Expected: pass.

- [ ] **Step 9: Commit**

```powershell
git add src/RevitCli/Fix tests/RevitCli.Tests/Fix
git commit -m "feat: plan parameter fix actions"
```

## Task 6: Safety Gates, Journal, and Plan Rendering

**Files:**
- Create: `src/RevitCli/Fix/FixPlanSafety.cs`
- Create: `src/RevitCli/Fix/FixPlanRenderer.cs`
- Create: `src/RevitCli/Fix/FixJournal.cs`
- Create: `src/RevitCli/Fix/FixJournalStore.cs`
- Create: `tests/RevitCli.Tests/Fix/FixPlanSafetyTests.cs`
- Create: `tests/RevitCli.Tests/Fix/FixJournalStoreTests.cs`

- [ ] **Step 1: Write safety tests**

Create `tests/RevitCli.Tests/Fix/FixPlanSafetyTests.cs`:

```csharp
using RevitCli.Fix;
using Xunit;

namespace RevitCli.Tests.Fix;

public class FixPlanSafetyTests
{
    [Fact]
    public void ValidateApply_BlocksInferredWithoutFlag()
    {
        var plan = new FixPlan();
        plan.Actions.Add(new FixAction { ElementId = 1, Parameter = "Mark", NewValue = "A", Inferred = true, Confidence = "medium" });

        var result = FixPlanSafety.ValidateApply(plan, yes: true, allowInferred: false, maxChanges: 50);

        Assert.False(result.Success);
        Assert.Contains("allow-inferred", result.Error);
    }

    [Fact]
    public void ValidateApply_BlocksLowConfidenceEvenWithFlag()
    {
        var plan = new FixPlan();
        plan.Actions.Add(new FixAction { ElementId = 1, Parameter = "Mark", NewValue = "A", Inferred = true, Confidence = "low" });

        var result = FixPlanSafety.ValidateApply(plan, yes: true, allowInferred: true, maxChanges: 50);

        Assert.False(result.Success);
        Assert.Contains("low-confidence", result.Error);
    }

    [Fact]
    public void ValidateApply_BlocksMaxChanges()
    {
        var plan = new FixPlan();
        plan.Actions.Add(new FixAction { ElementId = 1, Parameter = "Mark", NewValue = "A" });
        plan.Actions.Add(new FixAction { ElementId = 2, Parameter = "Mark", NewValue = "B" });

        var result = FixPlanSafety.ValidateApply(plan, yes: true, allowInferred: false, maxChanges: 1);

        Assert.False(result.Success);
        Assert.Contains("max", result.Error.ToLowerInvariant());
    }
}
```

- [ ] **Step 2: Write journal tests**

Create `tests/RevitCli.Tests/Fix/FixJournalStoreTests.cs`:

```csharp
using System;
using System.IO;
using RevitCli.Fix;
using Xunit;

namespace RevitCli.Tests.Fix;

public class FixJournalStoreTests
{
    [Fact]
    public void SaveAndLoad_RoundTripsJournal()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"revitcli_fix_journal_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var baseline = Path.Combine(dir, "fix-baseline.json");
            var journal = new FixJournal
            {
                SchemaVersion = 1,
                CheckName = "default",
                BaselinePath = baseline,
                StartedAt = "2026-04-26T00:00:00Z",
                User = "test"
            };
            journal.Actions.Add(new FixAction
            {
                Rule = "required-parameter",
                Strategy = "setParam",
                ElementId = 100,
                Category = "doors",
                Parameter = "Mark",
                OldValue = "",
                NewValue = "D-100",
                Confidence = "high"
            });

            var path = FixJournalStore.SaveForBaseline(baseline, journal);
            var loaded = FixJournalStore.LoadForBaseline(baseline);

            Assert.Equal(path, FixJournalStore.GetJournalPath(baseline));
            Assert.Equal("default", loaded.CheckName);
            var action = Assert.Single(loaded.Actions);
            Assert.Equal(100, action.ElementId);
            Assert.Equal("D-100", action.NewValue);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
```

- [ ] **Step 3: Run tests and verify they fail**

Run:

```powershell
dotnet test tests\RevitCli.Tests\RevitCli.Tests.csproj --filter "FullyQualifiedName~FixPlanSafetyTests|FullyQualifiedName~FixJournalStoreTests" --verbosity minimal
```

Expected: compile failure because safety and journal classes do not exist.

- [ ] **Step 4: Add safety validator**

Create `src/RevitCli/Fix/FixPlanSafety.cs`:

```csharp
using System.Linq;

namespace RevitCli.Fix;

internal static class FixPlanSafety
{
    public static FixSafetyResult ValidateApply(FixPlan plan, bool yes, bool allowInferred, int maxChanges)
    {
        if (!yes)
            return FixSafetyResult.Fail("Apply requires --yes in non-interactive mode.");

        if (maxChanges <= 0)
            return FixSafetyResult.Fail("--max-changes must be greater than 0.");

        if (plan.Actions.Count > maxChanges)
            return FixSafetyResult.Fail($"Planned action count {plan.Actions.Count} exceeds --max-changes {maxChanges}.");

        if (plan.Actions.Any(a => a.Inferred) && !allowInferred)
            return FixSafetyResult.Fail("Inferred actions require --allow-inferred before apply.");

        if (plan.Actions.Any(a => a.Confidence == "low"))
            return FixSafetyResult.Fail("Low-confidence fallback actions are dry-run only in v1.5.");

        return FixSafetyResult.Ok();
    }
}

internal sealed class FixSafetyResult
{
    public bool Success { get; init; }
    public string Error { get; init; } = "";

    public static FixSafetyResult Ok() => new() { Success = true };
    public static FixSafetyResult Fail(string error) => new() { Success = false, Error = error };
}
```

- [ ] **Step 5: Add plan renderer**

Create `src/RevitCli/Fix/FixPlanRenderer.cs`:

```csharp
using System;
using System.Text;

namespace RevitCli.Fix;

internal static class FixPlanRenderer
{
    public static string Render(FixPlan plan)
    {
        var builder = new StringBuilder();
        var inferred = plan.Actions.Count(a => a.Inferred);
        builder.AppendLine($"Fix plan for check '{plan.CheckName}': {plan.Actions.Count} action(s), {plan.Skipped.Count} skipped, {inferred} inferred");
        builder.AppendLine();

        foreach (var action in plan.Actions)
        {
            builder.AppendLine(
                $"  [{action.Strategy}] {action.Rule} Element {action.ElementId} {action.Parameter}: \"{action.OldValue ?? ""}\" -> \"{action.NewValue}\" ({action.Confidence})");
        }

        foreach (var skipped in plan.Skipped)
        {
            builder.AppendLine($"  [SKIPPED] {skipped.Rule} Element {skipped.ElementId?.ToString() ?? "-"}: {skipped.Reason}");
        }

        foreach (var warning in plan.Warnings)
        {
            builder.AppendLine($"Warning: {warning}");
        }

        if (inferred > 0)
            builder.AppendLine("Warning: inferred actions require --allow-inferred before apply.");

        return builder.ToString().TrimEnd();
    }
}
```

If `System.Linq` is missing, add `using System.Linq;` at the top.

- [ ] **Step 6: Add journal classes**

Create `src/RevitCli/Fix/FixJournal.cs`:

```csharp
using System.Collections.Generic;

namespace RevitCli.Fix;

internal sealed class FixJournal
{
    public int SchemaVersion { get; set; } = 1;
    public string Action { get; set; } = "fix";
    public string CheckName { get; set; } = "default";
    public string? ProfilePath { get; set; }
    public string BaselinePath { get; set; } = "";
    public string StartedAt { get; set; } = "";
    public string? CompletedAt { get; set; }
    public string User { get; set; } = "";
    public List<FixAction> Actions { get; set; } = new();
}
```

Create `src/RevitCli/Fix/FixJournalStore.cs`:

```csharp
using System.IO;
using System.Text.Json;

namespace RevitCli.Fix;

internal static class FixJournalStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string GetJournalPath(string baselinePath)
    {
        var full = Path.GetFullPath(baselinePath);
        var dir = Path.GetDirectoryName(full)!;
        var name = Path.GetFileNameWithoutExtension(full);
        return Path.Combine(dir, $"{name}.fixjournal.json");
    }

    public static string SaveForBaseline(string baselinePath, FixJournal journal)
    {
        var path = GetJournalPath(baselinePath);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllText(path, JsonSerializer.Serialize(journal, JsonOptions));
        return path;
    }

    public static FixJournal LoadForBaseline(string baselinePath)
    {
        var path = GetJournalPath(baselinePath);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Fix journal not found: {path}");

        var journal = JsonSerializer.Deserialize<FixJournal>(File.ReadAllText(path), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (journal == null || journal.SchemaVersion != 1)
            throw new InvalidDataException($"Invalid fix journal: {path}");

        return journal;
    }
}
```

- [ ] **Step 7: Run safety and journal tests**

Run:

```powershell
dotnet test tests\RevitCli.Tests\RevitCli.Tests.csproj --filter "FullyQualifiedName~FixPlanSafetyTests|FullyQualifiedName~FixJournalStoreTests" --verbosity minimal
```

Expected: pass.

- [ ] **Step 8: Commit**

```powershell
git add src/RevitCli/Fix tests/RevitCli.Tests/Fix/FixPlanSafetyTests.cs tests/RevitCli.Tests/Fix/FixJournalStoreTests.cs
git commit -m "feat: add fix safety and journal support"
```

## Task 7: Fix Command

**Files:**
- Create: `src/RevitCli/Commands/FixCommand.cs`
- Modify: `src/RevitCli/Commands/CliCommandCatalog.cs`
- Modify: `src/RevitCli/Commands/CompletionsCommand.cs`
- Create: `tests/RevitCli.Tests/Commands/FixCommandTests.cs`
- Modify: `tests/RevitCli.Tests/Commands/CliCommandCatalogTests.cs`

- [ ] **Step 1: Write command tests for dry-run and safety gates**

Create `tests/RevitCli.Tests/Commands/FixCommandTests.cs` with tests that use `FakeHttpHandler` responses for `/api/audit`, `/api/snapshot`, and `/api/elements/set`. If current `FakeHttpHandler` only supports one response, create a small local `QueueHttpHandler` inside this test file:

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

public class FixCommandTests
{
    [Fact]
    public async Task Fix_DryRun_PrintsPlanAndDoesNotCallSet()
    {
        var profilePath = WriteProfile("""
version: 1
checks:
  default:
    failOn: error
    auditRules:
      - rule: required-parameter
fixes:
  - rule: required-parameter
    category: doors
    parameter: Mark
    strategy: setParam
    value: "D-{element.id}"
""");
        try
        {
            var handler = new QueueHttpHandler();
            handler.Enqueue("/api/audit", ApiResponse<AuditResult>.Ok(new AuditResult
            {
                Passed = 0,
                Failed = 1,
                Issues = new List<AuditIssue>
                {
                    new()
                    {
                        Rule = "required-parameter",
                        Severity = "warning",
                        Message = "Missing Mark",
                        ElementId = 10,
                        Category = "doors",
                        Parameter = "Mark",
                        CurrentValue = "",
                        Source = "structured"
                    }
                }
            }));
            var client = new RevitClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:17839") });
            var writer = new StringWriter();

            var exitCode = await FixCommand.ExecuteAsync(
                client, "default", profilePath, Array.Empty<string>(), null,
                dryRun: true, apply: false, yes: false, allowInferred: false,
                maxChanges: 50, baselineOutput: null, noSnapshot: false, writer);

            Assert.Equal(0, exitCode);
            Assert.Contains("Fix plan", writer.ToString());
            Assert.Contains("D-10", writer.ToString());
            Assert.DoesNotContain(handler.Requests, r => r.EndsWith("/api/elements/set", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            File.Delete(profilePath);
        }
    }

    [Fact]
    public async Task Fix_ApplyWithoutAllowInferred_BlocksBeforeSnapshot()
    {
        var profilePath = WriteProfile("""
version: 1
checks:
  default:
    failOn: error
""");
        try
        {
            var handler = new QueueHttpHandler();
            handler.Enqueue("/api/audit", ApiResponse<AuditResult>.Ok(new AuditResult
            {
                Issues = new List<AuditIssue>
                {
                    new()
                    {
                        Rule = "required-parameter",
                        Severity = "warning",
                        Message = "Missing Mark",
                        ElementId = 10,
                        Category = "doors",
                        Parameter = "Mark",
                        CurrentValue = "",
                        Source = "structured"
                    }
                }
            }));
            var client = new RevitClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:17839") });
            var writer = new StringWriter();

            var exitCode = await FixCommand.ExecuteAsync(
                client, "default", profilePath, Array.Empty<string>(), null,
                dryRun: false, apply: true, yes: true, allowInferred: false,
                maxChanges: 50, baselineOutput: null, noSnapshot: false, writer);

            Assert.Equal(1, exitCode);
            Assert.Contains("allow-inferred", writer.ToString());
            Assert.DoesNotContain(handler.Requests, r => r.EndsWith("/api/snapshot", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            File.Delete(profilePath);
        }
    }

    private static string WriteProfile(string yaml)
    {
        var path = Path.Combine(Path.GetTempPath(), $"revitcli_fix_command_{Guid.NewGuid():N}.yml");
        File.WriteAllText(path, yaml);
        return path;
    }
}

internal sealed class QueueHttpHandler : HttpMessageHandler
{
    private readonly Queue<(string Path, string Json)> _responses = new();
    public List<string> Requests { get; } = new();

    public void Enqueue<T>(string path, ApiResponse<T> response)
    {
        _responses.Enqueue((path, JsonSerializer.Serialize(response)));
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!.AbsolutePath);
        var next = _responses.Dequeue();
        Assert.Equal(next.Path, request.RequestUri.AbsolutePath);
        if (request.Content != null)
            _ = await request.Content.ReadAsStringAsync(cancellationToken);

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(next.Json, Encoding.UTF8, "application/json")
        };
    }
}
```

- [ ] **Step 2: Run command tests and verify they fail**

Run:

```powershell
dotnet test tests\RevitCli.Tests\RevitCli.Tests.csproj --filter FullyQualifiedName~FixCommandTests --verbosity minimal
```

Expected: compile failure because `FixCommand` does not exist.

- [ ] **Step 3: Add `FixCommand`**

Create `src/RevitCli/Commands/FixCommand.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using RevitCli.Checks;
using RevitCli.Client;
using RevitCli.Fix;
using RevitCli.Shared;

namespace RevitCli.Commands;

public static class FixCommand
{
    public static Command Create(RevitClient client)
    {
        var checkNameArg = new Argument<string?>("checkName", () => null, "Check set name (default: 'default')");
        var profileOpt = new Option<string?>("--profile", "Path to .revitcli.yml profile");
        var ruleOpt = new Option<string[]>("--rule", () => Array.Empty<string>(), "Only fix matching rule. Repeat for multiple rules.") { AllowMultipleArgumentsPerToken = true };
        var severityOpt = new Option<string?>("--severity", "Only fix severity: error, warning, info");
        var dryRunOpt = new Option<bool>("--dry-run", "Preview fixes without applying");
        var applyOpt = new Option<bool>("--apply", "Apply fixes");
        var yesOpt = new Option<bool>("--yes", "Confirm apply in non-interactive mode");
        var allowInferredOpt = new Option<bool>("--allow-inferred", "Allow medium-confidence inferred fixes to apply");
        var maxChangesOpt = new Option<int>("--max-changes", () => 50, "Maximum number of changes");
        var baselineOutputOpt = new Option<string?>("--baseline-output", "Path for pre-fix baseline snapshot");
        var noSnapshotOpt = new Option<bool>("--no-snapshot", "Skip baseline snapshot and disable rollback support");

        var command = new Command("fix", "Plan or apply profile-driven parameter fixes")
        {
            checkNameArg, profileOpt, ruleOpt, severityOpt, dryRunOpt, applyOpt, yesOpt,
            allowInferredOpt, maxChangesOpt, baselineOutputOpt, noSnapshotOpt
        };

        command.SetHandler(async (checkName, profile, rules, severity, dryRun, apply, yes, allowInferred, maxChanges, baselineOutput, noSnapshot) =>
        {
            Environment.ExitCode = await ExecuteAsync(
                client, checkName, profile, rules, severity, dryRun, apply, yes,
                allowInferred, maxChanges, baselineOutput, noSnapshot, Console.Out);
        }, checkNameArg, profileOpt, ruleOpt, severityOpt, dryRunOpt, applyOpt, yesOpt, allowInferredOpt, maxChangesOpt, baselineOutputOpt, noSnapshotOpt);

        return command;
    }

    public static async Task<int> ExecuteAsync(
        RevitClient client,
        string? checkName,
        string? profilePath,
        IReadOnlyList<string> rules,
        string? severity,
        bool dryRun,
        bool apply,
        bool yes,
        bool allowInferred,
        int maxChanges,
        string? baselineOutput,
        bool noSnapshot,
        TextWriter output)
    {
        if (dryRun && apply)
        {
            await output.WriteLineAsync("Error: --dry-run and --apply cannot be combined.");
            return 1;
        }

        var run = await CheckRunner.RunAsync(client, checkName, profilePath);
        if (!run.Success)
        {
            await output.WriteLineAsync(run.Error);
            return 1;
        }

        var options = new FixPlanOptions { Severity = severity };
        foreach (var rule in rules)
            options.Rules.Add(rule);

        var plan = FixPlanner.Plan(run.Data!.CheckName, run.Data.Issues, run.Data.Profile, options);

        if (!apply)
        {
            await output.WriteLineAsync(FixPlanRenderer.Render(plan));
            return 0;
        }

        if (plan.Actions.Count == 0)
        {
            await output.WriteLineAsync("No fixable issues found.");
            return 0;
        }

        var safety = FixPlanSafety.ValidateApply(plan, yes, allowInferred, maxChanges);
        if (!safety.Success)
        {
            await output.WriteLineAsync($"Error: {safety.Error}");
            return 1;
        }

        string? baselinePath = null;
        if (!noSnapshot)
        {
            baselinePath = baselineOutput ?? Path.Combine(".revitcli", $"fix-baseline-{DateTime.UtcNow:yyyyMMddTHHmmssZ}.json");
            var snapshotResult = await client.CaptureSnapshotAsync(new SnapshotRequest());
            if (!snapshotResult.Success)
            {
                await output.WriteLineAsync($"Error: failed to capture baseline: {snapshotResult.Error}");
                return 1;
            }

            var baselineDir = Path.GetDirectoryName(Path.GetFullPath(baselinePath));
            if (!string.IsNullOrEmpty(baselineDir) && !Directory.Exists(baselineDir))
                Directory.CreateDirectory(baselineDir);

            await File.WriteAllTextAsync(baselinePath, JsonSerializer.Serialize(snapshotResult.Data, new JsonSerializerOptions { WriteIndented = true }));
            await output.WriteLineAsync($"Baseline saved: {baselinePath}");
        }
        else
        {
            await output.WriteLineAsync("Warning: --no-snapshot disables automatic rollback support.");
        }

        var journalPath = "";
        if (baselinePath != null)
        {
            var journal = new FixJournal
            {
                CheckName = run.Data.CheckName,
                ProfilePath = run.Data.ProfilePath,
                BaselinePath = baselinePath,
                StartedAt = DateTime.UtcNow.ToString("o"),
                User = Environment.UserName,
                Actions = plan.Actions.ToList()
            };
            journalPath = FixJournalStore.SaveForBaseline(baselinePath, journal);
            await output.WriteLineAsync($"Journal saved: {journalPath}");
        }

        var modified = 0;
        foreach (var action in plan.Actions)
        {
            var setResult = await client.SetParameterAsync(new SetRequest
            {
                ElementId = action.ElementId,
                Param = action.Parameter,
                Value = action.NewValue,
                DryRun = false
            });

            if (!setResult.Success)
            {
                await output.WriteLineAsync($"Error: set failed for element {action.ElementId}: {setResult.Error}");
                if (baselinePath != null)
                    await output.WriteLineAsync($"Rollback: revitcli rollback {baselinePath} --yes");
                return 1;
            }

            modified += setResult.Data?.Affected ?? 0;
        }

        await output.WriteLineAsync($"Modified {modified} element parameter(s).");
        if (baselinePath != null)
            await output.WriteLineAsync($"Rollback: revitcli rollback {baselinePath} --yes");

        return 0;
    }
}
```

- [ ] **Step 4: Register command**

Modify `src/RevitCli/Commands/CliCommandCatalog.cs`:

```csharp
("fix", "Plan or apply profile-driven parameter fixes"),
("rollback", "Restore parameters changed by a fix baseline"),
```

Add command registration:

```csharp
root.AddCommand(FixCommand.Create(client));
```

Do not add `RollbackCommand` until Task 8 creates it.

- [ ] **Step 5: Run tests**

Run:

```powershell
dotnet test tests\RevitCli.Tests\RevitCli.Tests.csproj --filter "FullyQualifiedName~FixCommandTests|FullyQualifiedName~CliCommandCatalogTests" --verbosity minimal
```

Expected: pass after catalog test expectations are updated for `fix`.

- [ ] **Step 6: Commit**

```powershell
git add src/RevitCli/Commands/FixCommand.cs src/RevitCli/Commands/CliCommandCatalog.cs src/RevitCli/Commands/CompletionsCommand.cs tests/RevitCli.Tests/Commands/FixCommandTests.cs tests/RevitCli.Tests/Commands/CliCommandCatalogTests.cs
git commit -m "feat: add fix command"
```

## Task 8: Rollback Command

**Files:**
- Create: `src/RevitCli/Commands/RollbackCommand.cs`
- Modify: `src/RevitCli/Commands/CliCommandCatalog.cs`
- Modify: `src/RevitCli/Commands/CompletionsCommand.cs`
- Create: `tests/RevitCli.Tests/Commands/RollbackCommandTests.cs`

- [ ] **Step 1: Write rollback command tests**

Create `tests/RevitCli.Tests/Commands/RollbackCommandTests.cs`:

```csharp
using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using RevitCli.Commands;
using RevitCli.Client;
using RevitCli.Fix;
using RevitCli.Shared;
using Xunit;

namespace RevitCli.Tests.Commands;

public class RollbackCommandTests
{
    [Fact]
    public async Task Rollback_MissingBaseline_ReturnsError()
    {
        var client = new RevitClient(new HttpClient(new QueueHttpHandler()) { BaseAddress = new Uri("http://localhost:17839") });
        var writer = new StringWriter();

        var exitCode = await RollbackCommand.ExecuteAsync(client, "missing.json", dryRun: false, yes: true, maxChanges: 50, writer);

        Assert.Equal(1, exitCode);
        Assert.Contains("baseline", writer.ToString().ToLowerInvariant());
    }

    [Fact]
    public async Task Rollback_DryRun_PrintsReverseActions()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"revitcli_rollback_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var baseline = Path.Combine(dir, "fix-baseline.json");
            File.WriteAllText(baseline, JsonSerializer.Serialize(new ModelSnapshot { SchemaVersion = 1 }));
            FixJournalStore.SaveForBaseline(baseline, new FixJournal
            {
                BaselinePath = baseline,
                CheckName = "default",
                Actions =
                {
                    new FixAction
                    {
                        Rule = "required-parameter",
                        Strategy = "setParam",
                        ElementId = 100,
                        Category = "doors",
                        Parameter = "Mark",
                        OldValue = "",
                        NewValue = "D-100",
                        Confidence = "high"
                    }
                }
            });
            var client = new RevitClient(new HttpClient(new QueueHttpHandler()) { BaseAddress = new Uri("http://localhost:17839") });
            var writer = new StringWriter();

            var exitCode = await RollbackCommand.ExecuteAsync(client, baseline, dryRun: true, yes: false, maxChanges: 50, writer);

            Assert.Equal(0, exitCode);
            Assert.Contains("D-100", writer.ToString());
            Assert.Contains("\"\"", writer.ToString());
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Rollback_Apply_SkipsConflictWhenCurrentValueChanged()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"revitcli_rollback_conflict_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var baseline = Path.Combine(dir, "fix-baseline.json");
            File.WriteAllText(baseline, JsonSerializer.Serialize(new ModelSnapshot { SchemaVersion = 1 }));
            FixJournalStore.SaveForBaseline(baseline, new FixJournal
            {
                BaselinePath = baseline,
                CheckName = "default",
                Actions =
                {
                    new FixAction
                    {
                        Rule = "required-parameter",
                        Strategy = "setParam",
                        ElementId = 100,
                        Category = "doors",
                        Parameter = "Mark",
                        OldValue = "",
                        NewValue = "D-100",
                        Confidence = "high"
                    }
                }
            });

            var handler = new QueueHttpHandler();
            handler.Enqueue("/api/elements/set", ApiResponse<SetResult>.Ok(new SetResult
            {
                Affected = 1,
                Preview =
                {
                    new SetPreviewItem
                    {
                        Id = 100,
                        Name = "Door 100",
                        OldValue = "USER-EDITED",
                        NewValue = ""
                    }
                }
            }));
            var client = new RevitClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:17839") });
            var writer = new StringWriter();

            var exitCode = await RollbackCommand.ExecuteAsync(client, baseline, dryRun: false, yes: true, maxChanges: 50, writer);

            Assert.Equal(0, exitCode);
            Assert.Contains("conflict", writer.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.Single(handler.Requests);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
```

- [ ] **Step 2: Run tests and verify they fail**

Run:

```powershell
dotnet test tests\RevitCli.Tests\RevitCli.Tests.csproj --filter FullyQualifiedName~RollbackCommandTests --verbosity minimal
```

Expected: compile failure because `RollbackCommand` does not exist.

- [ ] **Step 3: Add rollback command**

Create `src/RevitCli/Commands/RollbackCommand.cs`:

```csharp
using System;
using System.CommandLine;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using RevitCli.Client;
using RevitCli.Fix;
using RevitCli.Shared;

namespace RevitCli.Commands;

public static class RollbackCommand
{
    public static Command Create(RevitClient client)
    {
        var baselineArg = new Argument<string>("baseline", "Baseline snapshot path written by fix --apply");
        var dryRunOpt = new Option<bool>("--dry-run", "Preview rollback without applying");
        var yesOpt = new Option<bool>("--yes", "Confirm rollback apply in non-interactive mode");
        var maxChangesOpt = new Option<int>("--max-changes", () => 50, "Maximum number of rollback writes");

        var command = new Command("rollback", "Restore parameters changed by a fix baseline")
        {
            baselineArg, dryRunOpt, yesOpt, maxChangesOpt
        };

        command.SetHandler(async (baseline, dryRun, yes, maxChanges) =>
        {
            Environment.ExitCode = await ExecuteAsync(client, baseline, dryRun, yes, maxChanges, Console.Out);
        }, baselineArg, dryRunOpt, yesOpt, maxChangesOpt);

        return command;
    }

    public static async Task<int> ExecuteAsync(
        RevitClient client,
        string baselinePath,
        bool dryRun,
        bool yes,
        int maxChanges,
        TextWriter output)
    {
        if (!File.Exists(baselinePath))
        {
            await output.WriteLineAsync($"Error: baseline not found: {baselinePath}");
            return 1;
        }

        try
        {
            _ = JsonSerializer.Deserialize<ModelSnapshot>(File.ReadAllText(baselinePath));
        }
        catch (Exception ex)
        {
            await output.WriteLineAsync($"Error: baseline is not valid JSON: {ex.Message}");
            return 1;
        }

        FixJournal journal;
        try
        {
            journal = FixJournalStore.LoadForBaseline(baselinePath);
        }
        catch (Exception ex)
        {
            await output.WriteLineAsync($"Error: {ex.Message}");
            return 1;
        }

        if (journal.Actions.Count > maxChanges)
        {
            await output.WriteLineAsync($"Error: rollback action count {journal.Actions.Count} exceeds --max-changes {maxChanges}.");
            return 1;
        }

        if (!dryRun && !yes)
        {
            await output.WriteLineAsync("Error: rollback apply requires --yes in non-interactive mode.");
            return 1;
        }

        var restored = 0;
        var conflicts = 0;
        foreach (var action in journal.Actions)
        {
            var oldValue = action.OldValue ?? "";
            await output.WriteLineAsync(
                $"Rollback Element {action.ElementId} {action.Parameter}: \"{action.NewValue}\" -> \"{oldValue}\"");

            if (dryRun)
                continue;

            var preview = await client.SetParameterAsync(new SetRequest
            {
                ElementId = action.ElementId,
                Param = action.Parameter,
                Value = oldValue,
                DryRun = true
            });

            if (!preview.Success)
            {
                await output.WriteLineAsync($"Error: rollback preview failed for element {action.ElementId}: {preview.Error}");
                return 1;
            }

            var currentValue = preview.Data?.Preview.Count > 0
                ? preview.Data.Preview[0].OldValue ?? ""
                : "";

            if (currentValue != (action.NewValue ?? ""))
            {
                conflicts++;
                await output.WriteLineAsync(
                    $"Conflict: element {action.ElementId} {action.Parameter} is \"{currentValue}\", expected \"{action.NewValue}\". Skipped.");
                continue;
            }

            var result = await client.SetParameterAsync(new SetRequest
            {
                ElementId = action.ElementId,
                Param = action.Parameter,
                Value = oldValue,
                DryRun = false
            });

            if (!result.Success)
            {
                await output.WriteLineAsync($"Error: rollback failed for element {action.ElementId}: {result.Error}");
                return 1;
            }

            restored += result.Data?.Affected ?? 0;
        }

        if (dryRun)
            await output.WriteLineAsync($"Rollback dry-run: {journal.Actions.Count} action(s).");
        else
            await output.WriteLineAsync($"Rollback restored {restored} element parameter(s), {conflicts} conflict(s).");

        return 0;
    }
}
```

- [ ] **Step 4: Register rollback**

Modify `src/RevitCli/Commands/CliCommandCatalog.cs`:

```csharp
root.AddCommand(RollbackCommand.Create(client));
```

Add interactive help entry:

```csharp
("rollback <baseline>", "Restore parameters changed by a fix baseline"),
```

- [ ] **Step 5: Run rollback tests**

Run:

```powershell
dotnet test tests\RevitCli.Tests\RevitCli.Tests.csproj --filter "FullyQualifiedName~RollbackCommandTests|FullyQualifiedName~CliCommandCatalogTests" --verbosity minimal
```

Expected: pass after catalog expectations include `rollback`.

- [ ] **Step 6: Commit**

```powershell
git add src/RevitCli/Commands/RollbackCommand.cs src/RevitCli/Commands/CliCommandCatalog.cs src/RevitCli/Commands/CompletionsCommand.cs tests/RevitCli.Tests/Commands/RollbackCommandTests.cs tests/RevitCli.Tests/Commands/CliCommandCatalogTests.cs
git commit -m "feat: add fix rollback command"
```

## Task 9: Add-in Structured Audit Metadata

**Files:**
- Modify: `src/RevitCli.Addin/Services/RealRevitOperations.cs`
- Modify: `src/RevitCli.Addin/Services/PlaceholderRevitOperations.cs`
- Create or modify Add-in tests under `tests/RevitCli.Addin.Tests`

- [ ] **Step 1: Locate AuditIssue construction sites**

Run:

```powershell
Get-ChildItem -Path 'src\RevitCli.Addin' -Recurse -File -Filter '*.cs' |
  Select-String -Pattern 'new AuditIssue' -Context 2,8
```

Expected: output shows every Add-in audit issue constructor in `RealRevitOperations.cs` and `PlaceholderRevitOperations.cs`.

- [ ] **Step 2: Add metadata to required parameter issues**

In `RealRevitOperations.cs`, update required parameter issue creation so each issue includes:

```csharp
Category = spec.Category,
Parameter = spec.Parameter,
Target = spec.Category,
CurrentValue = value ?? "",
ExpectedValue = spec.RequireNonEmpty ? null : value,
Source = "structured"
```

Use the local variable names from the existing method; if names differ, keep the property assignments semantically identical.

- [ ] **Step 3: Add metadata to naming issues**

In naming pattern issue creation, set:

```csharp
Category = pattern.Target,
Parameter = "Name",
Target = pattern.Target,
CurrentValue = currentName,
ExpectedValue = null,
Source = "structured"
```

If the current code checks sheet numbers or room numbers rather than `Name`, set `Parameter` to the exact parameter that the check reads.

- [ ] **Step 4: Add metadata to placeholder audit issues**

In `PlaceholderRevitOperations.cs`, make any placeholder issue explicitly include:

```csharp
Category = "doors",
Parameter = "Mark",
Target = "doors",
CurrentValue = "",
ExpectedValue = "D-100",
Source = "structured"
```

Only use these values for placeholder test data; do not invent production metadata.

- [ ] **Step 5: Add tests for JSON contract**

Create an Add-in or protocol test that serializes an `AuditResult` with a structured issue and verifies `category`, `parameter`, and `source` appear. Use shared DTO serialization if full Revit API setup is not needed:

```csharp
var result = new AuditResult
{
    Issues =
    {
        new AuditIssue
        {
            Rule = "required-parameter",
            Severity = "warning",
            Message = "Missing Mark",
            ElementId = 100,
            Category = "doors",
            Parameter = "Mark",
            CurrentValue = "",
            Source = "structured"
        }
    }
};
```

Assert:

```csharp
Assert.Equal("doors", issue.Category);
Assert.Equal("Mark", issue.Parameter);
Assert.Equal("structured", issue.Source);
```

- [ ] **Step 6: Run Add-in tests**

Run:

```powershell
dotnet test tests\RevitCli.Addin.Tests\RevitCli.Addin.Tests.csproj -p:RevitInstallDir="D:\revit2026\Revit 2026" --verbosity minimal
```

Expected: pass.

- [ ] **Step 7: Commit**

```powershell
git add src/RevitCli.Addin/Services/RealRevitOperations.cs src/RevitCli.Addin/Services/PlaceholderRevitOperations.cs tests/RevitCli.Addin.Tests
git commit -m "feat: emit structured audit issue metadata"
```

## Task 10: Starter Profiles, Docs, Smoke, and Full Verification

**Files:**
- Modify: `profiles/architectural-issue.yml`
- Modify: `profiles/interior-room-data.yml`
- Modify: `profiles/general-publish.yml`
- Modify: `README.md`
- Modify: `CHANGELOG.md`
- Modify: `scripts/smoke-revit2026.ps1`

- [ ] **Step 1: Add commented starter recipes**

In each starter profile, append a commented example block. Example for `profiles/architectural-issue.yml`:

```yaml
# fixes:
#   - rule: required-parameter
#     category: doors
#     parameter: Mark
#     strategy: setParam
#     value: "D-{element.id}"
#     maxChanges: 20
#
#   - rule: naming
#     category: rooms
#     parameter: Name
#     strategy: renameByPattern
#     match: "^Room (.+)$"
#     replace: "$1"
#     maxChanges: 50
```

Keep these commented so starter profiles do not silently write to a user's model.

- [ ] **Step 2: Update README command table**

Add rows:

```markdown
| `revitcli fix [checkName]` | Preview or apply profile-driven parameter fixes |
| `revitcli rollback <baseline>` | Restore parameters changed by a fix baseline |
```

Add a short feature section:

```markdown
### Auto-fix Playbooks

- `fix --dry-run` turns `check` issues into a reviewable parameter write plan.
- `fix --apply --yes` writes a snapshot baseline and fix journal before modifying the model.
- `rollback <baseline> --yes` restores only the parameters touched by that fix journal.
- v1.5 supports parameter-only strategies: `setParam` and `renameByPattern`.
```

- [ ] **Step 3: Update CHANGELOG**

Add an unreleased or `v1.5.0` section matching the repository style:

```markdown
## v1.5.0 - Auto-fix Playbooks

- Added `revitcli fix` for dry-run and apply of profile-driven parameter fixes.
- Added `revitcli rollback` for journal-scoped restoration of fix changes.
- Added structured `AuditIssue` metadata for fix planning.
- Added `setParam` and `renameByPattern` fix strategies.
- Kept delete, geometry, family editing, and cross-document fixes out of scope.
```

- [ ] **Step 4: Extend smoke script behind explicit flags**

Add parameters to `scripts/smoke-revit2026.ps1`:

```powershell
[switch]$FixDryRun,
[switch]$FixApply,
[string]$FixCheckName = "default",
[string]$FixProfile
```

Add commands only when flags are present:

```powershell
if ($FixDryRun) {
    Invoke-Step -Name "fix dry-run" -Command @("revitcli", "fix", $FixCheckName, "--dry-run", "--profile", $FixProfile)
}

if ($FixApply) {
    Invoke-Step -Name "fix apply" -Command @("revitcli", "fix", $FixCheckName, "--apply", "--yes", "--profile", $FixProfile)
    $baseline = Get-ChildItem -LiteralPath ".revitcli" -Filter "fix-baseline-*.json" |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if ($null -eq $baseline) {
        throw "fix apply did not create a fix baseline"
    }
    Invoke-Step -Name "rollback" -Command @("revitcli", "rollback", $baseline.FullName, "--yes")
}
```

Adjust helper names to match the current smoke script. Do not run apply unless `$FixApply` is explicitly passed.

- [ ] **Step 5: Run markdown and script checks**

Run:

```powershell
git diff --check
```

Expected: no output.

Run:

```powershell
$null = [System.Management.Automation.Language.Parser]::ParseFile(
  (Resolve-Path 'scripts\smoke-revit2026.ps1'),
  [ref]$null,
  [ref]$null
)
```

Expected: no parser errors.

- [ ] **Step 6: Run pure CLI tests**

Run:

```powershell
dotnet test tests\RevitCli.Tests\RevitCli.Tests.csproj --verbosity minimal
```

Expected: all tests pass.

- [ ] **Step 7: Run Add-in tests**

Run:

```powershell
dotnet test tests\RevitCli.Addin.Tests\RevitCli.Addin.Tests.csproj -p:RevitInstallDir="D:\revit2026\Revit 2026" --verbosity minimal
```

Expected: all tests pass on a machine with Revit 2026 API DLLs.

- [ ] **Step 8: Run live Revit 2026 smoke when Revit and test model are open**

Run with dry-run first:

```powershell
scripts\smoke-revit2026.ps1 `
  -RevitInstallDir "D:\revit2026\Revit 2026" `
  -FixDryRun `
  -FixProfile ".revitcli.yml"
```

Run apply only against a controlled model and safe profile:

```powershell
scripts\smoke-revit2026.ps1 `
  -RevitInstallDir "D:\revit2026\Revit 2026" `
  -FixDryRun `
  -FixApply `
  -FixProfile ".revitcli.yml"
```

Expected report contains:

- `doctor`
- `check`
- `fix dry-run`
- `fix apply`
- `rollback`
- final command exit codes
- baseline path
- journal path

- [ ] **Step 9: Commit**

```powershell
git add profiles README.md CHANGELOG.md scripts/smoke-revit2026.ps1
git commit -m "docs: document auto-fix playbooks workflow"
```

## Final Verification Before PR

- [ ] Run:

```powershell
git diff --check
```

Expected: no output.

- [ ] Run:

```powershell
dotnet test tests\RevitCli.Tests\RevitCli.Tests.csproj --verbosity minimal
```

Expected: all tests pass.

- [ ] Run when Revit 2026 API DLLs are available:

```powershell
dotnet test tests\RevitCli.Addin.Tests\RevitCli.Addin.Tests.csproj -p:RevitInstallDir="D:\revit2026\Revit 2026" --verbosity minimal
```

Expected: all tests pass.

- [ ] Run live smoke only when Revit 2026 and the controlled model are open:

```powershell
scripts\smoke-revit2026.ps1 -RevitInstallDir "D:\revit2026\Revit 2026" -FixDryRun
```

Expected: dry-run smoke succeeds without modifying the model.

- [ ] Run apply smoke only with a safe profile and restorable test model:

```powershell
scripts\smoke-revit2026.ps1 -RevitInstallDir "D:\revit2026\Revit 2026" -FixDryRun -FixApply -FixProfile ".revitcli.yml"
```

Expected: apply writes a baseline and journal, rollback restores the touched parameters, and final check output is recorded.

## Implementation Guardrails

- Do not implement `purgeUnplaced` or `linkRoomToBoundary` in v1.5.
- Do not add `/api/fix/apply`.
- Do not parse `check` table output to plan fixes.
- Do not allow low-confidence message fallback to apply.
- Do not silently choose between two same-priority recipes.
- Do not import a full snapshot during rollback.
- Do not make starter profiles write to a model by default.
- Do not claim live Revit smoke passed unless the command was run and its output was recorded.
