# Security model

DevProjex treats a selected project as data to inspect, not as authority to run
its programs or configuration. This page is a map of the existing guarantees and
limits documented in [McpServer.md](McpServer.md),
[Git-Safety.md](Git-Safety.md), [HideSecrets.md](HideSecrets.md), and
[Release-Process.md](Release-Process.md); it does not add a new security promise.

| Review criterion | Boundary and mitigation |
|---|---|
| Prompt injection | MCP wraps returned project content, human-readable structured results, dependency details, and error details in a random per-response untrusted-data delimiter. Trusted warnings and status trailers contain only fixed codes, enum states, and counts; paths, names, configuration keys, parser reasons, user patterns, Git references, and other project-controlled text stay inside the boundary. Live Context follows the same rule: revision changes expose only typed counts in trusted text, multi-root notices use an ordinal there, and changed paths, requested paths, and root names remain inside an untrusted-data block. Instructions found in project files remain data, never trusted control input. |
| Secret leakage | MCP always applies the local Smart Secrets pass to returned file content, stored packs, search results, and synthetic dependency-result bodies. Live calls use the marks captured from that invocation's profile even when the configured root and physical root are aliases. Dependency evidence is protected at its original source line before formatting; an unverifiable dynamic fragment is omitted, and the synthetic result is redacted again. Search receives exact final-text ranges inserted by redaction and excludes only those ranges; placeholder-like source code remains searchable. Findings expose rule, category, path, and line, never the matched value. File names and paths are address fields and are not masked inside returned source content; project-controlled addresses stay inside the untrusted-data boundary. String metadata from `list_projects`, profile reports, and errors passes the secret and private-data detectors before publication. A masked root remains addressable by its stable `#index` selector without revealing the original display text. Folder and ZIP exports are refused when a secret is detected in a project or file name, because those archive address fields cannot be safely substituted. Known example and placeholder classes are allowlisted. Detection is heuristic, binary and oversized-file limits apply, and every pack must still be checked before it is published outside the user's environment. Content that cannot pass mandatory bounded inspection is withheld: selection-wide tools report a partial result, while single-file `get_file` returns an explicit error and batch `get_file` reports a count-safe unavailable section. |
| Scope of authority | Canonical-path, symbolic-link, and validated-handle checks enforce the configured root jail, including the second open of a validated portable profile. Paths, patterns, `tracked_only`, and `git_scope` narrow the effective selection. An explicit profile may broaden the startup exclusions, and an agent can change exclusion toggles only when the server was explicitly started with `--allow-agent-exclusions`. Neither delegation crosses the root jail, disables mandatory Hide Secrets, or bypasses an active remote-host allowlist; the permanent `.git` boundary also remains in force. Stored packs are invalidated when the protection policy changes; a selection-only change keeps the protected snapshot but emits an explicit revision warning. A generated editor connection replaces the editor `devprojex` entry as a whole and preserves only `sandboxEnabled` and `dev`; unknown properties from an earlier entry are not inherited. |
| Network | Local MCP use is offline by default. No probe leaves the machine before the remote opt-in is considered: a `project` value is classified by its spelling alone, and a path that names a host is refused before anything opens it, because opening one is itself the network operation the operating system performs to answer. Device paths to a drive, a volume or a named pipe address this machine and are not refused. A built product accepts a repository URL only over `https` or `ssh`, including the `scp`-style `user@host:path` form; `http`, `git`, and `file` URLs are refused twice, once where a surface validates the URL and again where Git is invoked. That transport set is fixed in the build: no environment variable, profile, configuration file, or command-line value widens it. Remote Git acquisition requires the startup `--allow-remote` opt-in and then uses the `ExplicitNetwork` Git profile with a validated URL and allowed transport. An optional `--remote-hosts` allowlist narrows that opt-in to exact normalized hosts. Inspection operates on the pinned checkout after acquisition. The trusted trailer reports only a validated lowercase hexadecimal commit id; the repository-controlled branch remains inside structured untrusted data. |
| Process execution | Project executables and arbitrary project commands are never run. Git is a pinned executable invoked only through typed `LocalRead`, `ManagedCheckout`, or `ExplicitNetwork` operations with fixed commands, restricted configuration, an allowlisted environment, deadlines, output limits, and process-tree cancellation. |
| File system | MCP tools are annotated read-only and non-destructive for the source project. Remote checkouts and stored packs live in application-owned managed cache or temporary session storage; the source project is not written. Cache checkout writes require an active lease and a path inside the application-owned repository container. Prepared output storage has one shared 512 MiB quota and requires a 256 MiB free-space reserve. Quota admission or free-space verification failure fails closed; reservations are released on error, cancellation, and disposal. Cleanup does not follow links. Portable-profile fallback staging uses a private `0700` directory and `0600` files on Unix; it rejects a linked managed directory instead of writing through it. |
| Supply chain | Grammar sources and secret-detection rules are pinned and hash-verified. Release channels require content receipts, static completeness checks, mutation gates, and real-entry-point smoke tests before publication. Published container artifacts include build provenance; the other channel-specific evidence is described in the release process. |

The trusted filter and protection lines are reported once per session and then
only when their content changes, with a constant continuation line in their
place. This changes how often an unchanged trusted line is repeated and nothing
else: the lines still carry only fixed text, enum states, and counts, the change
signal is derived from server state rather than assumed, an unprovable state
sends the full set, and the untrusted-data boundary around project text is
unchanged. `list_projects` always reports the complete baseline.

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

The agent journal is local application state and never leaves the machine unless
the user explicitly exports a context receipt. It contains session metadata,
bounded execution parameters, paths, notices, and counts, but no project file contents,
tool-result bodies, search-query text, symbol-selector text, detected
secret values, or masked private-data values. Remaining string values are checked
by the existing secret and private-data detectors before they are queued and are
checked again immediately before Markdown or JSON receipt export. Detected metadata
is replaced rather than exported. Markdown places project-controlled receipt metadata
inside random, balanced untrusted-data boundaries; JSON preserves the documented
schema with the same redacted values.
Journal recording does not change the telemetry-free guarantee: DevProjex does not
transmit the journal or collect it as telemetry.

Application-owned journal and live-session directories must be physical directories;
DevProjex refuses a symbolic link or junction at those service-directory boundaries.
Journal retention considers only a validated session header whose session identity,
process identity, root shape, and exact `.jsonl` file name agree. Invalid or foreign
files are not classified as expired journal sessions and are not deleted by retention.
The Store-data migration likewise refuses linked source, destination, backup, or tree
entries, and it never treats an arbitrary `.tmp` or `.lock` file as one of its own
migration artifacts.

The live-session registry is bounded local discovery metadata, not an authentication
or authorization mechanism. A record is considered discoverable only when its exact
file name names the same PID, the operating system reports the same process start,
and its heartbeat is within the accepted past and future bounds. Record size, entry
count, root count, and string lengths are bounded. Root-jail validation, effective
selection, mandatory content protection, and process ownership remain the enforcing
boundaries; finding a registry record grants no additional authority.

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
