# RevitCli v6.0 Release Readiness

> Created: 2026-05-29.
> Current status: GO for v6.0.0 formal Local BIMOps contract baseline release.
> Office rollout status: NO-GO for office rollout completion.
> Production support status: NO-GO for production support claim.

This document is the v6.0 release boundary contract. It marks the local
contract baseline as ready while preventing local smoke, synthetic fixtures, or
the controlled `revit_cli.rvt` packet from being treated as office rollout
completion.

## Stable Release Baseline

| Area | Commands | Release expectation |
| --- | --- | --- |
| Contract gate | `workbench verify --contract workbench-contract.v2 --dir . --output json` | Must pass with `v60LocalBimOpsContractGate` and structured runtime evidence. |
| Release gate | `release verify --strict --output json` | Must pass and check the v6.0 contract, smoke docs, rollout status, local controlled packet, and this readiness boundary. |
| Current-source live smoke | `scripts/smoke-revit-wsl.sh --require-current-source` | Must pass before claiming the checked-out source is installed and loaded in Revit 2026; the current accepted condition is `currentSourceDriftKind=none`. |
| Operations ledger | `ledger append`, `ledger query`, `ledger validate`, `ledger stats`, `ledger timeline`, `ledger analytics`, `ledger replay` | Must remain local, deterministic, dry-run/preview first, and backed by smoke docs plus release/workbench checks. |
| Office pilot state | `release pilot status --output json` | Must remain machine-readable; the current status is `completedOfficePilotCount=0` and `remainingEvidenceCompleteOfficePilotCount=2`. |
| Office pilot claim | `release pilot claim --output json` | Must stay dry-run first and blocked until evidence-complete office pilots reach the threshold. |
| Production support claim | `release pilot claim --production-support --support-review docs/smoke/v6.0/<support-review>.md --output json` | Must stay blocked until office rollout completion is claimable and a public-safe `productionSupportReviewPath` has passed support-review validation. |

## Current Boundaries

- The v6.0 Local BIMOps contract baseline is ready for v6.0.0 formal release
  handoff.
- The Revit 2026 current-source no-write smoke passed on the controlled
  `revit_cli.rvt` model with `currentSourceDriftKind=none`; it proves source
  and loaded add-in alignment, not office rollout completion.
- `docs/smoke/v6.0/office-rollout-status.json` still records
  `completedOfficePilotCount=0`, no `completedPilots`, no office rollout
  completion claim, and no production support claim.
- The local controlled pilot packet is useful evidence for the controlled smoke
  model, but it is not an office rollout pilot and cannot satisfy the rollout
  threshold.
- Production support needs a filled public-safe review summary at
  `productionSupportReviewPath` after private support review; blank scaffolds
  and template files are not claim-ready.
- Revit 2024 and Revit 2025 do not have v6.0 live add-in smoke evidence in this
  release boundary.

## Non-Goals

The v6.0.0 release baseline introduces no SaaS, no MCP, no built-in LLM parser,
no dashboard-central workflow state, and no database runtime. External
operators may call visible shell commands, but they do not bypass dry-run,
explicit approval, receipts, rollback checks, or local audit trails.

## Release Command Sequence

```powershell
dotnet test tests/RevitCli.Tests/
revitcli workbench verify --contract workbench-contract.v2 --dir . --output json
revitcli release verify --strict --output json
revitcli release pilot status --output json
revitcli release pilot claim --output json
scripts/smoke-revit-wsl.sh --require-current-source
```

`release verify --strict` is the blocking local release check for v6.0.0. The
pilot status and claim commands are proof that rollout completion remains
blocked until real office pilot evidence is registered.
