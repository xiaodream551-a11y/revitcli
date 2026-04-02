# Changelog

All notable changes to RevitCli will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/), and this project adheres to [Semantic Versioning](https://semver.org/).

## [0.1.0] - 2026-04-02

### Added

- **CLI Commands**
  - `revitcli status` — Check Revit plugin connection status
  - `revitcli query` — Query elements by category, filter, or ID with table/JSON/CSV output
  - `revitcli export` — Batch export sheets as DWG, PDF, or IFC with progress bar
  - `revitcli set` — Modify element parameters with `--dry-run` preview
  - `revitcli audit` — Run model checking rules (naming, clash, room-bounds, level-consistency, unplaced-rooms)
  - `revitcli config` — View and modify CLI configuration (`config show` / `config set`)
  - `revitcli doctor` — Diagnose setup issues and connection problems
  - `revitcli completions` — Generate shell completion scripts (bash/zsh/PowerShell)
  - `revitcli interactive` — Interactive REPL mode (`-i` shortcut)

- **Revit Add-in**
  - Embedded HTTP server (EmbedIO) with REST API
  - Thread bridge for Revit main thread marshalling via ExternalEvent
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
  - 54 unit and integration tests

### Architecture

- Client-Server model: CLI ↔ HTTP REST ↔ Revit Add-in ↔ Revit API
- Three .NET projects: CLI (net8.0), Add-in (net8.0), Shared DTOs (netstandard2.0)
- Placeholder Revit API handlers (real implementation requires Windows + Revit)
