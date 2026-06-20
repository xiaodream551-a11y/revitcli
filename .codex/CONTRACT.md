# RevitCli Codex Contract

This file is the single source of truth for project-local Codex agents. If an
agent file conflicts with this contract, this contract wins.

## Operating Model

RevitCli uses human-led Codex work with bounded helper agents:

- The top-level Codex session owns planning, integration, edits across
  multiple domains, and final verification.
- Project agents are optional helpers for a named task with explicit scope.
- There is no autonomous tick loop, no automatic commit path, no hidden prompt
  interpreter, and no agent that may rewrite the repository without a scoped
  assignment.

## Agents

| Agent | Use |
|---|---|
| `repo-explorer` | Read-only lookup for files, symbols, tests, and impact scope. |
| `cli-engineer` | Scoped CLI/shared-contract/profile/output/workflow implementation. |
| `addin-engineer` | Scoped Revit add-in, HTTP handler, bridge, service, and Windows/Revit work. |
| `test-engineer` | Scoped xUnit coverage, fixtures, and verification diagnosis. |
| `code-reviewer` | Read-only review for one diff, commit, or named path set. |

Do not add more agents until a repeated workflow cannot be handled by one of
these roles.

## Assignment Contract

Every write-capable agent assignment must include:

```text
Task:
Allowed paths:
Forbidden paths:
Verification:
Stop condition:
```

If any required field is missing, the agent must ask for a narrower assignment
instead of guessing. The top-level session may fill these fields directly when
delegating.

## Repository Boundaries

- Preserve unrelated user changes. Read `git status --short` before editing.
- Do not stage or commit from a project agent.
- Do not run destructive git commands such as `git reset --hard`,
  `git checkout -- <path>`, or `git clean` unless the user explicitly asks.
- Do not edit `.codex/agents/*.toml`, `.codex/config.toml`, or this contract
  unless the task is specifically about agent configuration.
- Do not commit `.revitcli/`, `.revitcli.yml`, secrets, NuGet keys, publish
  output, or machine-specific Revit install paths.

## Revit Safety Rules

- The add-in binds `http://127.0.0.1:<port>/` only.
- Every Revit API access goes through `RevitBridge.InvokeAsync(...)`.
- Mutating model operations require the project's existing dry-run/plan,
  receipt, journal, and rollback conventions.
- High-impact model writes require explicit user approval before a real apply.

## Verification Defaults

Use the smallest command that covers the change:

```powershell
dotnet build src/RevitCli/RevitCli.csproj -c Debug
dotnet test tests/RevitCli.Tests/
dotnet build shared/RevitCli.Shared/
dotnet build src/RevitCli.Addin -p:RevitYear=2026
dotnet test tests/RevitCli.Addin.Tests/
cd dashboard; npm run check; npm run build
```

If a command cannot run locally because it needs Windows/Revit, say exactly
which command was skipped and what environment is required.

## Review And Commits

- Run `code-reviewer` or do a local review before merging broad or risky work.
- Commit subjects should follow the repository's Conventional Commit style:
  `feat:`, `fix:`, `test:`, `docs:`, `ci:`, or `chore:`.
- Commit messages should include a `Verify:` line for non-trivial changes.

## State

`.codex/state/` is for temporary notes, review output, and delegation scratch
files. Do not treat it as durable project truth.
