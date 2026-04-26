# Auto-fix Playbooks Design

## Summary

v1.5 turns `check` results into a reversible, reviewable parameter-fix workflow:

```powershell
revitcli check
revitcli fix --dry-run
revitcli fix --apply --yes
revitcli check
revitcli rollback .revitcli/fix-baseline-20260426T153000Z.json --yes
```

The first implementation is intentionally narrower than the full roadmap idea. It implements a parameter-write auto-fix slice only:

- `setParam`
- `renameByPattern`

The first implementation does not delete elements, move geometry, repair room boundaries, edit families, operate across documents, or add AI-generated profile logic. Those are outside the v1.5 MVP boundary.

The guiding constraint is that v1.5 must produce a real closed loop using the current CLI/Add-in architecture and the existing `/api/elements/set` endpoint wherever possible.

## Confirmed Decisions

| Decision | Outcome | Reason |
|---|---|---|
| Command shape | Add top-level `fix` and `rollback` commands | Keeps `check` as read-only diagnosis and `fix` as write intent. |
| MVP scope | Parameter-fix workflow only | Fits the existing set endpoint and avoids geometry/delete risk. |
| Strategies | `setParam`, `renameByPattern` | Enough to prove the check-to-fix loop without expanding Revit API surface too early. |
| Excluded strategies | `purgeUnplaced`, `linkRoomToBoundary` | Need deletion, relationship repair, or richer Add-in semantics. |
| Rollback | Required in v1.5 MVP | `fix --apply` must create a baseline and support recovery. |
| AuditIssue schema | Extend DTO with structured fields | Planner should not parse human messages as the primary contract. |
| Add-in compatibility | Strong compatibility with fallback | New Add-in emits structured fields; old Add-in can dry-run best-effort inferred fixes. |
| Fix source | All `check` issues with `elementId` may be considered | Planner decides fixability; strategy scope stays parameter-only. |
| Inference | Allowed, but guarded | Inferred fixes can dry-run by default; apply requires `--allow-inferred`. |
| Rollback scope | Restore only parameters touched by the fix journal | Avoids overwriting unrelated manual edits after fix apply. |

## Goals

- Convert suppressed `check` issues into a `FixPlan`.
- Support dry-run previews before any model write.
- Support real `fix --apply --yes` for parameter-only strategies.
- Write a fix baseline before apply unless the user explicitly opts out.
- Provide `rollback <baseline>` for the parameters touched by a v1.5 fix run.
- Extend `AuditIssue` with structured, nullable metadata for reliable planning.
- Preserve existing `check` output compatibility for scripts that only consume current fields.
- Keep exit codes simple: `0` success, `1` failure.
- Make all unsafe or inferred writes explicit and auditable.

## Non-Goals

- No geometry modification commands such as `move-wall` or `align-doors`.
- No family editor writeback or family geometry edits.
- No multi-document or linked-model fix workflow.
- No real-time collaboration, cloud worksharing, or BIM360/ACC integration.
- No AI-generated profiles or natural language query/fix language.
- No custom user script strategy mechanism in v1.5.
- No first-class delete strategy in v1.5.
- No new Add-in endpoint for the happy path parameter write. `fix` should call the same set capability used by `set` and `import`.

## User Stories

### Missing Parameter Fix

As a BIM coordinator, I can run `revitcli check`, see doors missing `Mark`, then run `revitcli fix --dry-run` to preview exactly which door parameters would be filled before applying the changes.

### Naming Cleanup

As a project lead, I can define a naming recipe in `.revitcli.yml` and run `revitcli fix --rule naming --apply --yes` to normalize parameter-backed names such as room names or sheet numbers.

### Safe Recovery

As an internal operator, I can run `revitcli rollback <baseline> --yes` after a bad fix run and restore only the parameters changed by that run, rather than importing a full snapshot and overwriting unrelated model edits.

### Legacy Add-in Preview

As a user with an older Add-in, I can still run `fix --dry-run` and see best-effort inferred actions, but I cannot apply those inferred actions unless I explicitly pass `--allow-inferred`.

## Command Matrix

### `revitcli fix [checkName]`

Runs the named check set, plans fix actions, and either previews or applies them.

Default `checkName` is `default`, matching `revitcli check`.

| Option | Type | Default | Meaning |
|---|---|---|---|
| `--profile PATH` | string | discovered `.revitcli.yml` | Load a specific profile. |
| `--rule RULE` | repeatable string | all rules | Only plan issues from matching rules. |
| `--severity error|warning|info` | string | all severities | Only plan issues with this severity. |
| `--dry-run` | bool | true | Preview without writing. |
| `--apply` | bool | false | Write planned actions. Mutually exclusive with `--dry-run`. |
| `--yes` | bool | false | Confirm apply in non-interactive mode. |
| `--allow-inferred` | bool | false | Allow inferred actions to be applied. |
| `--max-changes N` | int | 50 | Maximum number of actions allowed in one apply. |
| `--baseline-output PATH` | string | `.revitcli/fix-baseline-{timestamp}.json` | Snapshot baseline path. |
| `--no-snapshot` | bool | false | Skip baseline creation. Advanced unsafe mode. |

Exit codes:

| Code | Meaning |
|---|---|
| `0` | Plan produced successfully, no fixable issues found, dry-run succeeded, or apply succeeded. |
| `1` | Profile/check/planning/safety/snapshot/apply failed. |

### `revitcli rollback <baseline>`

Reads the baseline and associated fix journal, plans reverse parameter writes, and applies or previews them.

| Option | Type | Default | Meaning |
|---|---|---|---|
| `<baseline>` | argument | required | Baseline snapshot path written by `fix --apply`. |
| `--dry-run` | bool | false | Preview reverse writes without applying. |
| `--yes` | bool | false | Confirm rollback apply in non-interactive mode. |
| `--max-changes N` | int | 50 | Maximum rollback writes allowed in one apply. |

Exit codes:

| Code | Meaning |
|---|---|
| `0` | Rollback preview or apply completed. |
| `1` | Baseline/journal/read/planning/safety/apply failed. |

## Profile Schema

v1.5 adds a top-level `fixes:` section to `ProjectProfile`.

Top-level fixes keep recipes independent from any specific check type. A recipe can match any structured or inferred issue produced by `check`.

```yaml
version: 1

checks:
  default:
    failOn: error
    auditRules:
      - rule: naming
    requiredParameters:
      - category: doors
        parameter: Mark
        requireNonEmpty: true
        severity: warning

fixes:
  - rule: required-parameter
    category: doors
    parameter: Mark
    strategy: setParam
    value: "{category}-{element.id}"
    maxChanges: 20

  - rule: naming
    category: rooms
    parameter: Name
    strategy: renameByPattern
    match: "^Room (.+)$"
    replace: "$1"
    maxChanges: 50
```

### `FixRecipe`

```csharp
public class FixRecipe
{
    public string? Rule { get; set; }
    public string? Category { get; set; }
    public string? Parameter { get; set; }
    public string Strategy { get; set; } = "";
    public string? Value { get; set; }
    public string? Match { get; set; }
    public string? Replace { get; set; }
    public int? MaxChanges { get; set; }
}
```

Validation rules:

- `strategy` is required.
- `strategy` must be one of `setParam` or `renameByPattern` in v1.5.
- `setParam` requires `parameter` and `value`.
- `renameByPattern` requires `parameter`, `match`, and `replace`.
- `match` must compile as a .NET regex.
- `maxChanges`, if provided, must be positive.
- Unknown future fields should not break profile load unless they collide with required v1.5 fields.

## DTO Schema

### `AuditIssue`

Existing fields remain unchanged:

```csharp
public class AuditIssue
{
    public string Rule { get; set; } = "";
    public string Severity { get; set; } = "";
    public string Message { get; set; } = "";
    public long? ElementId { get; set; }
}
```

v1.5 adds nullable structured fields:

```csharp
public class AuditIssue
{
    public string Rule { get; set; } = "";
    public string Severity { get; set; } = "";
    public string Message { get; set; } = "";
    public long? ElementId { get; set; }

    public string? Category { get; set; }
    public string? Parameter { get; set; }
    public string? Target { get; set; }
    public string? CurrentValue { get; set; }
    public string? ExpectedValue { get; set; }
    public string? Source { get; set; }
}
```

Field meanings:

| Field | Meaning |
|---|---|
| `category` | Element category related to the issue, normalized to CLI aliases where possible. |
| `parameter` | Revit parameter related to the issue. |
| `target` | Naming or check target, such as `rooms` or `sheets`. |
| `currentValue` | Current parameter/name value when known. |
| `expectedValue` | Expected value when the check can derive one. |
| `source` | `structured` for Add-in-provided metadata, `messageFallback` for CLI fallback. |

Serialization compatibility:

- New fields are nullable and omitted when null.
- Existing JSON consumers that only read `rule`, `severity`, `message`, and `elementId` continue to work.
- `check` table output does not need to show every structured field by default.
- `check --output json` should include structured fields when available.

### `FixPlan`

`FixPlan` is CLI-side and does not need to be a shared Add-in DTO unless tests benefit from it.

```csharp
public class FixPlan
{
    public string CheckName { get; set; } = "default";
    public List<FixAction> Actions { get; set; } = new();
    public List<FixSkippedIssue> Skipped { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
}
```

### `FixAction`

```csharp
public class FixAction
{
    public string Rule { get; set; } = "";
    public string Strategy { get; set; } = "";
    public long ElementId { get; set; }
    public string Category { get; set; } = "";
    public string Parameter { get; set; } = "";
    public string? OldValue { get; set; }
    public string NewValue { get; set; } = "";
    public bool Inferred { get; set; }
    public string Confidence { get; set; } = "high";
    public string Reason { get; set; } = "";
}
```

Allowed `confidence` values:

- `high`: structured issue plus explicit recipe.
- `medium`: structured issue plus inferred recipe.
- `low`: message fallback. Low confidence actions can be previewed but not applied in v1.5.

### `FixSkippedIssue`

```csharp
public class FixSkippedIssue
{
    public string Rule { get; set; } = "";
    public string Severity { get; set; } = "";
    public long? ElementId { get; set; }
    public string Reason { get; set; } = "";
}
```

## Strategy Definitions

### `setParam`

Writes a parameter value on a target element.

Required recipe fields:

- `strategy: setParam`
- `parameter`
- `value`

Supported template tokens:

| Token | Value |
|---|---|
| `{element.id}` | Revit element id. |
| `{category}` | Issue or recipe category. |
| `{parameter}` | Target parameter. |
| `{currentValue}` | Current issue value, empty string if unknown. |
| `{expectedValue}` | Expected issue value, empty string if unknown. |

Rules:

- If issue has no `elementId`, skip.
- If recipe value renders to an empty string, skip unless future schema explicitly allows empty writes.
- Use `/api/elements/set` with `elementId`, `param`, `value`, and `dryRun`.
- Do not batch unrelated parameters into a single opaque request if that makes error reporting worse.

### `renameByPattern`

Computes a new parameter value from the current value using regex replacement.

Required recipe fields:

- `strategy: renameByPattern`
- `parameter`
- `match`
- `replace`

Rules:

- `match` must compile.
- `currentValue` must be known from the issue or from dry-run lookup.
- If `match` does not match, skip with reason.
- If replacement equals current value, skip with reason.
- The actual write still uses `/api/elements/set`.
- This does not rename Revit geometry, move elements, or update relationships. It only writes a parameter-backed name/number field.

## Planner Data Flow

```text
ProjectProfile + checkName
  -> CheckRunner executes check with suppressions
  -> AuditIssue[]
  -> issue filters (--rule / --severity)
  -> FixRecipeMatcher
  -> Strategy planner
  -> FixPlan(actions, skipped, warnings)
  -> renderer or applier
```

`CheckCommand` currently owns profile loading, request creation, suppression, reporting, history save, exit-code policy, and notification. v1.5 should extract a reusable internal check runner so `fix` can reuse the same issue set without parsing rendered command output.

Suggested internal shape:

```csharp
internal sealed class CheckRunResult
{
    public ProjectProfile Profile { get; init; } = new();
    public CheckDefinition CheckDefinition { get; init; } = new();
    public string CheckName { get; init; } = "default";
    public List<AuditIssue> Issues { get; init; } = new();
    public int SuppressedCount { get; init; }
    public int DisplayPassed { get; init; }
    public int DisplayFailed { get; init; }
}
```

`CheckCommand` should remain responsible for rendering and saving history. `FixCommand` should use the runner with history save disabled unless the user intentionally runs `check`.

## Recipe Matching

Recipes are matched by specificity:

| Priority | Match |
|---|---|
| 1 | `rule + category + parameter` |
| 2 | `rule + category` |
| 3 | `rule + parameter` |
| 4 | `rule` |
| 5 | automatic inference |

If two explicit recipes have the same priority and both match one issue, planner fails with an ambiguity error. Silent tie-breaking is unsafe.

Automatic inference is allowed only for parameter-like issues where the planner can identify:

- `elementId`
- target `parameter`
- target `newValue`

Inferred actions:

- render in dry-run output;
- have `Inferred = true`;
- cannot apply without `--allow-inferred`;
- are low-confidence if they rely on message fallback.

Low-confidence actions cannot apply in v1.5, even with `--allow-inferred`. The user must write an explicit recipe or upgrade the Add-in to provide structured fields.

## Apply Flow

`fix --apply` performs these steps:

1. Run the check runner.
2. Build the `FixPlan`.
3. If no actions exist, print `No fixable issues found.` and exit `0`.
4. Validate safety gates:
   - non-interactive apply requires `--yes`;
   - actions over `--max-changes` fail before snapshot;
   - inferred actions require `--allow-inferred`;
   - low-confidence actions cannot apply;
   - unknown or unsupported strategy fails before snapshot.
5. Unless `--no-snapshot` is passed, capture a snapshot to the baseline path.
6. Write a fix journal beside the baseline.
7. Apply actions through the same set endpoint used by `set`.
8. If an apply call fails, print the baseline path, journal path, and rollback command.
9. On success, print modified count and journal path.

`--no-snapshot`:

- should print a strong warning;
- should be blocked in non-interactive mode unless `--yes` is present;
- disables automatic rollback support for that run.

## Baseline and Journal

The baseline remains a normal `ModelSnapshot` file.

The fix journal records exactly what v1.5 changed and is the source of truth for rollback scope.

Suggested path when baseline is `.revitcli/fix-baseline-20260426T153000Z.json`:

```text
.revitcli/fix-baseline-20260426T153000Z.fixjournal.json
```

Suggested journal shape:

```json
{
  "schemaVersion": 1,
  "action": "fix",
  "checkName": "default",
  "profilePath": "D:\\project\\.revitcli.yml",
  "baselinePath": ".revitcli/fix-baseline-20260426T153000Z.json",
  "startedAt": "2026-04-26T15:30:00Z",
  "completedAt": "2026-04-26T15:30:05Z",
  "user": "Lenovo",
  "actions": [
    {
      "rule": "required-parameter",
      "strategy": "setParam",
      "elementId": 12345,
      "category": "doors",
      "parameter": "Mark",
      "oldValue": "",
      "newValue": "doors-12345",
      "inferred": false,
      "confidence": "high"
    }
  ]
}
```

The journal should be written before apply with planned actions and then finalized after apply. If apply fails midway, the journal still records planned and attempted actions so rollback can help recover the changed subset where preview data is available.

## Rollback Flow

`rollback <baseline>` performs these steps:

1. Resolve and read the baseline snapshot.
2. Resolve and read the sibling `.fixjournal.json`.
3. Validate journal schema and that it belongs to the provided baseline.
4. Convert journal actions into reverse parameter writes:
   - element id from journal;
   - parameter from journal;
   - value from `oldValue`.
5. Query or dry-run the current set operation to detect conflicts where possible.
6. Apply safety gates:
   - non-interactive apply requires `--yes`;
   - changes over `--max-changes` fail before write;
   - missing elements are reported and skipped;
   - current value different from journal `newValue` is a conflict and is not overwritten.
7. Apply reverse writes through `/api/elements/set`.
8. Print restored, skipped, and conflicted counts.

Rollback does not import the full snapshot. It restores only parameters that v1.5 fix journal says were changed by that fix run.

This is deliberately more conservative than a full snapshot import because it avoids overwriting unrelated user edits made after the fix.

## Output Contract

### Dry-run

Dry-run should print:

- check name;
- total issues considered;
- fixable action count;
- skipped issue count;
- inferred action count;
- a table of action rows with `rule`, `strategy`, `elementId`, `parameter`, `oldValue`, `newValue`, `confidence`;
- warnings for fallback/inference.

Example:

```text
Fix plan for check 'default': 3 action(s), 2 skipped, 1 inferred

  [setParam] required-parameter Element 12345 Mark: "" -> "doors-12345"
  [renameByPattern] naming Element 20001 Name: "Room Lobby" -> "Lobby"

Warning: 1 inferred action requires --allow-inferred before apply.
```

### Apply

Apply should print:

- baseline path;
- journal path;
- modified count;
- rollback command.

Example:

```text
Baseline saved: .revitcli/fix-baseline-20260426T153000Z.json
Journal saved: .revitcli/fix-baseline-20260426T153000Z.fixjournal.json
Modified 3 element parameter(s).
Rollback: revitcli rollback .revitcli/fix-baseline-20260426T153000Z.json --yes
```

## Error Paths

| Scenario | Behavior |
|---|---|
| Profile missing | Exit `1`; same guidance as `check`. |
| Check set missing | Exit `1`; list available check sets. |
| `fixes:` schema invalid | Exit `1`; identify the recipe and invalid field. |
| Unknown strategy | Exit `1`; list supported v1.5 strategies. |
| Regex compile failure | Exit `1`; include recipe index and regex error. |
| Revit not running | Exit `1`; suggest `revitcli doctor`. |
| Add-in unreachable | Exit `1`; suggest `revitcli doctor`. |
| Check API failure | Exit `1`; do not plan fixes. |
| Issue has no `elementId` | Skip with reason. |
| Issue has no matching recipe and cannot infer | Skip with reason. |
| Inferred action apply without `--allow-inferred` | Exit `1`; no snapshot and no write. |
| Low-confidence fallback action apply | Exit `1`; require explicit recipe or upgraded Add-in. |
| `--max-changes` exceeded | Exit `1`; no snapshot and no write. |
| Snapshot write fails | Exit `1`; no write. |
| Journal write fails | Exit `1`; no write. |
| Set endpoint fails | Exit `1`; print baseline and rollback command if available. |
| Rollback baseline missing | Exit `1`. |
| Rollback journal missing | Exit `1`; explain that this baseline is not a v1.5 fix baseline. |
| Rollback detects changed current value | Mark conflict; do not overwrite in v1.5. |
| Rollback element missing | Skip and report. |
| Rollback write fails | Exit `1`; print restored/skipped/conflicted counts so far. |

## Compatibility Strategy

### Existing CLI Commands

- `check` keeps existing default table output semantics.
- `check --output json` may include additional nullable fields.
- Existing `status`, `query`, `set`, `import`, `snapshot`, and `diff` command surfaces stay unchanged.
- Exit codes remain unchanged for existing commands.

### Add-in Compatibility

New v1.5 Add-in:

- emits structured `AuditIssue` fields where it can;
- keeps old fields populated.

Older Add-in:

- does not emit structured fields;
- lets `fix --dry-run` attempt best-effort inference;
- can apply only medium-confidence inferred actions when the user passes `--allow-inferred`;
- low-confidence message fallback remains dry-run only in v1.5.

This means v1.5 CLI can still talk to older Add-ins for read-only previews, but the reliable apply path is a v1.5 CLI plus v1.5 Add-in with structured issue metadata.

### Profile Compatibility

- Existing profiles without `fixes:` continue to load.
- `fix --dry-run` on a profile without fixes may still show inferred candidates.
- `fix --apply` on inferred candidates requires `--allow-inferred`.
- Starter profiles can include commented recipe examples, but should not silently enable broad writes.

## Architecture and File Impact

Likely new files:

```text
src/RevitCli/Fix/FixAction.cs
src/RevitCli/Fix/FixPlan.cs
src/RevitCli/Fix/FixPlanner.cs
src/RevitCli/Fix/FixRecipeMatcher.cs
src/RevitCli/Fix/FixTemplateRenderer.cs
src/RevitCli/Fix/FixJournal.cs
src/RevitCli/Fix/FixJournalStore.cs
src/RevitCli/Fix/Strategies/IFixStrategy.cs
src/RevitCli/Fix/Strategies/SetParamStrategy.cs
src/RevitCli/Fix/Strategies/RenameByPatternStrategy.cs
src/RevitCli/Commands/FixCommand.cs
src/RevitCli/Commands/RollbackCommand.cs
```

Likely changed files:

```text
shared/RevitCli.Shared/AuditResult.cs
src/RevitCli/Profile/ProjectProfile.cs
src/RevitCli/Profile/ProfileLoader.cs
src/RevitCli/Commands/CheckCommand.cs
src/RevitCli/Commands/CliCommandCatalog.cs
src/RevitCli/Commands/CompletionsCommand.cs
src/RevitCli/Commands/InteractiveCommand.cs
src/RevitCli/Client/RevitClient.cs
src/RevitCli.Addin/Services/RealRevitOperations.cs
src/RevitCli.Addin/Services/PlaceholderRevitOperations.cs
profiles/architectural-issue.yml
profiles/interior-room-data.yml
profiles/general-publish.yml
README.md
CHANGELOG.md
scripts/smoke-revit2026.ps1
```

The implementation plan should verify whether every listed file is truly needed before touching it.

## Performance Budget

| Operation | Budget |
|---|---|
| `fix --dry-run` planning overhead after check result | Under 500 ms for 1,000 issues. |
| Recipe matching | O(issue count * recipe count), acceptable for starter profiles. |
| Template rendering | O(action count), under 100 ms for 1,000 actions. |
| Snapshot before apply | Same budget as existing `snapshot`; may take tens of seconds on large models. |
| Apply | Dominated by set endpoint and Revit transactions; should not add extra model-wide scans. |
| Rollback planning | Under 500 ms for 1,000 journal actions, excluding Revit calls. |

Do not optimize storage with diff baselines in v1.5. Full snapshot plus small journal is easier to trust and debug.

## Testing Plan

### Pure CLI Unit Tests

- Profile loads without `fixes:`.
- Profile loads valid `setParam` recipe.
- Profile loads valid `renameByPattern` recipe.
- Profile rejects missing strategy.
- Profile rejects unsupported strategy.
- Profile rejects invalid regex.
- Matcher prefers `rule + category + parameter`.
- Matcher fails on ambiguous same-priority recipes.
- Template renderer supports `{element.id}`, `{category}`, `{parameter}`, `{currentValue}`, `{expectedValue}`.
- `setParam` skips missing element id.
- `setParam` skips empty rendered value.
- `renameByPattern` skips non-matching current value.
- `renameByPattern` skips unchanged replacement.
- Inferred action cannot apply without `--allow-inferred`.
- Low-confidence fallback cannot apply.
- `--max-changes` blocks before snapshot.
- Fix journal round-trips.
- Rollback conflicts when current value differs from journal `newValue`.

### Command Tests

- `fix --dry-run` prints plan and returns `0`.
- `fix --apply --yes` writes baseline before calling set.
- `fix --apply --yes --allow-inferred` allows inferred medium-confidence actions.
- `fix --apply --yes` blocks inferred actions.
- Snapshot failure prevents set calls.
- Journal failure prevents set calls.
- Set failure prints rollback guidance.
- `rollback <baseline> --dry-run` prints reverse actions.
- `rollback <baseline> --yes` calls set with old values.
- Missing baseline returns `1`.
- Missing journal returns `1`.

### Add-in Tests

- Required parameter issues include `category`, `parameter`, `currentValue`, and `source=structured`.
- Naming issues include `target` or `category`, `parameter` where applicable, `currentValue`, and `source=structured`.
- Existing old fields remain populated for all structured issues.

### Revit 2026 Smoke

The v1.5 smoke should run against a controlled model with at least one safe writable text parameter.

Required sequence:

```powershell
revitcli doctor
revitcli check
revitcli fix --dry-run
revitcli fix --apply --yes
revitcli check
revitcli rollback .revitcli/fix-baseline-<timestamp>.json --yes
revitcli check
```

Smoke report must record:

- Revit install path;
- model path;
- CLI version;
- Add-in version;
- profile path;
- commands;
- exit codes;
- key output;
- baseline path;
- journal path;
- rollback result.

## Acceptance Criteria

- `revitcli fix --dry-run` prints a plan for at least one starter-profile-supported parameter recipe.
- `fix --apply --yes` writes a baseline before modifying any parameter.
- `fix --apply --yes` refuses inferred actions unless `--allow-inferred` is present.
- Low-confidence message fallback actions remain dry-run only.
- `rollback <baseline> --yes` restores the parameters changed by that fix journal.
- `rollback` does not overwrite a parameter whose current value no longer matches the journal's `newValue`.
- Existing profiles without `fixes:` still pass current profile tests.
- Existing `check` JSON consumers still see `rule`, `severity`, `message`, and `elementId`.
- `purgeUnplaced` and `linkRoomToBoundary` are not implemented in v1.5 MVP.
- CLI unit tests and Add-in tests pass.
- Revit 2026 smoke records `check -> fix --dry-run -> fix --apply -> check -> rollback`.

## Open Risks

| Risk | Impact | Mitigation |
|---|---|---|
| Message fallback inference is wrong | Bad write if applied blindly | Require `--allow-inferred`; low confidence remains dry-run only. |
| Structured fields are incomplete for some Add-in rules | Planner skips more issues than users expect | Render skipped reasons; add fields incrementally per rule. |
| Snapshot is slow on large models | Apply feels delayed | Keep `--no-snapshot` as explicit advanced escape hatch. |
| Rollback journal is missing | Baseline alone cannot identify touched parameters | Fail clearly; do not attempt full snapshot import in v1.5. |
| Multiple recipes match one issue | Wrong recipe could write wrong parameter | Treat same-priority ambiguity as a planning failure. |
| Parameter names differ by Revit language | Recipes may not match localized models | Allow future parameter aliases; v1.5 documents exact parameter names. |

## Deferred Work

- `purgeUnplaced` delete strategy.
- `linkRoomToBoundary` relationship strategy.
- Parameter aliases for localization.
- `--force` rollback conflict override.
- Team-level playbook files under `.revitcli/playbooks/`.
- Rich HTML/JSON fix reports.
- Dashboard visualization of fix history.
