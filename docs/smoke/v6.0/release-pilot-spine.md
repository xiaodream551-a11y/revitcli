# RevitCli v6.0 Release Pilot Spine Portable Smoke

This document records a synthetic CLI fixture for the v6.0 office rollout pilot
helper spine. It is public-safe evidence for command behavior only. It is not office rollout completion, not a production support claim, and not evidence from
a real office project-copy pilot.

## Scope

- Fixture pilot id: `v6-synthetic-pilot-spine-01`
- Fixture packet path:
  `docs/smoke/v6.0/v6-synthetic-pilot-spine-01.md`
- Fixture status path:
  `docs/smoke/v6.0/office-rollout-status.json`
- Boundary: no client names, model paths, ticket contents, private notes, or
  real office rollout evidence are recorded here.

## Command Spine

The portable CLI spine is intentionally dry-run first:

1. `release pilot scaffold --pilot-id v6-synthetic-pilot-spine-01 --output json`
   emits `release-pilot-scaffold.v1`, writes only the public-safe packet
   scaffold, and reports scaffold `nextActions`.
2. `release pilot validate --path docs/smoke/v6.0/v6-synthetic-pilot-spine-01.md --output json`
   emits `release-pilot-validate.v1`, reports validation issues before status
   registration, and uses validate `nextActions` for safe repair.
3. `release pilot register --pilot-id v6-synthetic-pilot-spine-01 --path docs/smoke/v6.0/v6-synthetic-pilot-spine-01.md --output json`
   is a rejected dry-run register path, emits `release-pilot-register.v1`,
   reports `completedOfficePilotCountBefore`,
   `completedOfficePilotCountAfter`, and register nextActions without mutating
   rollout status. The register/status gates reject synthetic or local
   controlled evidence so this fixture cannot become a completed office pilot.
4. `release pilot status --output json` emits `release-pilot-status.v1`,
   reports `missingEvidence`, `missingEvidenceSummary`,
   `evidenceCompleteOfficePilotCount`,
   `remainingEvidenceCompleteOfficePilotCount`, and status structural repair
   nextActions without mutating rollout status.
5. `release pilot claim --output json` emits `release-pilot-claim.v1`, remains
   dry-run first, reports machine-readable `claimBlockers`, and keeps
   completion/support writes behind explicit readiness gates.
6. `release pilot support-review scaffold --path docs/smoke/v6.0/<support-review>.md --output json`
   emits `release-pilot-support-review-scaffold.v1` for the public-safe
   production support review summary path, reports
   `rolloutStatusMutated=false`, and does not turn a blank scaffold into a
   production support claim.
7. `release pilot support-review validate --path docs/smoke/v6.0/<support-review>.md --output json`
   emits `release-pilot-support-review-validate.v1`, checks the public-safe
   summary before claim, and keeps blank summaries claim-blocking.

## Result Boundary

The synthetic spine proves the helper commands, schemas, dry-run behavior,
machine-readable nextActions, and support review validation shape. It does not
satisfy the office rollout pilot threshold in
`docs/smoke/v6.0/office-rollout-status.json`. Real completion still requires
2-3 completed office pilots with private review, BIM manager signoff,
project-copy owner signoff, support ticket review, and multi-user rollout
postmortem evidence.
