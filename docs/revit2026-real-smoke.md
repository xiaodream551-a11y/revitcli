# Revit 2026 Real Smoke Acceptance

This is the internal acceptance gate for the Revit 2026 vertical slice:

```text
doctor -> status -> query --id -> query <category> --filter -> set --dry-run -> set -> query confirm -> restore
```

The goal is to prove that the CLI, installed add-in, live add-in HTTP server, Revit API bridge, dry-run preview, transaction write, and restore path all work against a real Revit 2026 document.

## Prerequisites

- Windows machine with Revit 2026 installed.
- Revit API DLLs exist under the selected install directory:
  - `RevitAPI.dll`
  - `RevitAPIUI.dll`
- RevitCli CLI and add-in installed from the same build.
- Revit 2026 restarted after installing the add-in.
- A project document is open before running `status`, `query`, or `set`.
- The test model contains at least one wall or door that can be queried by a stable filter.

If Revit 2026 is installed outside `%ProgramFiles%`, pass the path explicitly:

```powershell
$revit2026 = "D:\revit2026\Revit 2026"
```

## Test Model Contract

Record these before running the smoke:

| Field | Requirement |
|---|---|
| Model path | Full path to the `.rvt` used for the run |
| Revit build | Revit 2026 build shown by `status` |
| Category | Prefer `walls` or `doors` |
| Element ID | Stable element ID returned by `query --id` |
| Filter | Must match exactly one element and must not depend on the parameter being written |
| Safe parameter | Writable text parameter, preferably `Comments` or another project-safe text parameter |
| Old value | Non-null value so `set` can restore it exactly |
| Test value | Non-empty unique value, for example `revitcli-smoke-20260426` |

Do not run the apply step if the dry-run preview does not show the target element ID and the exact old-value to new-value transition.

## Install From Source Tree

Use this when validating a local branch before review:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\install.ps1 `
  -RevitYears 2026 `
  -RevitInstallDir "D:\revit2026\Revit 2026" `
  -Force
```

Expected result:

- CLI is copied to `%LOCALAPPDATA%\RevitCli\bin`.
- Add-in files are copied to `%LOCALAPPDATA%\RevitCli\addin\2026`.
- Manifest exists at `%APPDATA%\Autodesk\Revit\Addins\2026\RevitCli.addin`.
- `%LOCALAPPDATA%\RevitCli\install.json` records the installed CLI version and `2026`.

Restart Revit 2026 after this step.

## Manual Baseline Commands

Run these first and record command, exit code, and key output:

```powershell
revitcli doctor
$LASTEXITCODE

revitcli status
$LASTEXITCODE

revitcli query --id 12345 --output json
$LASTEXITCODE

revitcli query walls --filter "Mark = W-01" --output json
$LASTEXITCODE

revitcli set walls --filter "Mark = W-01" --param Comments --value "revitcli-smoke-20260426" --dry-run
$LASTEXITCODE
```

Only continue when:

- `doctor` exits `0`.
- `status` shows Revit 2026, an active document, and the expected add-in version.
- `query --id` returns exactly one element.
- `query <category> --filter` returns exactly one element, with the same ID.
- `set --dry-run` shows the target ID plus old and new parameter values.

## Scripted Smoke

Prefer the scripted smoke after the manual commands identify a safe element:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\smoke-revit2026.ps1 `
  -RevitInstallDir "D:\revit2026\Revit 2026" `
  -ElementId 12345 `
  -Category walls `
  -Filter "Mark = W-01" `
  -Param Comments `
  -Value "revitcli-smoke-20260426" `
  -OutputPath ".\revitcli-smoke-2026-dry-run.json"
```

Expected result:

- Exit code `0`.
- Report JSON includes `cliVersion`, `installedAddinVersion`, `liveAddinVersion`, `manifestAssemblyPath`, `serverInfoPath`, `oldValue`, `testValue`, and every command step with its exit code and output.
- `applied` is `false`.

Then run the apply/confirm/restore smoke:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\smoke-revit2026.ps1 `
  -RevitInstallDir "D:\revit2026\Revit 2026" `
  -ElementId 12345 `
  -Category walls `
  -Filter "Mark = W-01" `
  -Param Comments `
  -Value "revitcli-smoke-20260426" `
  -Apply `
  -OutputPath ".\revitcli-smoke-2026-apply.json"
```

Expected result:

- Exit code `0`.
- `applied` is `true`.
- The script writes the test value, confirms it with `query --id`, restores the old value with `set --id`, then confirms the restore with `query --id`.

If the apply step fails after the write attempt, the script still tries to restore the original value. Treat any restore failure as blocking.

## Evidence Packet

Attach or paste these items into the PR or review handoff:

```text
Date/time:
Branch/commit:
Machine:
Revit install dir:
Revit build:
Model path:
Model preconditions:
  category:
  elementId:
  filter:
  safe parameter:
  old value:
  test value:

Commands:
  command:
  exit code:
  key output:

Smoke reports:
  dry-run report path:
  apply report path:

Result:
  PASS / FAIL
  follow-up:
```

## Stop Conditions

Stop and fix the lower layer first when any of these happen:

- `doctor` cannot find `RevitInstallDir`, `RevitAPI.dll`, or `RevitAPIUI.dll`.
- The add-in manifest is missing or points to a DLL that does not exist.
- Installed add-in version, live add-in version, and CLI version do not match.
- `server.json` is missing, stale, has a dead PID, or points to a non-Revit process.
- `status` reports no active document.
- `query --id` returns no element or a different element than expected.
- Filter returns zero or multiple elements.
- Dry-run preview does not include the expected old and new values.
- The safe parameter is missing, null, read-only, or cannot be restored.
