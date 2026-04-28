# RevitCli MCP server — operator guide

This document is the operator-facing reference for running RevitCli's
Model Context Protocol (MCP) server. If you want an LLM agent
(Claude Desktop, Cursor, your own SDK script) to drive Revit through
RevitCli — this is where you start.

> **What is MCP?** A JSON-RPC protocol over stdio that lets LLM
> clients enumerate and call tools / read resources. Spec
> 2024-11-05. RevitCli's MCP server exposes 9 tools and 5 resources
> covering query, audit, write, and observability.

---

## 1. Prerequisites

- A working RevitCli install (`revitcli --version` returns a version).
- The Revit add-in loaded inside Revit (the MCP server proxies
  through the add-in's HTTP endpoint — without it, every tool will
  return "Revit is not running or plugin is not loaded").
- A `.revitcli.yml` profile in your project root (only required if
  you plan to use `audit` / `fix` / `publish`; query / status work
  without one).

---

## 2. Quickstart: hand-test before connecting an LLM

The fastest way to confirm the MCP server works in your environment
is to call it from the CLI, no chat client needed.

```bash
# What's exposed?
revitcli mcp list

# Read-only sanity check — does the addin respond?
revitcli mcp test status

# Discover what's in the active document
revitcli mcp resource revitcli://categories

# Try a write — refused without --allow-writes
revitcli mcp test set --args '{"param":"Mark","value":"D-1","confirm":true,"elementId":42}'
# > Write tools are disabled. Restart `mcp serve` with --allow-writes.

# Same call, this time enabled
revitcli mcp test set --allow-writes \
  --args '{"param":"Mark","value":"D-1","confirm":true,"elementId":42}'
```

If `mcp test status` works, the LLM client will work too — the same
server graph backs both paths.

---

## 3. Connect Claude Desktop

Edit `~/Library/Application Support/Claude/claude_desktop_config.json`
(macOS) or `%APPDATA%\Claude\claude_desktop_config.json` (Windows):

```json
{
  "mcpServers": {
    "revitcli": {
      "command": "revitcli",
      "args": ["mcp", "serve"]
    }
  }
}
```

To enable mutating tools (`set`, `fix`, `rollback`, `import`,
`publish`):

```json
{
  "mcpServers": {
    "revitcli": {
      "command": "revitcli",
      "args": ["mcp", "serve", "--allow-writes"]
    }
  }
}
```

> **Don't set `--allow-writes` until you've reviewed the safety
> model in §5.** Once enabled the LLM can mutate the live model;
> every call still requires `confirm: true` per call but the door is
> open.

Restart Claude Desktop. The tools appear in the chat composer's
tool list.

---

## 4. Tools and resources reference

### 4.1 Read-only tools (always enabled)

| Tool       | Purpose                                          | Key inputs              |
| ---------- | ------------------------------------------------ | ----------------------- |
| `status`   | Is Revit running? Which document is open?        | (none)                  |
| `query`    | List elements by category and filter expression. | `category`, `filter`    |
| `audit`    | Run a profile-defined check set; return issues.  | `checkName`, `severity` |
| `snapshot` | Capture a fresh model snapshot (summary-only).   | (none)                  |

### 4.2 Write tools (require `--allow-writes`)

All five share a **double-gate** safety model:

1. The server must have been started with `--allow-writes`.
2. Each call must include `"confirm": true` in the arguments.

Both gates closed → refusal text. Audit log entry on every outcome.

| Tool       | Purpose                                                        | Key inputs                                                     |
| ---------- | -------------------------------------------------------------- | -------------------------------------------------------------- |
| `set`      | Write a parameter value on one or more elements.               | `param`, `value`, `confirm`, `elementId`/`elementIds`/`filter` |
| `fix`      | Apply profile-driven autofixes to audit-flagged issues.        | `checkName`, `rules`, `dryRun`, `confirm`                      |
| `rollback` | Restore parameters from a baseline written by `fix`.           | `baseline` (under `.revitcli/`), `confirm`                     |
| `import`   | Bulk-write parameters from a CSV file.                         | `file` (under `.revitcli/`), `category`, `matchBy`, `confirm`  |
| `publish`  | Run the profile's publish pipeline (precheck→exports→receipt). | `pipeline`, `dryRun`, `since`, `confirm`                       |

**Path binding** (since phase 3.5): `rollback`'s `baseline`,
`import`'s `file`, and `publish`'s `since` MUST resolve under
`.revitcli/`. Anything else gets refused with audit
outcome=`refused-path-out-of-bounds`. Stage caller-controlled
inputs there.

**Progress notifications** (since phase 5): `publish` emits
`notifications/progress` start + finish events when the client
includes `_meta.progressToken` in the request. Other tools accept
the token gracefully but emit no events.

### 4.3 Resources (read-only)

| URI                            | Mime type        | What                                                |
| ------------------------------ | ---------------- | --------------------------------------------------- |
| `revitcli://snapshot/latest`   | application/json | Fresh summary-only snapshot from the addin.         |
| `revitcli://history/`          | application/json | Recent snapshots from the local timeline.           |
| `revitcli://profile/effective` | text/yaml        | Resolved effective profile (extends-merged).        |
| `revitcli://categories`        | application/json | Category list with element counts (LLM cold-start). |
| `revitcli://journal/recent`    | application/json | Last 50 audit-log entries (agent self-awareness).   |

Read any resource from the CLI without an MCP client:

```bash
revitcli mcp resource revitcli://categories
```

---

## 5. Safety model

### 5.1 Two-layer gate

Every write tool requires:

1. **Server-side opt-in**: `mcp serve --allow-writes`. Your choice
   when starting the server. Defaults to OFF.
2. **Per-call opt-in**: `"confirm": true` in the request arguments.
   The LLM must consciously assert intent each time. Missing or
   `false` → refusal.

Defense in depth: a misconfigured server (writes enabled by
mistake) still won't act on a call that lacks `confirm`. A
miscoded LLM (passing `confirm: true` everywhere) still can't
write if writes weren't enabled at startup.

### 5.2 Caller-supplied paths are bounded

`rollback` (`baseline`), `import` (`file`), and `publish` (`since`)
take filesystem paths. All three resolve through `McpPathGuard`
which:

- Canonicalizes the input via `Path.GetFullPath`.
- Asserts the resolved path lives under `.revitcli/`.
- Refuses traversal segments (`..`), different-drive paths
  (Windows), and string-prefix lookalikes (`.revitcli-evil/`).

Symlink-naive: an operator who plants a symlink under `.revitcli/`
pointing outside is trusted. The threat model is LLM-supplied
strings, not a hostile operator.

### 5.3 Audit log

Every CLI and MCP write — successful, refused, errored — appends a
JSON line to `.revitcli/journal.jsonl`. The shape is consistent
across transports, tagged with `transport: "cli"` or `"mcp"`.

Outcomes recorded:

- `ok` — write succeeded.
- `error` — server returned a failure (Revit closed, parameter not
  found, …).
- `refused-writes-disabled` — `--allow-writes` was not set.
- `refused-no-confirm` — `confirm: true` missing or false.
- `refused-missing-baseline` / `refused-missing-file` /
  `refused-missing-category` / `refused-missing-matchBy` —
  required arg not supplied.
- `refused-path-out-of-bounds` — caller-supplied path escaped
  `.revitcli/`.

Read it from the CLI:

```bash
revitcli journal tail --since 1h
revitcli journal stats --since 24h
revitcli journal tail --action set --output json | jq .
```

Or expose it to the agent itself:

```bash
revitcli mcp resource revitcli://journal/recent
```

The LLM can ask "what did I just do?" and use the answer for
self-correction.

---

## 6. Common workflows

### 6.1 Cold-start: agent meets unfamiliar model

```
Agent: read revitcli://categories
       → "Walls 142, Doors 38, Windows 21, ..."
Agent: read revitcli://profile/effective
       → checks/exports/publish definitions
Agent: tool/call audit { checkName: "default" }
       → list of issues
```

### 6.2 Audit → fix → verify loop

```
Agent: audit { checkName: "default" }
Agent: fix { checkName: "default", confirm: true }   # captures baseline first
Agent: audit { checkName: "default" }                 # verify clean
```

If something looked wrong, recovery:

```
Agent: rollback { baseline: ".revitcli/fix-baseline-<ts>.json", confirm: true }
```

### 6.3 Bulk parameter import from spreadsheet

Operator stages the CSV under `.revitcli/imports/`. Agent runs:

```
Agent: import {
         file: ".revitcli/imports/january-doors.csv",
         category: "doors",
         matchBy: "Mark",
         dryRun: true,
         confirm: true
       }
       → plan summary, no writes
Agent: import { ..., dryRun: false, confirm: true }
       → applied
```

### 6.4 Governed publish

```
Agent: publish {
         pipeline: "client-A",
         dryRun: true,
         confirm: true
       }
       → preview
Agent: publish { pipeline: "client-A", confirm: true }
       → real publish, with progress events if client subscribed
```

---

## 7. Troubleshooting

| Symptom                                                                                | Likely cause                                                                                        |
| -------------------------------------------------------------------------------------- | --------------------------------------------------------------------------------------------------- |
| Every tool returns "Revit is not running or plugin is not loaded"                      | Revit closed or addin not loaded. Start Revit, open a doc, verify with `revitcli doctor`.           |
| Write tool returns "Write tools are disabled. Restart `mcp serve` with --allow-writes" | Server started without the flag. Restart Claude Desktop after editing the config.                   |
| Write tool returns "This is a write operation. Re-issue with confirm: true"            | LLM omitted the gate. Ask it to add `confirm: true` to its arguments.                               |
| `import` / `rollback` / `publish` returns "must be a path under .revitcli/"            | Caller passed an absolute path or one that traverses outside. Move the file under `.revitcli/`.     |
| `journal tail` shows nothing                                                           | No writes happened in the window, or the journal lives elsewhere. Pass `--path /actual/path.jsonl`. |

---

## 8. Where the code lives

- `src/RevitCli/Mcp/McpServer.cs` — JSON-RPC dispatch, capabilities,
  progress plumbing.
- `src/RevitCli/Mcp/Tools/` — one file per tool.
- `src/RevitCli/Mcp/Resources/` — one file per resource.
- `src/RevitCli/Mcp/Tools/McpPathGuard.cs` — `.revitcli/` boundary.
- `src/RevitCli/Mcp/Tools/JsonArgs.cs` — JSON arg helpers shared
  across write tools.
- `src/RevitCli/Output/JournalLogger.cs` — audit-log writer.
- `src/RevitCli/Journal/JournalReader.cs` — audit-log reader.
- `src/RevitCli/Commands/McpCommand.cs` — `mcp serve / list / test /
resource` subcommands.

For the protocol-level test scaffolding see
`tests/RevitCli.Tests/Mcp/`.
