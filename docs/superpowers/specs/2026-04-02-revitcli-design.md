# RevitCli Design Spec

## Overview

RevitCli is an open-source CLI tool that brings Revit operations to the command line. It communicates with a Revit add-in via HTTP REST to enable model querying, batch export, and parameter modification — all from the terminal.

**License:** MIT
**Target Revit versions:** 2024, 2025, 2026

## Architecture

Classic Client-Server model:

```
CLI (revitcli.exe)  ──HTTP REST──>  Revit Add-in (embedded HTTP Server)
                                         │
                                    Revit API
```

- CLI is a standalone .NET console application
- Revit add-in embeds a lightweight HTTP server (EmbedIO)
- Communication over `localhost:17839` (configurable)
- All Revit API calls are marshalled to the main thread via `ExternalEvent`

## Project Structure

```
revitcli/
├── src/
│   ├── RevitCli/              # CLI console app
│   │   ├── Commands/          # Command implementations (query, export, set)
│   │   ├── Client/            # HTTP client for plugin communication
│   │   └── Program.cs         # Entry point
│   │
│   └── RevitCli.Addin/        # Revit add-in (.NET Class Library)
│       ├── Server/            # Embedded HTTP server
│       ├── Handlers/          # API endpoint handlers
│       └── RevitCliApp.cs     # Plugin entry (IExternalApplication)
│
├── shared/
│   └── RevitCli.Shared/       # Shared models/DTOs
│
├── tests/
│   ├── RevitCli.Tests/
│   └── RevitCli.Addin.Tests/
│
├── revitcli.sln
├── LICENSE
└── README.md
```

Three projects: CLI, Add-in, Shared library. The shared library holds DTOs used by both sides.

## CLI Commands

### Status

```bash
revitcli status                    # Check if Revit plugin is online
```

### Query

```bash
revitcli query <category>          # Query all elements of a category
revitcli query walls               # All walls
revitcli query walls --filter "height > 3000"
revitcli query doors --filter "Fire Rating = 60min"
revitcli query --id 12345          # By ElementId
revitcli query walls --output json # Output format (table/json/csv)
```

### Export

```bash
revitcli export --format dwg       # Export current view as DWG
revitcli export --format pdf --sheets "A1*"
revitcli export --format dwg --sheets all --output-dir ./exports
revitcli export --format ifc       # Export IFC
```

Supported formats: DWG, PDF, IFC.

### Set

```bash
revitcli set <category> --param <name> --value <value>
revitcli set doors --param "Fire Rating" --value "60min"
revitcli set walls --filter "height > 3000" --param "Comments" --value "Tall wall"
revitcli set --id 12345 --param "Mark" --value "W-01"
revitcli set ... --dry-run         # Preview changes without applying
```

### Design Decisions

- `--filter` uses simple expressions (no complex query language for MVP)
- `--dry-run` is mandatory for safe modification workflows
- `--output` defaults to table (human-readable), supports json/csv (scriptable)

## API Design

Base URL: `http://localhost:17839`

### Endpoints

```
GET  /api/status                          # Health check + Revit version info

GET  /api/elements?category=walls         # Query elements
GET  /api/elements?category=doors&filter=Fire%20Rating%20%3D%2060min
GET  /api/elements/12345                  # Query by ID

POST /api/export                          # Export
     { "format": "dwg", "sheets": ["A1*"], "outputDir": "C:/exports" }

POST /api/elements/set                    # Modify parameters
     { "category": "doors", "filter": "...", "param": "Fire Rating", "value": "60min", "dryRun": true }
```

### Response Format

All responses follow a unified format:

```json
{ "success": true, "data": { ... }, "error": null }
```

### Long-Running Operations

Export and other long-running operations return a task ID:

```
POST /api/export → { "taskId": "abc123" }
GET  /api/tasks/abc123 → { "status": "running", "progress": 65 }
```

## Plugin Architecture

```
Revit startup → Load RevitCliApp (IExternalApplication)
                    │
                    ├── Start HTTP Server (background thread)
                    │
                    └── Register ExternalEvent (main thread callback)

Request flow:
HTTP Request → Server receives (background thread)
    → Create task, raise ExternalEvent
    → Revit main thread executes API call
    → Result written to TaskCompletionSource
    → HTTP Response returned
```

### Key Implementation Details

- HTTP server: `EmbedIO` (lightweight, embeddable, MIT license)
- Thread bridging: `ExternalEvent` + `IExternalEventHandler` (official Revit pattern)
- Lifecycle: auto-start on Revit launch, auto-stop on Revit close
- Multi-version: conditional compilation or runtime detection for API differences across 2024/2025/2026

## Error Handling

| Scenario | Handling |
|----------|----------|
| Revit not running | Friendly message: "Revit is not running or plugin is not loaded" with startup guidance |
| No document open | Return error "No document open"; `status` shows connected but no project |
| Filter matches nothing | Return empty result, CLI shows "No elements matched" |
| Set affects many elements | Without `--dry-run`, CLI prompts: "This will modify 328 elements. Continue? [y/N]" |
| Export path doesn't exist | Plugin auto-creates directory |
| Revit busy with transaction | Request queued, timeout returns busy error |
| Port in use | Plugin tries fallback port, writes actual port to local config for CLI discovery |

## Tech Stack

| Component | Technology |
|-----------|-----------|
| CLI framework | `System.CommandLine` |
| CLI table output | `Spectre.Console` |
| HTTP client | `HttpClient` (.NET built-in) |
| Embedded HTTP server | `EmbedIO` |
| JSON serialization | `System.Text.Json` |
| Target framework | `net48` (Revit 2024/2025), `net8.0-windows` (Revit 2026) |
| Shared library | `netstandard2.0` (compatible with both) |
| Testing | `xUnit` + `Moq` |
| Packaging | `dotnet publish` single-file + `.addin` manifest |
| CI | GitHub Actions |
| License | MIT |
