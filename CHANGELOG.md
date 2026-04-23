# Changelog

All notable changes to RevitCli will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/), and this project adheres to [Semantic Versioning](https://semver.org/).

## [1.1.0] - 2026-04-23

Model-as-Code Phase 1 — snapshot + diff infrastructure. This lays the
foundation for incremental publish (v1.2) and CSV import (v1.3).

### Added

- **`revitcli snapshot`** — capture the model's semantic state as JSON:
  elements grouped by category, sheets, schedules, and a counts summary.
  Options: `--output FILE`, `--categories LIST`, `--no-sheets`,
  `--no-schedules`, `--summary-only` (fast path, counts only).
- **`revitcli diff FROM TO`** — diff two snapshot JSONs. Formats:
  `table` (default), `json`, `markdown`. Options: `--report FILE`
  (format inferred from `.md`/`.json` extension), `--categories LIST`,
  `--max-rows N`. Errors on schema version mismatch; warns on
  DocumentPath mismatch.
- **Shared DTOs** (`shared/RevitCli.Shared/`):
  `ModelSnapshot`, `SnapshotRequest`, `SnapshotDiff` with every property
  carrying `[JsonPropertyName("camelCase")]` attributes for stable wire
  format.
- **`SnapshotHasher`** — pure static helper producing stable 16-char
  SHA256 hashes for elements, sheet metadata, and schedules. Sorts
  parameter keys Ordinally and escapes `\n`, `\`, `|` in values to
  prevent collisions.
- **`SnapshotDiffer`** — pure C# diff algorithm. Elements keyed by Id
  within each category, sheets by Number, schedules by Id. Defensively
  dedupes via `GroupBy().First()` so malformed input produces a clean
  diff instead of a raw `ArgumentException`.
- **`DiffRenderer`** — renders diffs as table / json / markdown with
  truncation at `--max-rows` and pipe-escape protection for Markdown
  table cells.
- **Addin endpoint** — `POST /api/snapshot` via `SnapshotController`
  wired into `RealRevitOperations.CaptureSnapshotAsync` (runs on the
  Revit main thread through `RevitBridge`). Full Revit API traversal:
  `FilteredElementCollector.OfCategory(...)` for elements,
  `OfClass(typeof(ViewSheet))` for sheets,
  `OfClass(typeof(ViewSchedule))` for schedules with body-cell hash.
- **48 new tests** across shared/CLI layers: DTO roundtrip, hash
  stability, diff algorithm edge cases, renderer formatting, CLI
  commands, and HTTP client wiring.

### Deferred to Phase 2 (v1.2)

- `SnapshotSheet.ContentHash` — view-level element aggregation for
  "sheet really changed?" detection. Currently left as empty string.
- `revitcli publish --since SNAPSHOT` — incremental re-export of only
  sheets whose content changed since the baseline.
- Profile `publish.incremental: true` flag + baseline auto-management.

### Known Carry-forward

- Addin test project (`tests/RevitCli.Addin.Tests/`) has pre-existing
  compile errors against Revit 2026 API (`UIApplication` reference
  missing in test csproj). Not caused by this release; flagged for a
  separate `chore: restore addin tests` commit.
- `--verbose` flag on the `snapshot` command is specified in the design
  doc but not yet implemented.

Spec: [docs/superpowers/specs/2026-04-23-model-as-code-design.md](docs/superpowers/specs/2026-04-23-model-as-code-design.md)
Plan: [docs/superpowers/plans/2026-04-23-snapshot-and-diff.md](docs/superpowers/plans/2026-04-23-snapshot-and-diff.md)

## [1.0.0] - 2026-04-04

RevitCli v1.0 — production-ready local BIMOps runner for Revit teams.

### Added

- **`revitcli init`** — interactive profile creation from 3 starter templates
- **`revitcli score`** — model health score 0-100 with weighted rules and letter grade
- **`revitcli coverage`** — parameter fill rates by category with visual bar chart
- **Publish receipts** — `.revitcli/receipts/<pipeline>-<timestamp>.json` with profile hash, user, machine
- **15 commands total**: status, query, set, audit, export, check, publish, init, score, coverage, config, doctor, batch, completions, interactive

### Changed

- PDF export: no sheets + no views → actionable error instead of exporting all views
- Starter profiles embedded in CLI package (init works from installed tool)
- Publish receipt includes profileHash for auto-discovered profiles

### Production Readiness

- 122 tests (contract tests, integration tests, validation tests)
- 10 built-in audit rules + 2 profile-driven checks
- Documentation website with 5 pages
- 3 starter profiles (architectural, interior, general)
- Supports Revit 2024/2025/2026
- Install scripts, GitHub Actions release workflow
- Output contracts locked (JSON, HTML, exit codes, webhook payload)

## [0.5.0] - 2026-04-04

### Added

- **Documentation website** (GitHub Pages, dark theme)
  - Quick Start guide, 3 core workflows, profile reference, troubleshooting
  - Live at https://xiaodream551-a11y.github.io/revitcli/

- **3 starter profiles** (`profiles/`)
  - `architectural-issue.yml` — room data, sheet completeness, pre-issue gate
  - `interior-room-data.yml` — room metadata, naming, FM handover
  - `general-publish.yml` — health checks, DWG/PDF/IFC pipelines

- **Doctor onboarding** — quickstart guidance when no profile detected
- **34 new tests** (86 → 122) — check command, journal, diff engine, output contracts, profile validation

### Changed

- Profile inheritance: `defaults.Notify` now properly inherited from parent
- Error messages are actionable (suggest fixes, list available options)
- `set --ids-from <file>` for Windows pipe workaround
- Malformed `--ids-from` input rejected with clear error (not silent partial)
- Publish suggests 'revitcli doctor' on connection failure

### Fixed

- Placeholder contract tests replaced with real assertions
- ParseIds tests exercise actual parser path

## [0.4.0] - 2026-04-04

### Added

- **Pipe composability**
  - `set --stdin`: read element IDs from stdin (JSON array or one per line)
  - `set --ids-from <file>`: read element IDs from JSON file (Windows-friendly)
  - `SetRequest.ElementIds`: batch element ID list for pipe workflows
  - Strict parsing with clear error messages on malformed input

- **Webhook notifications**
  - `defaults.notify` in `.revitcli.yml`: HTTPS URL to POST results
  - Fires after `check` and `publish` with status, counts, timestamp
  - HTTPS-only for security, non-blocking (errors to stderr)

- **Operation journal**
  - `.revitcli/journal.jsonl`: append-only log of `set` and `publish` operations
  - Records: action, parameters, affected count, user, timestamp
  - Both interactive and non-interactive paths log consistently

### Known Issues

- `--stdin` pipe does not work reliably with PowerShell + `dotnet run`; use `--ids-from` instead
- `--ids-from` has a serialization issue with the add-in API; use category+filter or --id for now

## [0.3.0] - 2026-04-04

### Added

- **6 new audit rules** (total: 10 built-in + 2 profile-driven)
  - `views-not-on-sheets` — printable views/schedules not placed on sheets
  - `imported-dwg` — imported (not linked) CAD files
  - `in-place-families` — in-place families that should be loadable
  - `duplicate-room-numbers` — rooms sharing the same number
  - `room-metadata` — rooms missing number or using default name
  - `sheets-missing-info` — sheets with no number or empty content

- **Check result diff engine**
  - Auto-saves results to `.revitcli/results/` after each check run
  - Compares against previous run: reports new, resolved, unchanged issues
  - `--no-save` flag to skip storage (used by publish precheck)

### Changed

- **Naming rule rewritten**: replaced broad regex with prefix-based detection
  using known Revit default view prefixes (English + Chinese + German + French).
  System names like "标高 1" / "Level 1" are now whitelisted and no longer flagged.
- `views-not-on-sheets` and `sheets-missing-info` account for `ScheduleSheetInstance`
  placements (schedules on sheets are not false positives)
- `audit --list` output generated from rule registry (no more stale hardcoded strings)
- Diff output only appears in table format (JSON/HTML remain clean for CI parsers)
- Result storage wrapped in try/catch (I/O failures don't break audit output)

## [0.2.0] - 2026-04-04

### Added

- **Project Profiles** (`.revitcli.yml`)
  - Named check sets with audit rules, required parameter checks, naming patterns
  - Named export presets (format, sheets/views, output directory)
  - Named publish pipelines with precheck gates
  - Single-parent inheritance via `extends` with cycle detection
  - Validation of `failOn` and `severity` values at load time

- **`revitcli check`** — Run project checks from profile
  - Sends all checks in single request to add-in (fast batch execution)
  - `--output table|json|html` for different consumers
  - `--report <file>` to save reports (format inferred from extension)
  - Exit code based on `failOn` (error/warning) for CI gating
  - Suppression/waiver system: by rule, category, parameter, element IDs, with expiry dates

- **`revitcli publish`** — Run export pipelines from profile
  - Optional precheck gate (runs a check set first)
  - Sequential export preset execution
  - `--dry-run` support
  - Output paths resolved relative to profile file

- **Check Report Renderer**
  - Table (plain text), JSON (CI), HTML (dark mode with summary cards)
  - All formats display suppressed issue count

- **Server-side audit extensions**
  - `required-parameter`: batch check per category with duplicate-aware parameter scan
  - `naming-pattern`: custom regex patterns for views, sheets, or any category

- **Multi-version Revit support**
  - Dual TFM: `net48` (Revit 2024) + `net8.0-windows` (Revit 2025/2026)
  - `RevitYear` build parameter with per-year output directories
  - Element IDs widened from `int` to `long` (64-bit ElementId since Revit 2024)

- **Capability/version model**
  - `status` reports `revitYear`, `addinVersion`, and `capabilities` list
  - CLI displays add-in version and capabilities

- **Installer**
  - `install.ps1`: auto-detects Revit years, per-year add-in deployment, PATH setup
  - `uninstall.ps1`: multi-year manifest removal, optional `-Purge`
  - `release.yml`: GitHub Actions builds per Revit year, packages ZIP with checksum

- **`doctor`** now displays detected `.revitcli.yml` profile info

### Changed

- Release notes derive supported Revit years from actual build outcomes
- Profile inheritance documented as full-object replacement (not deep merge)

## [0.1.0] - 2026-04-04

### Added

- **Real Revit API Integration (Revit 2026)**
  - `status` — Returns actual Revit version and active document info
  - `query --id` — Fetch real elements by ElementId
  - `query <category> --filter` — Category collection with typed filter matching, unit conversion, duplicate parameter handling
  - `set` — Parameter modification with Transaction safety, type coercion (String/Integer/Double/ElementId), `--dry-run` with full validation, all-or-nothing semantics
  - `audit` — 4 real rules: `naming`, `room-bounds`, `level-consistency`, `unplaced-rooms`
  - `export` — DWG/PDF per-view/sheet export with wildcard matching, IFC whole-model export

- **Add-in Architecture**
  - `IExternalApplication` + `ExternalEvent` bridge for safe main-thread access
  - `RealRevitOperations` with real Revit API calls
  - Exception mapping in controllers (ArgumentException->400, InvalidOperationException->409)
  - Transaction commit status verification
  - Export return value checking

- **CLI Enhancements**
  - `--views` option for export command
  - Category aliases in English + Chinese (walls/墙, doors/门, etc.)
  - Duplicate parameter disambiguation via `[N]` suffix in both query and set

### Changed

- Removed `clash` audit rule (placeholder; deferred to future release)
- Controllers now return structured JSON errors instead of HTTP 500

## [0.1.0-alpha] - 2026-04-02

### Added

- **CLI Commands (10)**
  - `revitcli status` — Check Revit plugin connection status
  - `revitcli query` — Query elements by category, filter, or ID with table/JSON/CSV output
  - `revitcli export` — Batch export sheets as DWG, PDF, or IFC with progress bar
  - `revitcli set` — Modify element parameters with `--dry-run` preview
  - `revitcli audit` — Run model checking rules
  - `revitcli config` — View and modify CLI configuration (`config show` / `config set`)
  - `revitcli doctor` — Diagnose setup issues and connection problems
  - `revitcli completions` — Generate shell completion scripts (bash/zsh/PowerShell)
  - `revitcli interactive` — Interactive REPL mode (`-i` shortcut)
  - `revitcli batch` — Execute commands from a JSON file

- **Revit Add-in**
  - Embedded HTTP server (EmbedIO) with REST API
  - `IRevitOperations` interface separating business logic from HTTP handlers
  - Port fallback mechanism (tries 10 ports if default is occupied)
  - Server discovery via `~/.revitcli/server.json` with PID validation

- **Developer Experience**
  - Spectre.Console colored output with automatic TTY detection
  - Pipe-friendly plain text fallback when stdout is redirected
  - `--version` and `--verbose` global options
  - Configuration system (`~/.revitcli/config.json`)
  - Non-zero exit codes on command errors
  - Packaged as .NET global tool (`dotnet tool install --global RevitCli`)
  - GitHub Actions CI (build + test on push/PR)
  - NuGet auto-publish workflow on tag push
  - 86 unit, integration, and protocol tests
