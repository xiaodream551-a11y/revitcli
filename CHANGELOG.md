# Changelog

All notable changes to RevitCli will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/), and this project adheres to [Semantic Versioning](https://semver.org/).

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
