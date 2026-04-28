# RevitCli on GitHub Actions

Wire `revitcli check` into a pull-request workflow in three steps:

1. Add `permissions: { security-events: write }` to your job (Code Scanning
   needs it to ingest SARIF).
2. Add `permissions: { pull-requests: write }` to your job if you want the
   sticky PR comment bot enabled (the default).
3. Reference the bundled composite action at
   `.github/actions/revitcli-check`.

## Quickstart

```yaml
# .github/workflows/revitcli-check.yml
name: RevitCli Check
on:
  pull_request:
permissions:
  contents: read
  security-events: write
  pull-requests: write # required by the sticky PR comment bot (default)
jobs:
  check:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: ./.github/actions/revitcli-check
        with:
          revitcli-version: "1.7.0" # pin in production
          profile: ".revitcli.yml" # optional; auto-discovered when omitted
          # pr-comment: 'true'        # default; pass 'false' to disable
```

Findings appear in three places:

1. **Files changed** tab — Code Scanning annotations (from SARIF).
2. **Security** tab — full SARIF run with rule descriptions.
3. **Conversation** tab — a single sticky comment summarizing severity
   counts and a per-rule table. Subsequent pushes UPDATE this comment in
   place rather than stacking new ones; the bot identifies the comment by
   the hidden `<!-- revitcli-pr-comment -->` marker the writer prepends.

## Disabling the PR comment bot

If your repo policy forbids bot comments, or `pull-requests: write` is
unavailable, pass `pr-comment: 'false'`:

```yaml
- uses: ./.github/actions/revitcli-check
  with:
    pr-comment: "false"
```

The SARIF upload is independent and continues to work without
`pull-requests: write`.

## Webhook payloads

`revitcli check` honours `defaults.notify` from the project profile. When set
to an HTTPS URL, every successful run POSTs the following JSON:

```json
{
  "event": "check",
  "name": "default",
  "passed": 12,
  "failed": 1,
  "suppressed": 0,
  "severityFailed": true,
  "timestamp": "2026-04-27T12:34:56.7890000Z",
  "profilePath": "/repo/.revitcli.yml"
}
```

`severityFailed` mirrors the command's exit code: `true` when the check would
exit non-zero per the profile's `failOn` policy. Webhook delivery is
best-effort — failures emit a stderr warning but never change the exit code.

See [`.github/actions/revitcli-check/README.md`](../../.github/actions/revitcli-check/README.md)
for the action's input reference.
