# Changelog

All notable changes to RevitCli will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/), and this project adheres to [Semantic Versioning](https://semver.org/).

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
