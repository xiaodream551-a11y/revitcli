# RevitCli

Command-line interface for Autodesk Revit. Query elements, batch export, and modify parameters — all from your terminal.

```bash
revitcli status
revitcli query walls --filter "height > 3000" --output json
revitcli export --format dwg --sheets "A1*" --output-dir ./exports
revitcli set doors --param "Fire Rating" --value "60min" --dry-run
```

## Architecture

```
CLI (revitcli.exe)  ──HTTP REST──>  Revit Add-in (embedded HTTP Server)
                                         │
                                    Revit API
```

RevitCli consists of two components:

- **CLI** — A standalone .NET 8 console app that sends commands
- **Revit Add-in** — A plugin that runs inside Revit, embedding an HTTP server (EmbedIO) to receive and execute commands via the Revit API

## Features (MVP)

| Command | Description |
|---------|-------------|
| `revitcli status` | Check if Revit plugin is online |
| `revitcli query <category>` | Query elements with optional `--filter`, `--id`, `--output` (table/json/csv) |
| `revitcli export --format <fmt>` | Batch export sheets (DWG, PDF, IFC) |
| `revitcli set <category> --param <name> --value <val>` | Modify parameters with `--dry-run` preview |

## Requirements

- .NET 8 SDK
- Autodesk Revit 2024, 2025, or 2026 (for the add-in)

## Build

```bash
dotnet build
dotnet test
```

## Install

### CLI

```bash
dotnet publish src/RevitCli -c Release -o ./publish
```

Add the `publish` directory to your PATH.

### Revit Add-in

1. Build the add-in: `dotnet publish src/RevitCli.Addin -c Release`
2. Copy `RevitCli.Addin.dll` and `RevitCli.addin` to your Revit add-ins folder:
   - `%APPDATA%\Autodesk\Revit\Addins\<version>\`
3. Restart Revit

## Project Structure

```
src/RevitCli/              # CLI console app
src/RevitCli.Addin/        # Revit add-in with embedded HTTP server
shared/RevitCli.Shared/    # Shared DTOs
tests/                     # Unit tests (24 tests)
```

## License

MIT
