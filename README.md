# RevitCli

Command-line interface for Autodesk Revit. Query elements, batch export, and modify parameters — all from your terminal.

```bash
revitcli status
revitcli query walls --filter "height > 3000" --output json
revitcli export --format dwg --sheets "A1*" --output-dir ./exports
revitcli set doors --param "Fire Rating" --value "60min" --dry-run
```

## Demo

### Check connection status

```
$ revitcli status
╭─────────────────┬──────────────────╮
│ Property        │ Value            │
├─────────────────┼──────────────────┤
│ Revit Version   │ 2025             │
│ Document        │ MyProject.rvt    │
│ Path            │ C:\Projects\...  │
╰─────────────────┴──────────────────╯
```

### Query elements

```
$ revitcli query walls --filter "height > 3000"
╭──────┬─────────────┬───────┬──────────────────┬────────╮
│   Id │ Name        │ Cat.  │ Type             │ Height │
├──────┼─────────────┼───────┼──────────────────┼────────┤
│  201 │ Wall 1      │ Walls │ Generic - 200mm  │ 3600   │
│  305 │ Wall 2      │ Walls │ Generic - 300mm  │ 4200   │
╰──────┴─────────────┴───────┴──────────────────┴────────╯
(2 element(s))
```

```
$ revitcli query doors --id 1024 --output json
[
  {
    "id": 1024,
    "name": "Door 1",
    "category": "Doors",
    "typeName": "Single-Flush 900x2100mm",
    "parameters": {
      "Fire Rating": "60min",
      "Mark": "D-01"
    }
  }
]
```

### Batch export

```
$ revitcli export --format pdf --sheets "A1*" --output-dir ./exports
Export started. Task ID: a3f8c012
Progress: 33%
Progress: 66%
Export completed.
```

### Modify parameters (with dry-run preview)

```
$ revitcli set doors --param "Fire Rating" --value "90min" --dry-run
Dry run: 12 element(s) would be modified.
╭──────┬──────────┬───────────┬───────────╮
│ Id   │ Name     │ Old Value │ New Value │
├──────┼──────────┼───────────┼───────────┤
│  401 │ Door 1   │ 60min     │ 90min     │
│  402 │ Door 2   │ 60min     │ 90min     │
│  ... │ ...      │ ...       │ ...       │
╰──────┴──────────┴───────────┴───────────╯
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
tests/                     # Unit tests (32 tests)
```

## License

MIT
