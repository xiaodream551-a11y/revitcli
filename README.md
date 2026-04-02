# RevitCli

Command-line interface for Autodesk Revit. Query elements, batch export, and modify parameters — all from your terminal.

> **Status: Early Development (v0.1.0-alpha)**
> CLI skeleton is complete; add-in protocol scaffold exists, but Revit lifecycle and thread integration are unfinished. All add-in handlers currently return placeholder data. See [Roadmap](#roadmap) for next steps.

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
                                    Revit API (pending)
```

- **CLI** — Standalone .NET 8 console app, fully functional
- **Revit Add-in** — EmbedIO HTTP server with thread bridge, placeholder Revit API handlers

## Commands

| Command | CLI Ready | Revit API |
|---------|-----------|-----------|
| `revitcli status` | Yes | Placeholder |
| `revitcli query` | Yes | Placeholder |
| `revitcli export` | Yes | Placeholder |
| `revitcli set` | Yes | Placeholder |
| `revitcli audit` | Yes | Placeholder |
| `revitcli config show/set` | Yes | N/A |
| `revitcli doctor` | Yes | Tests add-in connection |
| `revitcli batch <file>` | Yes | Depends on subcommands |
| `revitcli completions <shell>` | Yes | N/A |
| `revitcli interactive` / `-i` | Yes | Depends on invoked commands |

"Placeholder" means the CLI command works end-to-end, but the add-in returns hardcoded test data instead of real Revit model data.

## What Works Today

- All 10 CLI commands parse arguments, validate input, and handle errors correctly
- `--version`, `--verbose` global options
- Pipe-friendly output (auto TTY detection)
- Configuration system (`~/.revitcli/config.json`)
- Port discovery (`~/.revitcli/server.json` with PID validation)
- Shell completions (bash/zsh/PowerShell, partial — `doctor` and `interactive` not yet included)
- Interactive REPL mode
- Batch execution from JSON files
- Non-zero exit codes on errors
- 58 unit and integration tests
- GitHub Actions CI + NuGet publish workflow

## What Doesn't Work Yet

- **Real Revit API calls** — All add-in handlers return placeholder data
- **IExternalApplication** — Add-in entry point not wired to Revit lifecycle
- **ExternalEvent bridge** — Thread marshalling runs synchronously (no real Revit main thread)
- **Multi-target build** — Add-in targets `net8.0` only, needs `net48` for Revit 2024/2025
- **Export** — Returns instant "completed" without actually exporting anything

## Requirements

- .NET 8 SDK
- Autodesk Revit 2025 (for the add-in, when Revit API is integrated)

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

### Revit Add-in (not yet functional)

> The add-in currently returns placeholder data. Real Revit integration is pending.

1. Build: `dotnet publish src/RevitCli.Addin -c Release`
2. Copy `RevitCli.Addin.dll` and `RevitCli.addin` to `%APPDATA%\Autodesk\Revit\Addins\2025\`
3. Restart Revit

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

## Roadmap

See [docs/superpowers/plans/2026-04-02-revitcli.md](docs/superpowers/plans/2026-04-02-revitcli.md) for the implementation plan.

**Next milestone: v0.1.0 (Real Revit Integration)**

1. Wire add-in to `IExternalApplication` + `ExternalEvent` (requires Windows + Revit 2025)
2. Real `status` — return actual Revit version and document info
3. Real `query --id` — fetch real elements
4. Real `query <category> --filter` — filtered element collection
5. Real `set --dry-run` then `set` — parameter modification with transactions

## Project Structure

```
src/RevitCli/              # CLI console app (net8.0)
src/RevitCli/Config/       # Configuration management
src/RevitCli.Addin/        # Revit add-in with embedded HTTP server
shared/RevitCli.Shared/    # Shared DTOs (netstandard2.0)
tests/RevitCli.Tests/      # CLI unit tests (44 tests)
tests/RevitCli.Addin.Tests/ # Add-in + integration tests (14 tests, including 6 e2e)
```

## Publishing

1. Update version in `src/RevitCli/RevitCli.csproj`
2. Tag and push: `git tag v0.1.0 && git push origin v0.1.0`
3. GitHub Actions auto-publishes to NuGet.org

> Requires `NUGET_API_KEY` secret in repository settings.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md).

## License

MIT
