# Revit 2026 Install and Version Consistency Design

## Summary

This phase makes RevitCli's Revit 2026 installation state observable and repeatable. The goal is not to add commands or expand feature scope. The goal is to make it clear whether the CLI is talking to the Add-in built from the same project version, whether the manifest points at the expected assembly, and whether a live Revit session is using stale installed bits.

The work builds on the existing Revit 2026 smoke slice:

`doctor -> status -> query --id -> query <category> --filter -> set --dry-run -> set`

The next baseline adds an installation and version gate before that smoke can be trusted.

## Goals

- Make `doctor` report CLI version, manifest path, manifest assembly path, installed Add-in assembly version, live Add-in version, and Revit 2026 API path.
- Fail `doctor` when the CLI and installed or live Add-in differ by major or minor version.
- Warn, not fail, when only patch/build metadata differs.
- Keep `status` concise while ensuring the Add-in version comes from the currently loaded Add-in assembly.
- Keep Revit 2026 as the only target for this phase.
- Support explicit D-drive Revit installs through `-p:RevitInstallDir="D:\revit2026\Revit 2026"` and `REVITCLI_REVIT2026_INSTALL_DIR`.
- Keep tests from writing or deleting the real user `C:\Users\Lenovo\.revitcli\server.json`.

## Non-Goals

- No new public commands.
- No DTO schema changes.
- No expansion into `snapshot`, `diff`, `publish`, `import`, `export`, or `audit`.
- No attempt to make 2024/2025 equally verified in this phase.
- No automatic mutation of a running Revit session.

## Architecture

`doctor` becomes the installation and runtime truth surface. It should inspect local files before relying on a live Revit connection, so users can diagnose an install even while Revit is closed.

Responsibilities:

- CLI startup exposes its own informational version.
- Add-in `status` exposes the version of the currently loaded Add-in assembly.
- `doctor` reads the Revit 2026 Add-in manifest under `%APPDATA%\Autodesk\Revit\Addins\2026\RevitCli.addin`.
- `doctor` resolves the manifest `<Assembly>` path and reads the assembly version from that file.
- `doctor` reads and validates `server.json`: port, pid, token, process name, and Revit year.
- `scripts/install.ps1` remains the installation entry point and should install the current Revit 2026 publish output to the Revit 2026 Addins directory.

Existing 2024/2025 project structure should not be removed. This design only makes the Revit 2026 path the verified path.

## Data Flow

1. Build/publish the Add-in:

   ```powershell
   dotnet publish src\RevitCli.Addin -p:RevitYear=2026 -p:RevitInstallDir="D:\revit2026\Revit 2026"
   ```

2. Install the Add-in:

   ```powershell
   scripts\install.ps1
   ```

3. The installer copies the Add-in publish output to:

   `%APPDATA%\Autodesk\Revit\Addins\2026\`

4. The installer writes or updates `RevitCli.addin` so `<Assembly>` points at the installed `RevitCli.Addin.dll`.

5. Revit starts, loads the Add-in, and the Add-in writes:

   `C:\Users\Lenovo\.revitcli\server.json`

6. CLI discovers `server.json`, connects to `/api/status`, and receives the live Add-in version.

7. `doctor` compares:

   - CLI version
   - manifest assembly file version
   - live Add-in version from `/api/status`
   - server info Revit year and process identity

## Version Policy

Version comparisons use major and minor as the compatibility boundary.

- Same major and minor: pass.
- Different patch: warning.
- Different build metadata or informational suffix: warning.
- Different major or minor: failure.
- Missing or unparsable version: failure for installed assembly, failure for live Add-in when Revit is reachable.

Example:

- CLI `1.3.0`, Add-in `1.3.1`: warn.
- CLI `1.3.0`, Add-in `1.0.0`: fail.
- CLI `1.3.0+local`, Add-in `1.3.0`: warn at most.

## Error Handling

`doctor` keeps exit codes simple: `0` for success and `1` for any failure. It should still print all checks it can complete before returning failure.

Failure cases:

- Revit 2026 API DLLs missing at the resolved install directory.
- Add-in manifest missing.
- Add-in manifest XML cannot be parsed.
- Manifest has no `<Assembly>`.
- Manifest assembly path does not exist.
- Installed assembly version cannot be read.
- Installed assembly major/minor differs from CLI.
- `server.json` is missing when live connectivity is required.
- `server.json` has invalid port, pid, token, or Revit year.
- `server.json` pid does not exist or belongs to a non-Revit process.
- `/api/status` returns 401, 404, malformed JSON, or an explicit API error.
- Live Add-in major/minor differs from CLI.

Revit closed is allowed to produce useful installation diagnostics. The command should still fail because the full runtime baseline is unavailable, but it should distinguish local install failures from runtime connection failures.

## Testing

Pure CLI tests:

- `doctor` reports and compares CLI, installed Add-in, and live Add-in versions.
- `doctor` fails on missing manifest, missing assembly, unparsable version, major/minor mismatch, invalid `server.json`, stale pid, and non-Revit pid.
- `doctor` warns on patch-only version mismatch.
- `RevitClient` preserves structured API errors and produces specific 401/404 diagnostics.
- `ProgramExit` preserves handler-set failure exit codes.

Add-in and protocol tests:

- `ApiServer` accepts an injectable `serverInfoPath`.
- Protocol tests use a temp `server.json` and never write or delete the real user file.
- `/api/status` returns an Add-in version derived from the currently loaded Add-in assembly metadata.
- Add-in tests run with:

  ```powershell
  dotnet test tests\RevitCli.Addin.Tests\RevitCli.Addin.Tests.csproj -p:RevitInstallDir="D:\revit2026\Revit 2026"
  ```

Live Revit smoke:

- Revit 2026 is open with `D:\桌面\revit_cli.rvt`.
- `doctor` must show CLI/Add-in version consistency.
- Smoke records manifest path, assembly path, CLI version, Add-in version, commands, exit codes, and key output.
- The existing safe write/restore target remains:

  ```powershell
  scripts\smoke-revit2026.ps1 `
    -ElementId 337596 `
    -Category walls `
    -Filter "标记 = TEST" `
    -Param "标记" `
    -Value "TEST-CODEX-20260426" `
    -Apply
  ```

## Acceptance Criteria

- `doctor` fails when CLI `1.3.x` connects to or finds installed Add-in `1.0.x`.
- `doctor` passes when CLI, installed Add-in, and live Add-in share the same major/minor version.
- `status` reports the live Add-in assembly version.
- Add-in protocol tests do not alter `C:\Users\Lenovo\.revitcli\server.json`.
- The D-drive Revit 2026 path works for build, tests, and smoke.
- The smoke slice still restores the test parameter to its original value.

## Implementation Notes

- Prefer small helpers for version parsing and comparison so `doctor` stays readable.
- Keep version checks in CLI-side code; do not add a new endpoint.
- Do not make tests depend on the user's real Revit session.
- Keep generated smoke reports and publish outputs out of git.
