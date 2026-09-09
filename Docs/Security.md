# Security model

DevProjex treats a selected project as data to inspect, not as authority to run
its programs or configuration. This page is a map of the existing guarantees and
limits documented in [McpServer.md](McpServer.md),
[Git-Safety.md](Git-Safety.md), [HideSecrets.md](HideSecrets.md), and
[Release-Process.md](Release-Process.md); it does not add a new security promise.

| Review criterion | Boundary and mitigation |
|---|---|
| Prompt injection | MCP wraps returned project content, human-readable structured results, dependency details, and error details in a random per-response untrusted-data delimiter. Trusted warnings and status trailers contain only fixed codes, enum states, and counts; paths, names, configuration keys, parser reasons, and other project-controlled text stay inside the boundary. Instructions found in project files remain data, never trusted control input. |
| Secret leakage | MCP always applies the local Smart Secrets pass to returned file content, stored packs, search results, and synthetic dependency-result bodies. Search receives exact final-text ranges inserted by redaction and excludes only those ranges; placeholder-like source code remains searchable. Findings expose rule, category, path, and line, never the matched value. File names and paths are address fields and are not masked by secret or private-data protection; project-controlled addresses in response text stay inside the untrusted-data boundary. Reviewed example and placeholder classes are allowlisted. Detection is heuristic, binary and oversized-file limits apply, and every pack must still be reviewed before it is published outside the user's environment. Content that cannot pass mandatory bounded inspection is withheld: selection-wide tools report a partial result, while single-file `get_file` returns an explicit error and batch `get_file` reports a count-safe unavailable section. |
| Scope of authority | Canonical-path, symbolic-link, and validated-handle checks enforce the configured root jail, including the second open of a validated portable profile. Paths, patterns, `tracked_only`, and `git_scope` narrow the effective selection. An explicit profile may broaden the startup exclusions, and an agent can change exclusion toggles only when the server was explicitly started with `--allow-agent-exclusions`. Neither delegation crosses the root jail, disables mandatory Hide Secrets, or bypasses an active remote-host allowlist; the permanent `.git` boundary also remains in force. |
| Network | Local MCP use is offline by default. Remote Git acquisition requires the startup `--allow-remote` opt-in and then uses the `ExplicitNetwork` Git profile with a validated URL and allowed transport. An optional `--remote-hosts` allowlist narrows that opt-in to exact normalized hosts. Inspection operates on the pinned checkout after acquisition. The trusted trailer reports only a validated lowercase hexadecimal commit id; the repository-controlled branch remains inside structured untrusted data. |
| Process execution | Project executables and arbitrary project commands are never run. Git is a pinned executable invoked only through typed `LocalRead`, `ManagedCheckout`, or `ExplicitNetwork` operations with fixed commands, restricted configuration, an allowlisted environment, deadlines, output limits, and process-tree cancellation. |
| File system | MCP tools are annotated read-only and non-destructive for the source project. Remote checkouts and stored packs live in application-owned managed cache or temporary session storage; the source project is not written. Cache checkout writes require an active lease and a path inside the application-owned repository container. |
| Supply chain | Grammar sources and secret-detection rules are pinned and hash-verified. Release channels require content receipts, static completeness checks, mutation gates, and real-entry-point smoke tests before publication. Published container artifacts include build provenance; the other channel-specific evidence is described in the release process. |

Secret-redaction exceptions are operator-controlled. Project content is
untrusted input, so inline markers such as `gitleaks:allow` cannot grant an
exception in GUI, CLI, or MCP output.

Code transformations cannot weaken redaction for characters that remain in the output. DevProjex
detects against both the immutable source snapshot and transformed text, projects retained source
ranges through the transform map, and masks the union. Overlap priority selects a rule label only;
it does not discard another finding's uncovered range.

The structured tier derives replacement boundaries from the recognized dotenv, connection-string,
JSON, YAML, XML, Python, Dockerfile, netrc, or npmrc grammar. It masks logical values while leaving
their surrounding syntax outside the finding, and only a complete simple variable reference is
exempt from a sensitive-key finding.

Temporary redaction data is stored in private per-user directories. After an
abnormal termination it can remain until a later DevProjex startup runs the
scavenger, which removes stale directories once they are more than 24 hours old.

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
