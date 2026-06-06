# RevitCli v6.1 -> v7 Roadmap

> Status: execution baseline, created 2026-05-31.
> Current repo state: v6.0.0 is GO for the Local BIMOps Workbench contract
> baseline and NO-GO for office rollout completion or production support.
> North star: consolidate the existing local-first command contract into an
> office-grade BIMOps layer before expanding the command surface.

## Product Position

RevitCli v7 is not a new batch of unrelated Revit commands. It is the
office-grade version of the current Local BIMOps Workbench: versioned policy
packs, reviewable workflows, local ledger/evidence packages, support intake,
and rollout gates that make Revit project health, delivery preflight, batch
governance, and support triage repeatable without SaaS or hidden automation.

The technical strategy is contract consolidation:

- Reuse `workflow validate/simulate/review/run/receipts` instead of creating a
  separate runbook engine.
- Reuse `standards install/validate` as the base for policy packs.
- Reuse `ledger query/validate/stats/timeline/analytics/replay` as the local
  evidence spine.
- Reuse `release pilot scaffold/validate/register/status/claim` as the office
  rollout gate.
- Reuse `check/audit/score/issue preflight/views/links/rooms/marks/library/crash`
  by converging their outputs on one finding contract.

## Non-Goals

| Non-goal | Reason |
| --- | --- |
| SaaS dashboard or central database | Local files, receipts, and ledger records remain the source of truth. |
| Built-in LLM or MCP runtime | External tools may call visible CLI commands but must not bypass dry-run, approval, receipts, rollback, or local audit trails. |
| Default cross-project model writes | Multi-project work stays read-only unless a reviewed remediation plan is explicitly approved per model. |
| Generic one-click model optimization | Fixes must be white-listed, plan-backed, verified, and rollback-aware. |
| New workflow engine | Existing `workflow` commands already provide the right execution surface. |
| New health platform from scratch | Existing check, score, standards, and preflight surfaces should converge on shared findings. |

## Milestones

### v6.1: Supportability Closure

Goal: make a BIM manager or support engineer able to answer what is wrong with
this machine, this Revit install, this content library, this add-in set, or this
crash packet.

| Workstream | Existing surface | Deliverable |
| --- | --- | --- |
| Environment baseline | `doctor`, `status`, `release verify`, `env baseline` | `revit-env-baseline.v1` fields for Revit year/build, add-in identity/counts, content paths, Desktop Connector, GPU/runtime facts, and unknown-with-reason values. |
| Crash support bundle v2 | `crash analyze`, `crash collect` | Bundle manifest, analysis JSON, copied evidence, README, redaction report, and `support verify` style integrity checks. |
| Crash signatures | `crash analyze` | Fixture-backed signature rules with source location, confidence, and recommended next action. |
| Library doctor | `library check`, `library sources`, `library install` | Core vs optional content findings, template path findings, reversible Revit.ini repair plan, installer-launch receipts. |
| Add-ins audit | new `addins audit/plan` surface or `doctor` extension | Read-only manifest inventory, missing assembly, duplicate AddInId, load-order shadowing, and dev-path detection. |
| Support packet verification | `crash collect`, future support command | Offline integrity check for redacted local support bundles. |

### v6.2: Policy And Gate Closure

Goal: turn existing checks into policy-driven gates without duplicating command
surfaces.

| Workstream | Existing surface | Deliverable |
| --- | --- | --- |
| Unified finding contract | shared DTOs plus command output adapters | `bimops-finding.v1` used by new/updated reports. |
| Policy pack v1 | `standards install/validate`, profiles | Versioned policy pack schema, examples, validation, and diffable rule changes. |
| Preflight gate v1 | `issue preflight`, `check`, `score` | Gate envelope with stable exit codes: 0 pass, 1 warn, 2 fail, 3 tool error. |
| Score v2 | `score`, `check` | Score calculation from normalized findings and expiring waivers. |
| Family governance | `family validate`, `family purge`, standards family rules | Batch-safe family findings and quarantine plan that copies by default and never deletes by default. |
| Controlled fixer whitelist | existing plan/rollback surfaces | Only approved fixers can move from finding to plan; each declares reversibility. |

### v6.3: Pilot And Batch Closure

Goal: make office pilot evidence and multi-project read-only runs real, while
keeping production support and rollout claims blocked until evidence exists.

| Workstream | Existing surface | Deliverable |
| --- | --- | --- |
| Project manifest | `rvt scan`, `workbench project` | Stable manifest with path hash, classification, backup/local hints, and confidence. |
| Read-only batch runner | `batch`, `workflow`, project manifest | Per-model timeout, resume, JSONL results, crash collection on failure, and summary Markdown. |
| Portfolio ledger analytics | `ledger stats/timeline/analytics` | Merge local receipts/ledger records into static trend artifacts without a database. |
| Pilot evidence hardening | `release pilot` | Evidence-complete pilots only; synthetic and local controlled packets cannot satisfy rollout completion. |
| Support intake | crash/support packets, release pilot support review | Local ticket metadata, bundle attach, triage Markdown, resolution receipt, no ticket server. |
| Static evidence site | ledger and receipts | Optional static HTML/JSON viewer generated from local artifacts only. |

### v7.0: Office-Grade Local BIMOps

Goal: package the v6.1-v6.3 closures into a production-ready office workflow.

| Workstream | Command direction | Definition of done |
| --- | --- | --- |
| Policy pack | `standards policy init/validate/diff` or equivalent | Versioned packs can drive health, deliverables, families, numbering, exports, waivers, and rollout gates. |
| Workflow pack | `workflow registry/review/run/receipts` | Built-in weekly-health, pre-issue, support-triage, pilot-run, and export-package workflows have acceptance evidence. |
| Evidence package | `evidence package/verify/site` or equivalent | Receipts, ledger, journals, delivery manifests, support bundles, and static summaries can be verified offline. |
| Rollout controls | `release pilot` extensions | The CLI can answer which projects, machines, policy versions, and evidence gates block rollout. |
| Production support mode | `release pilot claim --production-support` plus support review | Production support cannot be claimed without completed pilots and a filled public-safe support review. |
| Batch supervisor | manifest-backed `workflow` or `batch` | A 50-model read-only run continues after individual model failure and produces a failure taxonomy. |

## P0/P1 Execution Order

| Priority | Issue | Acceptance |
| --- | --- | --- |
| P0 | `bimops-finding.v1` shared contract | Shared DTO, JSON round-trip tests, and migration notes for existing commands. |
| P0 | `env baseline into receipts` | `env baseline` emits `revit-env-baseline.v1` with Revit build/update-adjacent evidence and machine/runtime facts without failing when unknown; approved `library install`, `library repair-apply`, and `addins apply` receipts link the baseline path/hash/schema and expose the capture command when it is missing. |
| P0 | `crash bundle v2 verify` | Redacted bundle can be collected and verified offline. |
| P0 | `library doctor repair plan` | Template/content/Revit.ini findings can produce reversible plans. |
| P0 | `addins audit plan` | Add-in manifest risks are visible before any disable/apply path exists. |
| P0 | `policy pack v1` | Standards validation can validate policy packs and report normalized findings. |
| P1 | `preflight gate v1` | Gate consumes normalized findings and returns stable exit codes. |
| P1 | `projects manifest read-only batch` | Multi-project check runs are resumable and read-only by default. |
| P1 | `pilot evidence hardening` | Rollout claim remains blocked until evidence-complete pilots satisfy the status schema. |
| P1 | `evidence package static site` | Local evidence can be reviewed without a server or database. |

## Implementation Status

Current normalized finding surfaces:

| Surface | Command | Contract |
| --- | --- | --- |
| Environment baseline | `env baseline --years 2024,2025,2026 --out .revitcli/evidence/env-baseline.json --output json` | `revit-env-baseline.v1`; read-only machine/Revit/add-ins/content/Desktop Connector/GPU/runtime evidence with unknown-with-reason values and `requiresRevit: false` |
| Crash support bundle v2 | `crash analyze --output json`; `crash repro --output markdown`; `crash collect --output-dir packet`; `crash verify --packet packet` | `revit-crash-analysis.v1`, `revit-crash-repro.v1`, `revit-crash-collection.v1`, `revit-crash-redaction-report.v1`, `revit-crash-packet-manifest.v1`, and `revit-crash-packet-verify.v1`; clean repro checklist plus redaction review evidence and offline packet integrity checks |
| Library doctor | `library check --findings --output json`; `library repair-plan --plan-output plan.json`; `library repair-apply --plan plan.json --dry-run\|--yes --env-baseline .revitcli/evidence/env-baseline.json`; `library install --apply --yes --receipt-output receipt.json --env-baseline .revitcli/evidence/env-baseline.json`; `library repair-rollback --receipt receipt.json --dry-run\|--yes` | `bimops-finding.v1`, `revit-library-repair-plan.v1`, `revit-library-install.v1`, `revit-library-repair-receipt.v1`, and `revit-library-repair-rollback.v1`; reversible Revit.ini FamilyTemplatePath plan/apply/rollback plus installer launch receipts linked to environment baseline evidence |
| Add-ins audit | `addins audit --findings --output json`; `addins plan-disable --profile cloud-safe --plan-output plan.json`; `addins apply --plan plan.json --dry-run\|--yes --env-baseline .revitcli/evidence/env-baseline.json`; `addins rollback --receipt receipt.json --dry-run\|--yes` | `revit-addins-audit.v1`, `bimops-finding.v1`, `revit-addins-disable-plan.v1`, `revit-addins-disable-receipt.v1`, and `revit-addins-disable-rollback.v1`; read-only audit plus reversible manifest disable/apply/rollback linked to environment baseline evidence |
| Policy pack validation | `standards validate --findings --output json` | `bimops-finding.v1` |
| Policy pack v1 entrypoint | `standards policy init/validate/diff --output json` | `standards-policy-init.v1`, `standards-policy-diff.v1`, and existing standards validation/finding contracts |
| Issue gate | `issue preflight --findings --output json` | `bimops-finding.v1` |
| Delivery evidence | `deliverables verify --findings --output json` | `bimops-finding.v1` |
| Model health gate | `check --findings --output json` | `bimops-finding.v1` |
| Score v2 gate | `score --from-findings findings.json --waivers waivers.json --fail-on error --output json` | `model-health-finding-score.v1` with active/expired waiver counts and exit codes 0 pass, 1 warning gate, 2 fail gate, 3 tool error |

## Verification Gates

Every milestone must preserve these commands:

```bash
dotnet test tests/RevitCli.Tests/
revitcli workbench verify --contract workbench-contract.v2 --dir . --output json
revitcli release verify --strict --output json
revitcli release pilot status --output json
```

Live Revit claims still require current-source smoke evidence for the target
Revit year. Revit 2026 evidence must not be generalized to Revit 2024 or 2025
without separate proof or an explicit gap report.
