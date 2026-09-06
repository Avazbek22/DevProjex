# Security model

DevProjex treats a selected project as data to inspect, not as authority to run
its programs or configuration. This page is a map of the existing guarantees and
limits documented in [McpServer.md](McpServer.md),
[Git-Safety.md](Git-Safety.md), [HideSecrets.md](HideSecrets.md), and
[Release-Process.md](Release-Process.md); it does not add a new security promise.

| Review criterion | Boundary and mitigation |
|---|---|
| Prompt injection | MCP wraps returned project content in a random per-response untrusted-data delimiter. Trusted warnings and effective-filter trailers are emitted outside that boundary and contain no project-controlled message text. Instructions found in project files remain data, never trusted control input. |
| Secret leakage | MCP always applies the local Smart Secrets pass to returned file content, stored packs, and search results. Findings expose rule, category, path, and line, never the matched value. Reviewed example and placeholder classes are allowlisted. Detection is heuristic, binary and oversized-file limits apply, and every pack must still be reviewed before it is published outside the user's environment. |
| Scope of authority | Canonical-path, symbolic-link, and validated-handle checks enforce the configured root jail. Paths, patterns, profiles, `tracked_only`, and `git_scope` narrow the human-selected baseline. An agent can change exclusion toggles only when the server was explicitly started with `--allow-agent-exclusions`; even then, the startup Git baseline and the permanent `.git` boundary remain in force. |
| Network | Local MCP use is offline by default. Remote Git acquisition requires the startup `--allow-remote` opt-in and then uses the `ExplicitNetwork` Git profile with a validated URL and allowed transport. Inspection operates on the pinned checkout after acquisition. |
| Process execution | Project executables and arbitrary project commands are never run. Git is a pinned executable invoked only through typed `LocalRead`, `ManagedCheckout`, or `ExplicitNetwork` operations with fixed commands, restricted configuration, an allowlisted environment, deadlines, output limits, and process-tree cancellation. |
| File system | MCP tools are annotated read-only and non-destructive for the source project. Remote checkouts and stored packs live in application-owned managed cache or temporary session storage; the source project is not written. Cache checkout writes require an active lease and a path inside the application-owned repository container. |
| Supply chain | Grammar sources and secret-detection rules are pinned and hash-verified. Release channels require content receipts, static completeness checks, mutation gates, and real-entry-point smoke tests before publication. Published container artifacts include build provenance; the other channel-specific evidence is described in the release process. |

## What is not guaranteed

- Secret detection is not proof that output is safe or clean. It covers reviewed
  formats heuristically; binary files are not scanned, and documented size and
  inspection limits still apply.
- A concurrent process can replace `.git/config` between the safety query and a
  following Git invocation. The invocation keeps its fixed profile, but the two
  filesystem operations are not atomic.
- For SSH, DevProjex pins the executable and rejects repository
  `core.sshCommand`, but the user's SSH configuration can still execute
  `ProxyCommand` or `Match exec` directives.
- Root-jail and handle checks do not isolate DevProjex from other processes
  running as the same operating-system user. Such a process can race or inspect
  data that the user account itself can access.

For exact tool limits and trust markers, see [McpServer.md](McpServer.md). For the
full Git argument and environment profiles, see [Git-Safety.md](Git-Safety.md).
For detector coverage and failure behavior, see
[HideSecrets.md](HideSecrets.md). For artifact gates and provenance, see
[Release-Process.md](Release-Process.md).
