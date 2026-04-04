# RevitCli

Command-line interface for Autodesk Revit. Query elements, batch export, and modify parameters — all from your terminal.

> **Status: v0.1.0 — Revit 2026**
> All core commands are fully implemented with real Revit API integration. Verified on Windows + Revit 2026.

```bash
revitcli status
revitcli query walls --filter "height > 3000" --output json
revitcli export --format dwg --sheets "A1*" --output-dir ./exports
revitcli set doors --param "Fire Rating" --value "60min" --dry-run
revitcli audit --rules naming,room-bounds
```

## Architecture

```
CLI (revitcli.exe)  ──HTTP REST──>  Revit Add-in (embedded HTTP Server)
                                         │
                                    ExternalEvent Bridge
                                         │
                                    Revit API (main thread)
```

- **CLI** — Standalone .NET 8 console app
- **Revit Add-in** — EmbedIO HTTP server with ExternalEvent thread bridge for safe Revit API access

## Commands

| Command                          | Description                                                                       |
| -------------------------------- | --------------------------------------------------------------------------------- |
| `revitcli status`                | Show Revit version and active document                                            |
| `revitcli query <category>`      | Query elements with filters, output as table/JSON/CSV                             |
| `revitcli query --id <id>`       | Fetch a specific element by ID                                                    |
| `revitcli set <category>`        | Modify parameters with `--dry-run` preview and Transaction safety                 |
| `revitcli export --format <fmt>` | Export DWG, PDF, or IFC                                                           |
| `revitcli audit`                 | Run model quality checks (naming, room-bounds, level-consistency, unplaced-rooms) |
| `revitcli config show/set`       | View or modify CLI configuration                                                  |
| `revitcli doctor`                | Diagnose setup and connection issues                                              |
| `revitcli batch <file>`          | Execute commands from a JSON file                                                 |
| `revitcli completions <shell>`   | Generate shell completions (bash/zsh/PowerShell)                                  |
| `revitcli interactive` / `-i`    | Interactive REPL mode                                                             |

## Features

### Query

- Category-based collection with English + Chinese aliases (walls/墙, doors/门, etc.)
- Filter expressions: `name=Foo`, `height > 3000`, `type!=Default`
- Pseudo fields: `id`, `name`, `category`, `type`
- Parameter fields with numeric unit conversion
- Duplicate parameter disambiguation via `[N]` suffix
- Output formats: table (colored), JSON (scriptable), CSV

### Set

- Target by category+filter or single `--id`
- `--dry-run` previews old/new values with full type validation
- Type coercion: String, Integer, Double (with unit conversion), ElementId
- All-or-nothing Transaction — fails entirely if any element can't be modified
- Duplicate parameter disambiguation via `[N]` suffix

### Export

- **DWG** — Per-view/sheet export with wildcard matching (`A1*`, `all`)
- **PDF** — Combined PDF output
- **IFC** — Whole-model export (IFC2x3)
- `--sheets` and `--views` options for target selection

### Audit

- `naming` — Detect default view names ("Section 1", "3D View 2")
- `room-bounds` — Find rooms with zero area (not enclosed)
- `level-consistency` — Detect duplicate elevations
- `unplaced-rooms` — Find rooms not placed in model

## Requirements

- .NET 8 SDK
- Autodesk Revit 2026 (for the add-in)
- Windows (Revit is Windows-only)

## Build

```bash
dotnet build
dotnet test
```

## Install

### CLI

```bash
dotnet tool install --global RevitCli
```

Or build from source:

```bash
dotnet publish src/RevitCli -c Release -o ./publish
```

### Revit Add-in

1. Build: `dotnet publish src/RevitCli.Addin -c Release`
2. Close Revit
3. Copy all files from `src/RevitCli.Addin/bin/Release/net8.0-windows/publish/` to `%APPDATA%\Autodesk\Revit\Addins\2026\`
4. Start Revit and open a project
5. Verify: `revitcli doctor`

## Configuration

```bash
revitcli config show                          # View current settings
revitcli config set serverUrl http://localhost:9999
revitcli config set defaultOutput json
revitcli config set exportDir ./my-exports
```

## Shell Completions

```bash
# Bash
revitcli completions bash >> ~/.bashrc

# Zsh
revitcli completions zsh >> ~/.zshrc

# PowerShell
revitcli completions powershell >> $PROFILE
```

## Project Structure

```
src/RevitCli/              # CLI console app (net8.0)
src/RevitCli.Addin/        # Revit add-in with HTTP server + ExternalEvent bridge
shared/RevitCli.Shared/    # Shared DTOs and interface (netstandard2.0)
tests/RevitCli.Tests/      # CLI tests (86 tests)
tests/RevitCli.Addin.Tests/ # Add-in + protocol tests
```

## Roadmap

- [ ] One-click add-in installer
- [ ] Revit 2024/2025 compatibility (net48 multi-target)
- [ ] Capability/version negotiation between CLI and add-in
- [ ] Async export with real progress tracking
- [ ] More audit rules (clash detection, unused families)

## Publishing

1. Update version in `src/RevitCli/RevitCli.csproj`
2. Tag and push: `git tag v0.1.0 && git push origin v0.1.0`
3. GitHub Actions auto-publishes to NuGet.org

> Requires `NUGET_API_KEY` secret in repository settings.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md).

## License

MIT
