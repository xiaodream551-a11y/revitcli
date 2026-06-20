# Codex Subagents

This repo uses human-led Codex work with a small set of bounded helper agents.
The detailed contract is in [`.codex/CONTRACT.md`](../.codex/CONTRACT.md).

There is no autonomous tick loop, feature queue, or automatic commit path. The
top-level Codex session owns planning, integration, and final verification.

## Agents

| Agent | When to use |
|---|---|
| `repo-explorer` | Read-only lookup for files, symbols, tests, and impact scope. |
| `cli-engineer` | Scoped CLI/shared-contract/profile/output/workflow implementation. |
| `addin-engineer` | Scoped Revit add-in, HTTP handler, bridge, service, and Windows/Revit work. |
| `test-engineer` | Scoped xUnit coverage, fixtures, and verification diagnosis. |
| `code-reviewer` | Read-only review for one diff, commit, or named path set. |

## Delegation Template

Use this shape when handing work to a write-capable helper:

```text
Task:
Allowed paths:
Forbidden paths:
Verification:
Stop condition:
```

Example:

```text
Task: Add coverage for the new rvt backup cleanup planner.
Allowed paths:
- tests/RevitCli.Tests/Commands/RvtCommandTests.cs
- tests/RevitCli.Tests/Rvt/**
Forbidden paths:
- src/**
Verification:
- dotnet test tests/RevitCli.Tests/ --filter "FullyQualifiedName~RvtCommandTests"
Stop condition:
- If production behavior is wrong, write the failing test and report the exact production issue.
```

## Rules

- Preserve unrelated dirty work.
- Agents do not stage or commit.
- Agents do not edit `.codex/agents/*.toml`, `.codex/config.toml`, or
  `.codex/CONTRACT.md` unless the task is specifically about agent config.
- Use read-only exploration before write delegation when impact scope is
  unclear.
- Review broad or risky changes before commit.

## Hooks

The optional hooks in `scripts/git-hooks/` now provide lightweight repository
safety only. They do not require a Codex tick marker.
