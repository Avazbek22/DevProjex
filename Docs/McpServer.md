# DevProjex MCP Server

DevProjex includes a local Model Context Protocol server for read-only project
inspection and context packaging. It uses standard input/output only; no HTTP or
other network transport is exposed. Remote Git sources are disabled unless the
server starts with `--allow-remote`.

```shell
devprojex mcp --root /absolute/path/to/project
```

The default `--tool-set full` publishes all eight tools, with unchanged instructions
and responses. `--tool-set reduced` publishes exactly `list_projects`, `get_tree`,
`search_project`, `related_files`, `get_file`, and `read_pack`; it omits `analyze`
and `pack_context`. The set is fixed at startup, not changed during a session.
Reduced instructions and catalog guidance do not mention omitted tools. Calling an omitted tool returns
the normal unknown-tool protocol error (`-32602`) naming that tool. `read_pack`
continues to read retained search and dependency results in either set.

`--search-body-chars off|N` fixes the search declaration-body limit at startup:
`N` is an integer from 1 to 16,000 characters, with a default of 1,800;
`off` disables the body. Zero, negative, fractional, and out-of-range values
are rejected before startup. Declaration ranges, match order, selection of the
best declaration, and the total 16,000-character search budget remain unchanged.
The catalog and instructions describe the active limit, or omit body guidance
when disabled. Bodies still use only spare space and their overlapping context;
they do not displace matches from other files.

```shell
devprojex mcp --root /absolute/path/to/project --search-body-chars off
devprojex mcp --root /absolute/path/to/project --search-body-chars 1800
devprojex mcp --root /absolute/path/to/project --search-body-chars 3000
```

## Live Context

`devprojex mcp --root /absolute/path/to/project --live` makes the local profile
saved by the open DevProjex window the baseline for every call. The server rereads
that profile on every tool invocation, so changing checked tree nodes or applied
filters takes effect on the next call without restarting the MCP session. Three
rules stay separate: Checked nodes are focus for selection-wide tools;
extensions, ignores, Git scope, the root jail, allowlists, and mandatory secret
protection remain the **access boundaries**; and **saved results** stay pinned to
the revision that created them until the originating tool rebuilds them. Explicit
`paths` and globs can only narrow the saved focus. `profile: "local"` is equivalent
to omitting `profile`; portable and standard profile selections fail with
`DPX-MCP-INVALID-ARGUMENTS` because live mode has one selection source.

Tree, search, pack, analysis, and dependency results stay inside the checked
selection. A named `get_file` path that passes the effective filters may be read
outside that focus. The fixed notice remains trusted, while the requested path
stays in the existing untrusted file header:

```text
[Live context] the named path is outside the current window selection; returned because you named it. Tree, search, pack and related stay within the selection.
```

A batch uses the same wording with a count, for example
`[Live context] 2 named paths are outside ...`; it never repeats those paths in
trusted text.

A scalar path hidden by the effective filters still returns
`DPX-MCP-PATH-NOT-FOUND`. In a batched read, that range is reported as
`unavailable — outside effective selection` while the remaining ranges continue.
Every live response ends with the current per-root revision and the selected
file count from the latest plan built for that root. Before the first plan is
built, the count is `0`. A multi-root server identifies the root by a stable
ordinal in trusted text and places its project-controlled name in an untrusted
data block:

```text
[Live context] revision 16 · 128 files selected in the window
[Live context] revision 16 · 128 files selected in the window · root 1 of 2

Live context root 1 name:
project-name
```

The first response after the saved profile changes, including an error result,
also reports the frontier delta. Trusted text contains only counts and fixed
selection kinds; up to five added or removed names are written in ordinal order
inside a separate untrusted data block. If more names changed, the trusted line
reports how many names were shown and how many remain:

```text
[Live context] changed since revision 14: +2 folders, -1 file
[Live context] changed since revision 14: +7 folders, -2 files · 5 names shown, 4 more
[Live context] changed since revision 14: selection settings changed

Live context changed paths since revision 14:
+docs/api
+tests
-src/legacy
```

A transition away from the full-tree state uses `-all`; a transition back to
the full tree uses `+all`:

```text
[Live context] changed since revision 14: -all, +2 folders
[Live context] changed since revision 14: -1 folder, +all
```

Revision 1 is the first profile read in a server session. It advances whenever
the saved frontier, extensions, ignores, Git mode, transformations, or saved
secret marks change. Revisions are maintained independently for each configured
root. The marks captured with a live profile remain authoritative for that
invocation even when the configured root is an alias whose physical path has a
different spelling, such as `/var/tmp` and `/private/var/tmp`. A missing or
explicitly empty selection is reported rather than silently treated as an
ordinary empty project:

```text
[Live context] no window selection saved for this root; using server defaults.
[Live context] no window selection saved for this root; using server defaults. If the DevProjex window runs on Windows, live context across WSL is not supported yet.
[Live context] the window selects no files; tick files in the DevProjex window.
```

If the profile is locked, malformed, otherwise unreadable, or uses an unsupported
future schema, the server retains the last successful snapshot and adds:

```text
[Live context] saved window selection could not be read; using revision 16. Retry this call.
```

If the first read fails before any successful snapshot exists and no usable
backup is available, the tool fails with `DPX-MCP-PROJECT-UNAVAILABLE`, advises
the caller to retry, and does not silently use server defaults. Its live notice is:

```text
[Live context] saved window selection could not be read; retry this call.
```

A usable backup initializes revision 1 instead. A genuinely absent profile is
different: it uses server defaults and emits the documented `no window selection
saved` line.

`pack_context` records the revision used to build a pack:

```text
[Live context] pack built at revision 14.
```

`read_pack` never rebuilds content implicitly. After the selection changes it
names the tool that created the stored result, if that tool exists in the active
catalog. Full-set examples are:

```text
[Live context] pack built at revision 14; window is at revision 16. Call pack_context again to include the current selection.
[Live context] search result built at revision 14; window is at revision 16. Call search_project again to include the current selection.
[Live context] related-files result built at revision 14; window is at revision 16. Call related_files again to include the current selection.
```

The reduced set never recommends its omitted `pack_context` tool; an old pack
notice then reports the revision mismatch without an unavailable next call.

These trusted lines supplement rather than replace `[Search boundary]`,
`[Resolution]`, `[Dependency partial parse]`, and `[Effective filters]`. Trusted
live lines contain only fixed words, revision and count values, and fixed enum
states. Root names, file and folder names, and paths remain inside the same
randomized untrusted-data boundary used for other project-controlled text.
The server records a live-session heartbeat under the application state root in
`live-sessions/<pid>.json`. Desktop and Terminal remove records whose process
identity no longer matches or whose heartbeat is older than 15 seconds; a
healthy server updates every 5 seconds and removes its record on normal exit.

The Release process measurement on Windows x64 gives 27,710 characters for the
full `tools/list` result and 17,049 for reduced. These correspond to roughly
6,928 and 4,262 tokens using the character/4 estimate, not model usage. Process
budgets are 27,900 and 17,500 characters respectively.

Repeat `--root` to expose more than one project. When no explicit root is given,
DevProjex uses `DEVPROJEX_ROOT`, then `CLAUDE_PROJECT_DIR`, then the current
directory. A `project` argument is optional only when the server has exactly one root.

Private-data redaction is opt-in at server startup:

```shell
devprojex mcp --root /absolute/path/to/project --hide-private-data
```

Remote repository URLs are a separate startup opt-in:

```shell
devprojex mcp --root /absolute/path/to/project --allow-remote
```

To keep that opt-in limited to exact Git hosts, add a repeatable or
comma-separated allowlist:

```shell
devprojex mcp --root /absolute/path/to/project --allow-remote --remote-hosts github.com,gitlab.com
```

Without `--remote-hosts`, `--allow-remote` retains its unrestricted-host
behavior. `list_projects.baseline.remote` reports both the network opt-in and
the normalized active host list.

The server baseline Git mode can be selected at startup with
`--git-mode none|gitignore|tracked`. This applies only when a tool does not name
an explicit profile. Momentary Git state belongs to request-level `git_scope`
and is intentionally rejected at server startup. `off` is accepted as an input
alias for the canonical `none` token.

Without a Git flag the baseline is `gitignore`, the standard profile's mode.
It includes repository-local `info/exclude` and treats undeclared embedded
repositories as opaque; initialized declared submodules own their rules
recursively. Worktrees resolve the shared exclude file through `commondir`.
These rule sources are read as files without starting Git. Global excludes
remain outside the contract. See [SmartIgnore.md](SmartIgnore.md) for the full
specification; `--unrestricted` restores embedded-repository visibility.

The exclusion baseline is deliberately narrower than the desktop standard set.
A server started without exclusion flags runs with `smart-ignore` and
`empty-folders` only, so the agent sees the repository the way Git sees it:
dependency and build trees are gone, while dot-files, dot-folders,
extensionless files, hidden entries, and empty files stay visible. Those
toggles exist for a person who can see what a checkbox hides; an agent cannot,
and a hidden `Dockerfile`, `.github/` workflow, `.env.example`, or empty
`__init__.py` becomes a confident wrong answer about the project. `list_projects`
reports this baseline, and `analyze` echoes the effective set on every call.

The baseline exclusion set is selected at startup with `--exclude <NAME>`
(repeatable). The names are the exclusion tokens the CLI and TUI already speak:
`smart-ignore`, `empty-folders`, `empty-files`, `hidden-folders`,
`hidden-files`, `dot-folders`, `dot-files`, and `extensionless-files`, plus two
MCP-only tokens: `none` starts the server with every toggle off, and `default`
expands to the server default set so a line can extend it instead of re-listing
it — `--exclude default --exclude dot-folders` is the default set plus
`dot-folders`. Any list without `default` replaces the default set, the same
rule the CLI `--exclude` follows; `none` cannot be combined with another token.
Like the Git baseline this applies only when a tool does not name an explicit
profile. Redaction toggles are not exclusions
and are rejected at startup. The `hidden-folders` and `hidden-files` toggles
follow the platform hidden attribute (the Windows Hidden attribute or macOS
`UF_HIDDEN`); on Unix-like systems dot-named entries belong to the
`dot-folders` and `dot-files` toggles instead — see the dot-name ownership
note in [SmartIgnore.md](SmartIgnore.md). Linux has no separate hidden
attribute, so on Linux the hidden toggles on their own exclude nothing.
A selected file the process cannot read (for example, permission-denied)
degrades per file like other uninspectable content: it is withheld with an
uninspected-content notice instead of failing the whole call.

The widest baseline has a one-flag preset:

```shell
devprojex mcp --root /absolute/path/to/project --unrestricted
```

`--unrestricted` starts the server with every exclusion toggle off and the Git
baseline set to `none` — equivalent to `--exclude none --git-mode none`, and
rejected in combination with either flag. It widens visibility only: secret
redaction still applies to every response, and per-call arguments and profiles
behave exactly as they do for the spelled-out form. The `.git` administrative
area is a product boundary like symbolic links: it stays excluded even at this
widest baseline.

Per-call exclusion control by the agent is a separate startup opt-in:

```shell
devprojex mcp --root /absolute/path/to/project --allow-agent-exclusions
```

With the flag, the selection tools `get_tree`, `analyze`, `pack_context`,
`search_project`, `related_files`, and `get_file` gain an `exclusions` array parameter that
carries the full desired toggle set for that call; an empty array turns every
toggle off, and the value outranks both the server baseline and profile
exclusions. Tokens match case-insensitively and duplicates are rejected.
Without the flag the parameter does not exist in any schema and is rejected as
an unknown argument, so a default server keeps its startup-controlled exclusion
contract unchanged. Turning toggles off widens the per-call scan to trees the baseline
skips (subject to the Git baseline), so enable this delegation only for
agents you trust with full-project walks. Both the startup baseline and the
delegated set apply to opt-in remote checkouts exactly as they do to local
roots.

Delegation alone stays bounded by the startup Git baseline: the agent has no
parameter that lifts Git filtering, so even `exclusions: []` cannot surface
gitignored files on a default server. Pairing the two startup flags is the
full-reach recipe for a trusted agent:

```shell
devprojex mcp --root /absolute/path/to/project --unrestricted --allow-agent-exclusions
```

Because the parameter is a full desired state, growing the exclusion
vocabulary in a future version is a compatibility checkpoint: a token absent
from a replayed full-state array is turned off, including tokens the caller
predates. The supported way to build a full-state value is read-modify-write —
call `analyze`, copy its echoed `exclusions` array, edit, and send; the echo
uses the same tokens and stays valid across versions. A `paths` entry that the
effective exclusion set hides yields an empty selection rather than an error,
so check `analyze.files` when combining `paths` with exclusions.

Project discovery is conditional. With exactly one configured local root, omit
`project`, use project-relative paths, and begin with the project operation that
answers the question. Use `list_projects` for profiles and the active policy, not
only to learn that root's name. With several roots, use `list_projects` when the
project is not already known.

A typical multi-root sequence is:

```text
list_projects -> get_tree/analyze -> search_project/related_files/get_file -> pack_context -> read_pack
```

The MCP `initialize` result publishes the same workflow in `instructions`, along
with the redaction placeholder grammar and allowlisted example classes, the
trusted/untrusted boundary, response limits, and the segment-aware glob rules.
Tool descriptions remain self-contained because some clients do not display
server instructions; each description states the tool's purpose, when to use a
named alternative, its result, and its key limit.

Use `get_tree` for structure without content, `analyze`
for transformed size and token estimates before packing, `search_project` for
textual locations, `related_files` for statically evidenced relationships,
`get_file` for one file page, and `pack_context` for multi-file context. A large
pack returns an id that `read_pack` pages; `read_pack` does not recreate expired
packs. When a step wants more than one file or more than one range, send one
batched `get_file` call instead of several single reads; see
[Search, then one batched read](#search-then-one-batched-read).

## Agent journal

Each MCP server session writes a local agent journal. A session records the client
name and version, Standard or Live mode, configured roots, tool set, server version,
start and end times, and aggregate counts. Each call records the tool name, a bounded
set of address and mode arguments, profile revision, duration, result-character and
estimated-token counts, delivered relative paths, masking counts, fixed notice codes,
and an error code when the call failed. The journal does not store file contents,
tool-result bodies, detected secret values, or masked private-data values.

Journal files live under the per-user DevProjex state directory in its
`agent-journal` folder. Retention keeps at most 200 sessions and removes entries
older than 30 days. **Journal…** in the desktop MCP menu, `mcp log` in Terminal
Workspace, and the CLI journal commands read the same records. Clearing can be
limited to the current project; Terminal Workspace refuses to clear a journal
while a Live session for that project is active.

A context receipt is a Markdown or JSON snapshot of one session. It contains the
session metadata, totals, delivered-path counts, and call rows, so a user can retain
evidence of what context was made available without retaining the returned file
bodies. GUI, Terminal Workspace, and CLI exports use the same receipt formatter.
Per-call storage is queued after the result has been accounted for; journal file I/O
is not awaited on the MCP tool response path. On a 400-file latency probe with 120
repetitions per arm, median call time was 1.784 ms without recording and 1.820 ms
with recording. The 0.036 ms difference was below the baseline spread.

## Security Model

- Every local project is pinned when the process starts. With `--allow-remote`, a
  remote checkout is pinned on first use and its RepoCache session lease remains
  held until the server stops. Canonical path and symbolic-link checks reject
  access outside those roots. Content files are opened before their final handle
  path is validated, and the validated handle is the one read, so a path swap
  cannot escape the root jail.
- Without `--allow-remote`, tools perform no network operations, and no probe leaves
  the machine before that permission is considered. A `project` value is classified by its
  spelling alone, and a form that names a host is refused with `DPX-MCP-INVALID-ARGUMENTS`
  before anything opens it: `\\server\share` and `//server/share`, the UNC device path
  `\\?\UNC\server\share`, anything in the NT object namespace `\??\`, and the automount
  host maps `/net/server/…` and `/Network/Servers/server/…`. Opening such a path is itself
  the network operation: the operating system contacts the named host in order to answer, so
  a check that first asked whether the path existed would already have sent the traffic it was
  meant to prevent. The refusal applies with `--allow-remote` as well — a remote project is
  named by its URL, and these spellings are refused in both states.
- Three device forms address this machine and are not refused: a drive, as in
  `\\?\C:\project`, which is an ordinary local directory written the long way; a volume,
  `\\?\Volume{…}\project`; and a named pipe, `\\.\pipe\name`. Every other device is refused,
  and so is a relative segment inside an accepted one, because `\\.\` is normalised before the
  operating system reads it and `..` would otherwise put any device in the accepted one's place.
- A root listed at startup stays addressable in any spelling that resolves
  to the recorded one — a trailing separator, forward slashes, or the extended-length prefix —
  because deciding that compares strings and opens nothing. What the refusal cannot cover is a
  drive letter, a mount point, or a working directory that the operator has already bound to a
  remote share: by its spelling it is indistinguishable from a local path, and that binding is
  the operator's own configuration rather than something a client chose.
- With `--allow-remote`, network access is limited to the RepoCache clone/acquire step
  for a Git URL; all project inspection handlers operate only on the pinned checkout.
  With `tracked_only` or `git_scope`, the server may also start the local Git
  executable solely to read repository state. It never runs project executables
  or arbitrary project commands.
- Remote network sources use HTTPS, SSH, or SCP syntax. Query
  strings and fragments are rejected so credentials cannot enter Git process
  arguments. When `--remote-hosts` is present, network URLs and SCP forms must
  use one of its exact normalized hosts. `http`, `git` and `file` URLs are refused: a built product
  accepts a repository URL only over `https` or `ssh`, including the SCP form, and nothing in the
  environment or the call widens that set.
- A server session pins at most 16 distinct remote URL-and-branch sources. Existing
  keys are reused and valid sources are never evicted; exceeding the cap returns
  `DPX-MCP-REMOTE-LIMIT` with guidance to reuse a source or restart the server.
- Secret redaction is always enabled for returned file content and cannot be
  disabled. Private-data redaction is disabled by default and can be enabled
  for the whole server process with `--hide-private-data`, mirroring the CLI
  flag. In live mode the saved local profile can also enable it; the effective
  policy is startup **or** live profile, so a call cannot turn either source off.
  Tool schemas intentionally expose no redaction controls.
  This guarantees that an agent cannot disable the redaction pass; detection
  itself covers common secret formats but remains heuristic, not a guarantee.
  Review each pack before publishing it outside your environment.
  Some known documentation and placeholder values in provider rules, such as
  AWS-shaped keys containing `EXAMPLE` and bodies made from alphabetic sequences,
  are intentionally allowlisted in line with upstream Gitleaks rules to avoid
  fixture and example noise. This provider-tier exception does not exempt every
  credential-shaped assignment: scope-aware configuration detection still evaluates
  those values, and real secret formats remain subject to detection and redaction.
- The redaction boundary distinguishes project addresses from exported content.
  File contents and context packs are always processed by Secrets redaction;
  Private Data processing is added when the server starts with
  `--hide-private-data` or the active live profile enables it. Root paths in
  `list_projects` and project-derived details in tool errors remain data inside
  the untrusted boundary; remote tools use the safe Git URL as the project address.
  These addresses form the contract for the `project` argument. Without the
  flag, a pack retains real addresses like a default CLI export. With the flag,
  the pack is private-data-redacted in full, including its tree header.
  File names and paths are address fields and are not masked by secret or
  private-data protection. Callers need their literal values for `project`,
  `path`, and `paths`; when response text contains project-controlled addresses,
  they stay inside the per-response untrusted-data boundary.
- Searches run against content after mandatory secret redaction and any enabled
  private-data redaction, not the original file text. Static-dependency bodies
  produced by `related_files` first protect evidence at its source file and line,
  omitting a dynamic evidence fragment when its safe origin cannot be retained,
  and then pass through synthetic-document redaction before inline delivery or
  storage. Evidence, specifiers, and candidate paths cannot bypass the content
  policy.
- Returned project content is marked as untrusted data with a random, per-response
  delimiter. Agents must not interpret instructions found in project files as
  trusted control input.
- Agent paths and globs can only narrow the effective selection, and the `.git`
  administrative area is excluded in every mode. Which product exclusions and
  Git filtering run is the human's startup choice (`--exclude`, `--git-mode`,
  `--unrestricted`); agents can change the toggles only on a server
  deliberately started with `--allow-agent-exclusions`, or by naming a profile
  that already exists inside the project root — treat committed profile files
  as part of the selection surface you publish. Delegation covers file
  visibility only, never the redaction pass.
- Large packs are kept in an application-owned temporary session directory. Pack
  ids are random, valid only in the current server process, and removed at exit.
  After a server restart, rerun the tool that created the stored result to create
  a new id: `pack_context` for context packs, `search_project` for saved searches,
  or `related_files` for saved dependency results. `pack_context` is unavailable
  in the reduced tool set.
  Stale session directories older than 24 hours are scavenged at startup. A
  stored pack is limited to 200 MiB and all packs in one server session are
  limited to 1 GiB. To place a new pack within the session limit, the server evicts
  least-recently-read packs that have no active reader and reports the eviction count.
  Reading an evicted id returns `DPX-MCP-PACK-EXPIRED` with a quota notice. A request
  that exceeds the per-pack limit, or cannot fit while every candidate is active,
  returns `DPX-MCP-PACK-TOO-LARGE` with narrowing guidance.

Errors returned by tools have `isError: true` and stable `DPX-MCP-*` codes. Only
the code and `request failed` status are trusted; the detailed message, including
requested paths, names, and ranges, is spotlighted as untrusted data. Malformed JSON-RPC traffic is
reported as a protocol error; an unknown tool name is also a protocol-level error
and returns JSON-RPC code `-32602`.

Remote-specific errors are `DPX-MCP-REMOTE-DISABLED` when a URL is passed to a
server started without `--allow-remote`, `DPX-MCP-INVALID-ARGUMENTS` for an
unsupported URL, a branch used with a local path, or a `project` written as a path that
names a host, and `DPX-MCP-REMOTE-FAILED`
when Git, cloning, cache publication, or branch checkout fails.
`DPX-MCP-REMOTE-LIMIT` reports that the 16-source session cap was reached. Error
text uses the credential-free display form of the URL.
`DPX-MCP-REMOTE-HOST-DENIED` reports that an otherwise valid URL is outside the
optional startup host allowlist without echoing the rejected host.

### Redaction placeholders

Secrets are replaced before text is returned with placeholders shaped as
`DEVPROJEX_REDACTED[<category>#<n>]`; `search_project` searches that already
redacted text. Known example values, including documentation domains, 555
numbers, keys containing `EXAMPLE`, and reserved IP ranges, are intentionally
allowlisted to keep documentation and fixtures readable.

## Tools

The tool order is stable. Every tool is annotated read-only and non-destructive
because none modifies the source project. `pack_context` and `related_files` are
non-idempotent because either may create a stored result with a new session id. Without `--allow-remote`, every tool is
closed-world; with it, the six tools that accept `project` Git URLs are annotated
open-world.

Discovery is conditional rather than a mandatory sequence. Use `list_projects` only
when the project is unknown, `get_tree` or `search_project` when its location is
unknown, scalar `get_file` for one known location, and batch `get_file` for several
independent known locations. `pack_context` is for a multi-file document, while
`analyze` is useful when sizing or admission must be known before producing one.
Every tool publishes one short `anthropic/searchHint` in `_meta` as optional discovery
help. No tool publishes `anthropic/alwaysLoad`; clients that do not recognize the hint
can ignore it without changing any route or result.

### What a connection costs

Before a client asks anything about a project it has already paid for the tool schemas and the
server instructions. Measured on 2026-09-13 from the characters a client received:

| Payload | Characters |
|---|---:|
| `tools/list` normalized result, default server | 26,953 |
| `tools/list` C# client wire result, default server | 27,619 |
| `tools/list` C# client wire result with per-call exclusions | 30,661 |
| `instructions` | 1,145 |

Removing the two output schemas reduced the default catalog from 42,370 characters at the
base revision to 32,516 before the named batch-read selector and discovery hints were added.
Concise descriptions now keep the final catalog at 26,953 characters without changing schema
types, accepted values, bounds, or tool behavior. `pack_context` is the largest single tool at
5,614 characters, including 4,737 characters of input schema. The `exclusions`
parameter costs a flat 3,042 characters, 507 on each of the six tools that take it. A process
test holds the default `tools/list` result and the instructions under ceilings with deliberate
headroom, and pins the exclusion parameter's cost as an exact difference, so a new parameter or
description has to fit a budget rather than grow one silently.

| Tool | Parameters | Result and limits |
|---|---|---|
| `list_projects` | none | Session inventory used for profiles, active policy, or choosing among several projects: allowed local roots with path, name, type, and profiles, plus the server `baseline`. The profile database is read once per call and `profilesStatus` reports an unavailable bounded read. The baseline reports secret/private-data policy and the optional remote-host allowlist. With one local root, project tools accept an omitted `project`; otherwise they accept a unique listed name or its absolute path. Remote projects are addressed by URL and are not added to this list. |
| `get_tree` | `project?`, `branch?`, `paths?`, `include_patterns?`, `exclude_patterns?`, `tracked_only?`, `git_scope?`, `max_file_bytes?`, `max_depth?`, `format?` | Effective tree in `markdown` (default), `text`, `json`, or `xml`; at most 2,000 lines and 50,000 characters. Select several directories in one call with a brace pattern such as `include_patterns: ["src/middleware/{powered-by,body-limit,bearer-auth}/**"]` instead of walking each directory separately. Without `max_depth`, a large human-readable tree uses the deepest complete depth that fits. |
| `analyze` | `project?`, `branch?`, `paths?`, `include_patterns?`, `exclude_patterns?`, `profile?`, `detail?`, `detail_by_pattern?`, `tracked_only?`, `git_scope?`, `top_files?`, `max_file_bytes?`, `max_tokens?`, `rank?`, `focus?` | File, character, and token metrics plus the requested largest files by tokens. `contentMetrics` separates measured transformed bodies from size-based estimates; `documentMetrics` models `pack_context` with `view=content`, `format=text`, relative file headings, and its Root line. Every ranked file carries `estimated`; an uninspected one also carries `uninspected: true`. The `topFiles` array has a 32,000-character aggregate budget; `topFilesTruncated` and `topFilesRemaining` make any omission explicit. With `max_tokens` the result also carries `admission`: which files that budget would admit, from the same greedy pass `pack_context` uses and without producing content. `rank` and `focus` order that admission and are invalid without `max_tokens`. |
| `pack_context` | `project?`, `branch?`, `paths?`, `include_patterns?`, `exclude_patterns?`, `profile?`, `detail?`, `detail_by_pattern?`, `tracked_only?`, `git_scope?`, `max_tokens?`, `rank?`, `focus?`, `max_file_bytes?`, `view?`, `format?` | Exact DevProjex context pipeline. `max_tokens` measures the safe transformed selection, applies the ordinary token admission order, and materializes only admitted content without changing the budget report. `rank: "importance"` opts into importance-aware admission and document order; `focus` seeds graph-hop order within it. `detail_by_pattern` overrides `detail` per file. Inline through 50,000 characters; otherwise returns a `pack_id` valid until this server process exits. After restart, call `pack_context` again. |
| `read_pack` | `pack_id`, `start_line?`, `end_line?`, `start_column?` | Pages a stored result from `pack_context`, `search_project`, or `related_files`. Inclusive, 1-based line range; `start_column` continues within `start_line` using 1-based Unicode characters. At most 1,000 lines or 50,000 characters per call. An `end_line` after EOF is clamped and reported. Call the originating tool again after server restart or quota eviction. |
| `search_project` | `project?`, `branch?`, `pattern`, `paths?`, `include_patterns?`, `exclude_patterns?`, `tracked_only?`, `git_scope?`, `max_file_bytes?`, `context_lines?`, `ignore_case?`, `max_results?` | Matches over safe transformed text, grouped by file: the relative path stands on its own line, then each line of the group is written as `line:text` for a match and `line-text` for context. Line numbers refer to that returned text after replacements. `search_project` matches file content only and never matches paths; use `get_tree` with `include_patterns` to find files by name. A bounded collector keeps stronger evidence from everything inspected instead of preserving arrival order. The returned match text is capped at 16,000 characters. Overlapping or adjacent context windows are merged and distinct groups use `--`. Regex patterns are limited to 4,096 characters and a 2-second timeout; `max_results` cannot exceed 200. The trusted `[Search boundary]` line distinguishes a complete result from every partial limit and reports inspected sources, encountered and retained matches, written matches, named declaration files, and continuation guidance. Actual text inserted by redaction never matches. |
| `related_files` | `project?`, `branch?`, `path`, `direction?`, `include_patterns?`, `exclude_patterns?`, `profile?`, `tracked_only?`, `git_scope?`, `max_file_bytes?` | Statically evidenced dependencies and dependents for one seed or up to 16 seeds. `direction` is `dependencies`, `dependents`, or `both` (default). The trusted `[Resolution]` line counts resolved, ambiguous, unresolved, and external edges for the call. Coverage distinguishes recognized supported languages from unsupported files and reports configuration diagnostics. Results larger than 50,000 characters use `read_pack`. |
| `get_file` | `project?`, `branch?`, `profile?`, either `path` with `start_line?`, `end_line?`, `start_column?`, `symbol?`, or `requests` | Redacted text from one effective file or a batch of up to eight file requests and sixteen file selections. Every returned section starts with its path and returned line interval. A batch item with only `path` reads the whole file; `ranges` or `symbol` narrows it. Ranges are inclusive, each physical file is read and redacted once, overlaps merge, and every item reports `ok`, `partial`, `not-returned`, or `unavailable`. Both forms share the 1,000-line/50,000-character limit. Coordinates refer to returned text after replacements. A non-empty file that cannot pass the 16 MiB mandatory-redaction boundary is withheld; the single form returns `DPX-MCP-PAYLOAD-TRUNCATED` and never returns an empty success, while batch output reports the count-only unavailable status. `profile` applies the same effective selection and transformations as `analyze` and `pack_context`. Markdown-escaped names copied from default `get_tree` are accepted (`\_` and other ASCII punctuation); use `format: "text"` to copy unescaped names. |

On a server started with `--allow-agent-exclusions`, `get_tree`, `analyze`,
`pack_context`, `search_project`, `related_files`, and `get_file` additionally accept the
`exclusions` array parameter described in the startup section, so a file
revealed by a per-call value stays readable through the same value.

For `get_tree`, `analyze`, `pack_context`, and `search_project`, `paths` accepts
at most 256 entries and each
entry is limited to 4,096 Unicode scalar values. Lexically equivalent entries are
deduplicated before root-jail resolution; every unique path still passes the full
physical containment check. The entries name literal files or directories, so
`*`, `?`, `{`, and `[` have no glob meaning in `paths`. The selection is narrowed
before intersection with patterns, Git scope, and `max_file_bytes`; a missing entry
adds the count-only `DPX-SELECTION-PATH-MISSING` warning while valid entries continue,
and an empty intersection remains empty. Regex, glob, and `git_scope` schema lengths use the
same Unicode scalar-value semantics at runtime.

### `related_files`

`related_files.path` is one seed path or an array of at most 16 seed paths. A seed
selects the starting point for the answer; it does not promise that only the seed is
read. The server first builds the complete effective manifest through the same
baseline, profile, Git scope, exclusion, glob, and file-size pipeline as the other
selection tools, without narrowing that manifest to the seed paths. Each seed is then
resolved with the same path checks and case-correction guidance as `get_file`. A seed
outside the effective selection returns the existing `DPX-MCP-PATH-NOT-FOUND` error.

The human-readable payload has `Dependencies` and `Dependents` sections according to
`direction`. Each line contains a project-relative path, one or more evidence reasons
separated by ` · `, a `resolved` or `ambiguous` status, an estimated token count, and
an optional `cross-scope` marker. One ambiguous reference is one row with its bounded
candidate list; when Bash suffix matching exceeds its 32-path cap, the reason states how many
matches are shown and how many exist. Self-file edges are omitted. For example:

```text
Dependencies:
Application/Context/ProjectContextPlan.cs — one visible declaration identity in csharp:Apps/Mcp/DevProjex.Mcp.csproj · type reference ProjectContextPlan at line 18 — resolved — 642 tokens — cross-scope
Models/Alpha/User.cs — multiple visible declaration identities · type reference User at line 27 — ambiguous — 84 tokens — candidates: Models/Alpha/User.cs, Models/Beta/User.cs
Dependents:
Apps/Terminal/Execution/AnalyzeCommandHandler.cs — one visible declaration identity in csharp:Apps/Terminal/DevProjex.Terminal.csproj · type reference ProjectContextPlan at line 44 — resolved — 1830 tokens — cross-scope
```

Project-derived paths and evidence remain inside the random `untrusted-data` block.
Evidence fragments are checked against the protected text of their original source
file before formatting. If a source-bound manual mark covers that source line, or
the source cannot be recovered safely, the reference text is omitted while its
fixed evidence kind, safe line locator, resolution status, and target row remain.
The finished synthetic document is redacted again as a final independent pass.
Outside that block, the server appends `[Facts coverage] files=N, supported=N,
unsupported=N, extraction-failed=N` with a short explanation that supported files
produced facts for a recognized language while unsupported files had no extractor.
Configuration state is summarized outside the block as `[Dependency configuration]
problems=N · missing=A · corrupt=B · unsupported-semantics=C · affected-scopes=M`.
At most eight `path · problem` rows plus an `and N more` row stay inside the
untrusted block; parser reasons are not returned. After relation rows, the same block includes
up to eight `[Dependency partial parse] path=... · dropped=N · lines=...` rows when recognized
syntax was discarded. No partial-parse row is added when the manifest has no discarded syntax,
and the fixed response and stored-result limits still apply. `[Search scope] files=N` and the ordinary
`[Effective filters]` trailer follow. `[Resolution] resolved=N · ambiguous=M ·
unresolved=K · external=E` counts the selected-direction edges considered for the call.
A seed without facts is a successful empty result with
its path and any nonstandard explanation inside the untrusted block. One of the six fixed
dependency-engine status constants is repeated without a path in the trusted line
`[No facts] <constant>.`; file extensions and arbitrary project text never enter that line. A
call with no resolved edges receives trusted `[No related files] in the effective
selection.`; when unresolved references exist, that same line reports their count.
No trailer names a file hidden by the manifest. See
[Dependencies.md](Dependencies.md) for evidence layers, statuses, resolver boundaries,
limits, caching, and determinism.

The dependency graph cache is metadata-keyed. It reuses extracted facts for the
same canonical manifest paths and unchanged file identities, while configuration
fingerprints cover applicable project files, project references, TypeScript and
package maps, Python configuration, and Git-shaped selection. A manifest change,
a source identity change, or a change to an observed control file invalidates the
affected snapshot. Changing only `related_files` seeds or `direction` does not;
those are projections over the same immutable index. This is an optimization,
not a strict filesystem snapshot mode, and no strict cache switch is offered.

### `pack_context.expand_related`

`expand_related` packs the seeds together with their statically resolved
neighbours, so the documented `search_project` to `related_files` to
`pack_context` sequence becomes one call. It takes `seeds` (1 to 16
project-relative files), `hops` (1 or 2, default 1), and `direction`
(`dependencies`, `dependents`, or `both`, default `both`, the same enum
`related_files.direction` uses). `related_files` keeps its own purpose: it shows
evidence, resolution status, and candidate lists, and lets a reader choose
neighbours by hand.

**Expansion only ever narrows.** The neighbourhood is computed over the dependency
index built from the plan's own included files, which is the set the baseline,
profile, exclusions, Git scope, `tracked_only`, `paths`, the glob parameters, and
`max_file_bytes` already produced. A file those filters keep out is not in the
index, so no value of `expand_related` can reach it, and an excluded file cannot
act as a bridge: if the only route from a seed to a second-hop file runs through a
file the filters hid, that second-hop file is not packed. Only `Resolved` edges
travel; an `Ambiguous` or `Unresolved` reference names a file the engine would not
commit to, and following it would put a guess in the pack.

A seed must be a file inside the effective selection. A directory, a glob, or a
path the filters hide returns the existing `DPX-MCP-PATH-NOT-FOUND` error naming
the effective filters, which is the same answer any other unselected path gets.

Two hops do not bound the result: one widely imported file can reach most of a
repository. The expansion therefore stops at 400 files, counting the seeds, and
takes each hop in ordinal path order so the admitted prefix is deterministic rather
than an arbitrary choice between neighbours.

Every call that expanded reports one trusted line of counts and constants:

```text
[Expanded] seeds=1 · hop1=+11 · hop2=+0 · seeds-without-facts=0.
```

and, when the limit decided the answer, `; stopped at the 400-file expansion
limit, so the neighbourhood is incomplete.` The paths themselves stay in the
untrusted block with the rest of the pack, as every project path does.

Expansion selects the candidate set; it does not order it. Without `rank` the pack
keeps canonical path order, and with `rank` the existing ranking orders what
expansion admitted. To put particular files first under a binding `max_tokens`,
combine `expand_related` with `rank` and `focus`: a `focus` seed the expansion
admitted is ranked first as usual, and one it did not admit is a path outside the
selection and returns `DPX-MCP-PATH-NOT-FOUND`. Without `expand_related` every
response is byte-identical to a server without it.

## Result Contract

`list_projects` and `analyze` return their JSON object only as spotlighted text
inside `content`; they deliberately omit `structuredContent` and `outputSchema`.
Some clients discard all text whenever `structuredContent` exists, which would
also discard the untrusted-data boundary around repository-controlled names and
paths. The protected text is therefore the authoritative representation.
`analyze` results include an `exclusions` array that echoes the exclusion
tokens effective for the call, so the agent and a human reading the transcript
always see which toggles shaped the measurement. `list_projects` results
include a `baseline` object with the server `git` mode token, the baseline
`exclusions` tokens, and an `agentExclusions` flag. Both fields are new in v5.2
and required on every server, including servers started without the exclusion
flags; consumers must parse the spotlighted JSON text rather than expect a
structured MCP result.
`analyze.topFiles[].estimated` is required and marks whether an entry came from
size-based rather than inspected transformed-content metrics.
`analyze.topFiles[].uninspected` is an optional v5.2 addition: it is present and
`true` only when mandatory bounded inspection could not read that file and its
size-based metrics are estimates. `contentMetrics.measured` reports file, line,
character, and token totals only for inspected transformed file bodies;
`contentMetrics.estimated` separately reports the count, characters, and tokens
of estimated files. `documentMetrics` reports the canonical content/text pack
shape, including the Root line and project-relative file headings; its
`estimated` flag is true if any body is estimated or lacks text metrics. The legacy aggregate
`characters` and `tokens` retain their v5.2 rendered-estimate meaning.
The aggregate serialized `topFiles` content is limited to 32,000 characters.
When the requested count exceeds that budget, `topFilesTruncated` is `true` and
`topFilesRemaining` reports the exact number omitted. Every `analyze` result also
contains `protection`, whose `secrets` value is always enabled and whose
`privateData` value reflects the effective startup-or-live-profile policy. A
remote result adds `remote.commit` and `remote.branch` from the pinned checkout session.
`analyze.admission` is an optional v5.2 addition present only when `max_tokens` was
supplied. It reports which files that budget would admit, from the same first-fit
greedy pass `pack_context` uses, and produces no context document, no prepared file,
and no stored pack. It carries `budget`, `includedFileCount`, `skippedFileCount`,
`includedEstimatedTokens`, `skippedEstimatedTokens`, `includedFiles`,
`includedFilesTruncated`, `additionalIncludedFileCount`, `includedOrderDigest`,
`skippedFiles`, `additionalSkippedFileCount`, and `detail`. `includedFiles` is a
prefix of the admission order, bounded by 1,000 entries and by a 32,000-character
aggregate budget; the flag and the count say what was cut. `skippedFiles` follows the
pack report shape: the 25 largest skipped files plus, with `rank`, the 10
highest-priority skipped ones. `detail` is the effective default level the admission
was measured at; a per-file level appears on each skipped entry when
`detail_by_pattern` was supplied. The reported per-file level is the level resolved for
that file, not a claim that a transformation succeeded: an unsupported language, a
binary, or a file past the inspection boundary still reports its resolved level and
still ships unchanged, exactly as the `detail` parameter behaves generally.

`admission` paths are project-relative. A pack renders its own paths according to its
`view` and `format` - JSON and XML on a remote checkout use the safe repository URL -
so compare the two by `includedOrderDigest` and the counts rather than by string
equality of paths. Together with the existing `topFiles` budget, a fully populated
`admission` can roughly double an `analyze` reply; `analyze` has no paged fallback, so
prefer a smaller `top_files` when previewing a large selection.

For the same content snapshot, configuration, filters, and effective transforms, the
admitted set of `analyze` equals the set `pack_context` admits. `includedOrderDigest`
is a hash of the complete ordered admitted list of project-relative paths, computed in
the shared admission accumulator and nowhere else, and taken from the source path
rather than the printed one, so it is comparable across `markdown`, `text`, `json`,
and `xml`. It is an
**order** digest: compare it only between calls with the same `rank` and `focus`, and
use the counts for set equality. A metadata fingerprint is not a promise of
byte-identical content.

`analyze` with `max_tokens` is a budget-fitting tool, not a required step before every
`pack_context`. It reads, inspects, and transforms the selection exactly as a pack
does, so calling it before every pack doubles that work; it pays off when choosing a
budget or checking an admission decision without receiving content. Cost units differ
from `related_files`: this reports the admission cost of the transformed file at its
effective detail, while `related_files` reports `EstimatedTokens` from the source
character count. The two can differ for the same file.

The budget report for `analyze` is appended in its own spotlighted data block after
the spotlighted JSON because it can name skipped files. The fixed
`[Budget accounting]` counts remain trusted text outside both blocks.

When requested compression cannot inspect its delivery source or load a language
grammar, `analyze` adds optional `compressionUnavailable` with a one-line `reason`
and a deterministic `languages` array. The array is empty when the whole delivery
source is unavailable. This is an additive schema field; clients that cache the
schema must refresh it.

Filters are never silent, though an unchanged filter line is reported once per
session rather than on every response — see
[Service notices repeat only when they change](#service-notices-repeat-only-when-they-change).
`get_tree`, `pack_context`, and `related_files` carry a trusted
`[Effective filters] git: ...; exclusions: ...` line naming the Git mode and
exclusion toggles that shaped the tree and who can widen them: the server
startup line, or a per-call `exclusions` value on a delegation server. It sits
among the trailing diagnostics rather than last: an `[Empty selection]` line can
follow it, `[Protection]` comes after that, a pinned remote checkout adds
`[Remote]`, and a budgeted `pack_context` ends with `[Budget accounting]`. When
`max_file_bytes` is supplied, every tool that accepts it also reports
`; max_file_bytes: <bytes>` in its effective-filter diagnostics. Every selection
tool adds an `[Empty selection]` line when no file survived the
filters and the request arguments. That line opens with the stage that emptied the
selection as a constant token — `stage=patterns`, `stage=paths`, `stage=git-scope`,
or `stage=filters` — so a caller can tell a pattern that matched nothing from a
server that hides the file, without a second call. A pattern with no `/` and no
`**` matches only an entry directly in the project root, and its empty result
names the `**/` and `/**` rewrites instead of restating the general rule. `search_project` adds a `[No matches]` line
with the searched-file count when the pattern matched nothing, and a
`DPX-MCP-PATH-NOT-FOUND` error for a filtered file names the effective filters
and the party able to widen them — the startup line, or a per-call `exclusions`
value on a delegation server. These diagnostics never reveal hidden paths.
When selection produces warnings, `analyze` appends separate human-readable
trusted warning text blocks without changing its structured schema. Warning
messages contain stable codes and safe counts or retry guidance, never diagnostic
paths or project-controlled message text. A diff scope is summarized there only as
`git: diff`; its exact user-supplied references are placed in an adjacent
spotlighted data block.

### Service notices repeat only when they change

The `[Effective filters]` and `[Protection]` lines describe server state, not the
call, so a session receives them once and then only when what they say changes.
The change signal is the state the lines are made of: the project, the profile,
the effective exclusion set, the Git mode, and the protection policy. A response
that withholds them carries a constant pointer instead, and the pointer names
exactly the lines that response withheld:
`[Unchanged] filters, protection; see list_projects.` when it would have carried
both, `[Unchanged] filters; see list_projects.` or
`[Unchanged] protection; see list_projects.` when it would have carried one. Each
is never longer than the shortest set it can replace, so no response grows by
omitting a notice.

A tool that never reports one of the lines is not withholding it, so the pointer
never names it. `analyze` reports no protection line on any call, and no response
in a session of `analyze` calls says a protection line is unchanged.
`search_project` and `get_file` report no effective-filters line while the
selection is not empty, so what they withhold is the protection line alone and
that is all their pointer names. A session is never told that a line it was never
sent has not changed.

Omission has to be provable. When the project cannot be identified, when either
line would say something this session has not been told for that project, or when
the response has to explain itself anyway, the full set goes out again. Concretely
the full set always returns for: the first response of a session, the first
response after any of those inputs changed, an `[Empty selection]` response, and
any call that passed `max_file_bytes`, whose echo reports a per-call argument
rather than session state. A new server process is a new session and always starts
in full.

A line counts as reported only once it is in the text the caller receives. A
result whose body moved into a stored pack, a response whose trailing diagnostics
were truncated to fit a stored-pack limit, and a failed call all leave the session
where it was, so the next response reports the full set again rather than pointing
back at something the caller never saw.

`list_projects` is unaffected and always answers with the complete `baseline`
object, including the Git mode, exclusion tokens, `agentExclusions`, and
`protection`. It is the orientation call and the way an agent that lost its
history recovers the whole picture; it never counts as having reported a
per-call effective selection, so it does not suppress a later `[Effective
filters]` line.

Only the repetition of unchanged trusted lines changes. Lines that state a fact
about one call — `[Remote] commit=`, `[Resolution]`, `[Facts coverage]`,
`[Search scope]`, `[Budget accounting]`, `[Search observed]`, `[Search boundary]`, `[Name search]`,
search and tree truncation notices, and every `[Warning ...]` — are computed and sent for every
call as before, and the untrusted-data wrapper around project text is never
affected.

If mandatory secret redaction cannot inspect a selected file, including text
larger than the 16 MiB inspection boundary, selection-wide content tools return
a partial success plus a trusted `DPX-MCP-PAYLOAD-TRUNCATED` notice. `search_project`
searches the inspected files, `analyze` preserves its structured metrics
envelope while identifying metrics that may be estimated, and `pack_context`
withholds uninspected content. The notice is outside spotlight delimiters and
reports only a count, never file paths or uninspected content.
For the single-file `get_file` operation the same state is an error, because a
successful empty payload must mean that the inspected source file is genuinely
empty. Line ranges are evaluated only after the complete source passed mandatory
inspection. Search consumes each transformed, redacted file directly from the
bounded pipeline. The redaction preparer supplies the exact ranges it inserted in
the final text; only those ranges are excluded from pattern matches. Placeholder-like
source text, including an unfinished `DEVPROJEX_REDACTED[` prefix, remains searchable.
The search is streamed: no intermediate export is written or read. Context windows that overlap or touch
are emitted once as a merged grep-style group, with `--` between separate groups.
If the search cap below cuts a group, only matching lines whose complete
prefix, text, and line ending were written count as shown; the remaining count is
exact, and the `[Search truncated]` line names the cap that stopped it.

The match text a `search_project` call returns is capped at 16,000 characters, well
below the general 50,000-character response limit, because a wide alternation with
context lines could otherwise spend a large share of an agent's context in one
unpredictable call. When the cap stops the output, the response adds the constant
`[Search truncated] The returned text reached the 16000-character search cap. Narrow
the pattern, add paths or include_patterns, or lower context_lines.` Whenever a call
does not return every match it encountered, it also reports
`[Search observed] matches=N · matching-files=M within inspected sources` and
`[N additional observed matches not shown]`. These counts are exact for sources that were actually inspected,
not a claim about an uninspected suffix. A group cut only in its trailing context
lines withheld no match, so it receives the cap notice without an additional-match
line. Trusted counts and constants remain outside the untrusted block; no path enters
them. The selected uniquely addressable declaration body and its selector share this
same 16,000-character cap with the match text. Matches are chosen once at the full
cap, and body placement never removes a shown match or a distinct matching file.

Every search ends with a trusted boundary line. A complete search says:

```text
[Search boundary] complete · sources inspected=X/Y · matches retained=R/T · matches written=W · declaration files named=N.
```

A partial search uses the same counters, names the exact bound or bounds that applied,
and tells the caller to page a stored retained result when available or to narrow and
rerun for omitted evidence. Consequently, a complete zero-match response is evidence
that the whole effective selection was searched, while a partial zero-match response
is only evidence about its inspected sources.

Unavailable compression is reduced optimization, not unsafe output. The affected
file remains complete, and `analyze`, `pack_context`, and `get_file` append
`[Compression unavailable] failures=N · languages=M` when their effective selection requests
compression. This trusted trailer is outside every project spotlight block and
contains counts only; structured analysis data remains inside its untrusted representation.
Unsupported languages and files rejected by parse or structural safety checks are
separate unchanged-file outcomes and do not produce this trailer.

All eight tools omit `outputSchema` and `structuredContent` and return the useful
payload in text blocks under `content`. `list_projects` and `analyze` place their
JSON object inside the standard spotlighted untrusted-data block; the other tools
return their existing text or document shape there. This
avoids JSON escaping and unnecessary token overhead for trees, source text,
search context, dependency facts, and packs. Truncation and continuation metadata is appended as
trusted plain-text trailers outside every project spotlight block, such as
`[Tree truncated ...]` and `[Showing lines ...]`.
For `get_file` and `read_pack`, a requested `end_line` past EOF returns every
available line and appends
`[Showing lines A-N of N; end_line B exceeded the file.]`. A `start_line` past
EOF remains `DPX-MCP-INVALID-RANGE` with the valid `1-N` interval. A syntactically
reversed range is rejected before any project or pack access and states that lines
start at 1 and `start_line` must not exceed `end_line`. If the 1,000-line or 50,000-character page limit is reached
before EOF, the existing
`[Showing lines A-B of N; continue with start_line=B+1.]` trailer takes priority.
When the character cap falls inside one long line, the trailer keeps that line and
adds the next 1-based Unicode column:
`[Showing lines A-A of N; continue with start_line=A start_column=C.]`.

`max_depth` counts levels below the project root. Depth 0 returns the root alone,
depth 1 adds its direct children, and a file inside `src/router` first appears at
depth 3. `paths` narrows the selection but never re-roots the tree, so the depth a
subtree needs is still counted from the project root, not from the `paths` entry.
The three narrowing parameters compose in a fixed order and never widen each other:
`paths` and the pattern arrays intersect to form the selection, and `max_depth`
then prunes the rendering of that selection. Listing one directory therefore needs
no depth at all — `paths: ["src/router"]` alone returns everything selected under
it, and `include_patterns: ["**/*router*.ts"]` is how a file is found by name.
Several known directories belong in one call: for example,
`include_patterns: ["src/middleware/{powered-by,body-limit,bearer-auth}/**"]`
selects all three subtrees without walking them separately.

For `get_tree`, omitted `max_depth` on an oversized `text` or `markdown` result
selects the deepest depth whose complete tree fits the 2,000-line limit and
appends `[Tree limited to depth D of N to fit 2000 lines; pass max_depth or
include_patterns for a subtree.]`. Node and format-header line counts are
computed from the selected tree before rendering, so the response never stops
mid-tree for the line limit. An independent 50,000-character cap bounds unusually
long names; human-readable output then carries the same truncation notice. If depth 1 itself cannot fit,
the original bounded response and `[Tree truncated at 2000 lines or 50000 characters ...]`
trailer remain. An explicit
`max_depth` is the caller's choice and retains that same truncation behavior.
JSON and XML never return partial syntax: overflow remains
`DPX-MCP-PAYLOAD-TRUNCATED`; line overflow names the largest `max_depth` that
would produce a complete document, while character overflow asks the caller to
narrow `paths` or patterns.
The `text` tree writes its project address once, followed directly by the real
top-level children; it does not repeat the project name as a synthetic tree node.
Markdown tree Root values and node names escape active CommonMark, HTML, and
entity syntax so project-controlled labels remain literal data.
Markdown context project headings and content-only Root lines use the same
literal escaping. `get_file.path` and `paths` in `get_tree`, `analyze`, `pack_context`, and `search_project`
accept these escaped spellings when the literal path does not exist; callers can
also request `get_tree` with `format: "text"` to copy unescaped names.
Selection warnings are appended outside project spotlight blocks so clients can
distinguish trusted diagnostics from untrusted file data. MCP pack documents omit
their embedded warning diagnostics to avoid duplicating untrusted path-bearing
messages; the safe trusted warning trailer remains authoritative.

For tools that expose a format choice, `markdown` and `text` representations are
intended for people and agents to read. JSON and XML are machine-readable forms
with escaping guarantees; use `json` or `xml` when reliable parsing is required.

The documented per-tool character limits apply to useful payload text.
Untrusted-data markers and trusted warning trailers can add a small fixed overhead
beyond those limits. The exception is the stored `pack_context` response: its
50,000-character limit covers the complete response, including the wrapper, tree
preview, and trusted diagnostics.

An inline `pack_context` result contains the complete pack. A stored result is
self-contained and starts with this line:
`Pack stored as '<id>' (<N> characters, <M> lines). Call read_pack ...`
The project tree preview follows it. Clients extract the session-scoped `pack_id`
from that text and pass it to `read_pack`. The stored
response, including its tree preview and trusted diagnostics, is bounded to
50,000 characters; preview truncation never changes the stored pack, which
remains available in full through `read_pack`.

For human-readable `pack_context` documents with `view: "content"`, text and
Markdown write one `Root: ...` line and use project-relative file headings. A
remote checkout uses its safe repository URL in that Root line and never exposes
the managed cache path. `tree-content` keeps its existing relative content
headings. JSON and XML retain their machine-address contract for `root` and
`files[].path`.

When `max_tokens` is supplied, both inline and stored results include a budget
report in a separate spotlighted data block after the pack or tree preview. This
keeps an inline JSON or XML pack independently parseable while applying the same
untrusted-data boundary to skipped file names. The report states the budget,
included and skipped file counts, estimated tokens for both groups, up to the 25
largest skipped files, and `and X more` when the list is longer. It recommends
`detail=compact` or `detail=signatures` when additional files are needed.
The trusted trailer also includes `[Budget accounting] content ≈ N of M tokens ·
budget report ≈ K · reply ≈ E`. Stored responses additionally report
`stored document ≈ D`. `reply` covers every text block in the assembled response;
all four figures use the same characters-divided-by-four heuristic, not a tokenizer.
This separates greedily admitted content from the diagnostic report, which is
deliberately outside the content budget.

`pack_context` accepts the single optional ranking value `rank: "importance"`.
It applies experimental `importance-v1` only after the effective selection has
been resolved, so it cannot widen `paths`, patterns, profiles, exclusions, Git
scope, or submodule boundaries. With `max_tokens`, importance controls the
existing greedy admission pass; without a budget, all candidates are serialized
in descending importance. A tree-only pack rejects `rank`, and `get_tree` does
not expose it. Unknown values return `DPX-MCP-INVALID-ARGUMENTS`.

With `rank: "importance"`, `focus` accepts one non-empty path string or an array
of one to 16 strings. The input count is enforced before canonical-file
deduplication. Each value is resolved with the same root-jail, Markdown-unescaping,
case, directory, and effective-selection rules as `get_file` and `paths`.
`focus` without rank, null, an empty string/array, a non-string item, or more than
16 values returns `DPX-MCP-INVALID-ARGUMENTS`; a missing, outside-root, directory,
case-mismatched, or filtered file returns the established path diagnostic.
For JSON packs, `ranking.focus.seeds[].requested` keeps the submitted spelling
only after applying the same safe output-path policy and local-user occurrence
decision as the document root.

Valid seeds keep caller order at hop 0. A multi-source BFS over the same unique,
resolved, non-self file graph used by importance ranking orders reachable files
by minimum undirected hop, then `importance-v1` priority and canonical path.
Unreachable files follow in importance order. Excluded intermediate files cannot
bridge two visible files, and focus never expands the manifest. A seed is first
for greedy admission, not guaranteed inclusion. Seed state, the bounded hop
histogram, parent relation, original importance priority, and degraded fact state
are reported as `focus-v1`; path-bearing explanation lines remain inside the
standard untrusted-data block. Personalized PageRank is evaluation-only and is
not exposed. GUI and TUI selection are human-controlled and do not apply focus.

The registered evaluation showed the same RecallNew and AllRequired outcomes for
focus and an explicitly assembled `directed-from-seed` context in all nine
repository/budget aggregates; directed context used a smaller irrelevant-token
share in each. Focus is a one-call convenience for prioritizing a known file's
neighborhood while keeping a broad effective selection and fallback, not a claim
of superiority over directed `related_files` plus `paths` workflows. Reported
performance numbers measure ranking only, not the complete `pack_context` call.

The ranking uses quantized resolved-dependency PageRank, a safe offline
200-commit Git history window, and a small file-role signal. Missing evidence
limits confidence instead of being renormalized into an advantage. A shallow
window reports the number of commits actually read. Progress has three bounded
stages: fact indexing, history reading, and priority computation.

The bounded trailer follows the untrusted project block. Trusted status lines
report facts coverage, resolved internal-link coverage, files touching an edge,
history completeness, and the missing-signal policy. The up-to-ten top entries
and ranked skips contain project-derived paths, so they are placed in a separate
standard spotlighted `untrusted-data` block rather than being trusted as control
text. Omitting `rank` retains the ordinary order and performs neither dependency
indexing, Git history work, nor ranking content hashes. See
[Ranking.md](Ranking.md) for the algorithm, fixed weights, evaluation protocol,
and measured limitations.

Future tools must not add `structuredContent` when doing so can cause a client to
discard the protected text representation. Metadata about a text payload is not
sufficient reason to add an output schema.

## Progress Notifications

`pack_context`, `analyze`, and first-use `related_files` indexing report measured
selection, extraction, resolution, content-transformation, and output phases through MCP
`notifications/progress`. Notifications are sent
only when the caller supplies a `progressToken` in the request `_meta`; without
that token the server sends none. `progressToken` is transport metadata, not a
tool argument, so tool input schemas are unchanged. Progress is monotonic and
only the first and final milestones are forced; intermediate file-count updates
are rate-limited. Failure and cancellation still terminate the request instead of
leaving a progress operation open. The other five tools do not
report progress.

Defaults:

- `get_tree.format`: `markdown`
- `pack_context.view`: `tree-content`
- `pack_context.format`: `markdown`
- `pack_context.detail`: `full`
- `pack_context.max_tokens`: unlimited
- `pack_context.focus`: absent
- `analyze.detail`: `full`
- `analyze.top_files`: `10` (`1..1000`)
- `analyze.max_tokens`: absent (no admission preview)
- `analyze.rank`: absent
- `analyze.focus`: absent
- `detail_by_pattern`: absent (one detail level for the whole selection)
- `tracked_only`: `false`
- `git_scope`: absent
- `exclusions`: absent — the server baseline or profile set applies (parameter
  exists only on `--allow-agent-exclusions` servers)
- `search_project.context_lines`: `2`
- `search_project.ignore_case`: `true`
- `search_project.max_results`: `50`
- `related_files.direction`: `both`
- `related_files.path`: one string or an array of at most 16 strings

`include_patterns` and `exclude_patterns` are arrays of project-relative globs
using `/`. Each array accepts at most 256 non-empty patterns, with at most 512
characters per pattern. A pattern matches the whole project-relative path:
`*` and `?` stay inside one path segment, `**/` spans any depth, and `{a,b}`
lists alternatives (at most 64 per pattern, 1,024 per array after expansion).
`*.cs` therefore matches only root-level files; `**/*.cs` is every C# file,
`src/**` a subtree, `**/*.{ts,tsx}` two extensions. Matching is case-sensitive
on every platform — copy names from `get_tree`. Negation (`!`) and character
classes (`[...]`) are rejected with `DPX-MCP-INVALID-PATTERN` rather than
matched literally, because a silently empty result reads as "no such files".
`paths` contains existing project-relative files or directories for `get_tree`,
`analyze`, `pack_context`, and `search_project`. Its entries are literal paths;
glob metacharacters have meaning only in the pattern parameters. A `paths` entry
carrying no separator therefore names one entry directly in the project root, and
matches nothing when a file of that name lives deeper. Every array
parameter requires a JSON array: a bare string where an array is expected returns
`DPX-MCP-INVALID-ARGUMENTS` naming the argument, for example
`'paths' must be an array of strings.`, instead of a partial or empty result.

### Finding a file by name

`include_patterns` is the only parameter that matches a file by its name. Content
search never matches a path, and `paths` selects a path that already exists at the
depth it names, so both answer a name lookup with nothing useful. Two responses
therefore carry the constant `[Name search]` line, which names the form that
works: `search_project` when it searched at least one file, found no match, and
the pattern carries a `/` or ends in something shaped like an extension; and
`get_tree` when a `paths` entry with no separator was not present in the effective
tree. The line is a constant — the pattern and the paths a caller sent never reach
it — and neither `analyze` nor `pack_context` needs it, because a missing `paths`
entry there is a `DPX-MCP-PATH-NOT-FOUND` error rather than a partial answer. A
search that found matches, a search whose pattern reads as ordinary content, and a
selection that was already empty are all unchanged.

### Search hits name the declaration that contains them

`search_project` names the declaration each shown hit sits inside. There is no
parameter for it: a hit without the thing that contains it is what made callers
guess a line range and read twice. The name heads its hits inside the file's own
block, the way the path does, and is written again only when it changes:

```text
src/App.cs
in P.App
5:    public int Run() => 1;
--
12:   public int Stop() => 2;
src/Router.cs
in P.Router
9:    public int Route() => 3;
```

Both hits in `src/App.cs` sit in the same declaration, so it is written once. The
granularity is the index's, which for C# is the enclosing type rather than the
enclosing member, so two methods of one type share a header.

Location is therefore spelled once in a response: the path heads its block, each
line carries its number, and no row repeats the two together. A run of hits inside
one declaration is headed once, so a response that would have listed fifty rows
carries a handful of headers. A header labels the group it opens, so it sits above
that group's leading context rather than between the context and the hit, and a
group whose later matches cross into another declaration is headed again at the
match that crosses.

A hit can belong to no declaration at all — a top-level statement, an import, a
comment past the end of a type. A run of those is opened with the constant
`in (no declaration)`, so the header above them stops claiming them. It is written
only to close a run that a name had opened; a file whose hits never sit in a
declaration carries no header at all.

A declaration name is text this project wrote, so it stays inside the untrusted
block with the match lines it describes. Only counts leave it, as one trusted
line: `[Symbols] annotated=N · files-without-declarations=K.`, which
distinguishes a hit that sits in no declaration from a file that was never
parsed, plus a `files-past-the-64-file naming limit=` term when a search touched
more files than the naming bound allows.

The names come from bounded navigation extraction over files that actually produced
hits: one parse per matching file as it passes, never one per hit, and never over
the whole selection. Only navigation for the best 64 candidate files remains resident,
and that set can evict an earlier weaker file. Granularity is whatever the index declares, which for C# is
the enclosing type rather than the enclosing member. Hit lines are lines of the
transformed text the tool returns and the index parses the file on disk; redaction
replaces a secret with a placeholder on the same line and adds no lines, so the
two agree on the only coordinate this uses.

Headers are placed into a finished render rather than written during it, which is
what makes two rules hold without unwinding anything. A render the character cap cut
is left exactly as it was, so a capped response carries no naming at all and spends
every character it has on matches. And a header is only ever placed in front of lines
that are already present, so no header can be left as the last line of a response
with no hit under it. Placement is all or nothing: a header skipped for want of room
would leave the hits beneath it reading as part of the declaration named above them,
so when the headers do not fit, none are written and the coverage line reports that
nothing was named, rather than going silent about naming it did compute. The match
lines, the `--` group separators, and all boundary counters describe the same finished
render.

### A withheld search stays in the session

A search that has to withhold retained matches keeps the rest of those matches, so
the way forward is to page that result rather than to repeat the same scan. A search
that returns every retained match stores nothing. Its boundary still says explicitly
whether all eligible sources and all encountered matches were covered.

When matches are withheld the response carries three things beyond what it carried
before. Inside the untrusted block, after the matches, the distribution of what was
withheld, because a path is project text:

```text
Withheld matches by file:
src/router/match.ts 31
src/router/parse.ts 12
and 4 more file(s)
```

Counts, not line numbers. One recorded search withheld 861 matches; their numbers
would be noise, while the per-file counts tell a caller where to look next for a few
characters per file. At most 20 files are listed and the rest are summarised as a
count, and the count leads each line so that a path containing spaces, or ending in
digits, stays unambiguous to read.

**A cut listing keeps its breadth.** Whichever bound cuts it, `max_results` or the
character cap, every matched file gets a hit before any file gets a second. Three
recorded whole-file reads, 46 KB and the largest single class of them, happened
because an alphabetical cut never reached the file the caller was after: it held only
a name from an import line and opened the file. The choice is made one file at a time
over the whole result, and the character cost counted is what the renderer will
actually write, so the bound is honoured without a cut falling on whichever file
happened to be last.

Outside the block, in trusted text, one line of counts and a server-minted id:

```text
[Search stored] pack_id=<id> · matches=N · files=M; read_pack pages those files whole, without searching again.
```

`read_pack` pages that id exactly as it pages a `pack_context` result: it does not
rebuild a plan, does not take the project-operation gate, and does not search again.
The stored result holds the complete result of every file that withheld anything, so
`matches` counts what the store holds rather than what was withheld, and a page reads
continuously instead of as the fragments a per-group store would leave. The
distribution beside it counts what each file withheld, which is the smaller number.

Both the distribution and the selector list share the response's character budget
with the matches, and neither is written unless at least one of its rows fits: a
heading with nothing under it would report a search that found something and then
show none of it. A trusted line that points at one of those lists is written only
when the list was.

### What a bounded result retains

The search chooses which compact match records to retain while transformed files
stream past. A stronger late record can evict a weaker early record; the server does
not keep an unbounded list and sort it afterward. The constant naming this rule is:

```text
[Search order] bounded evidence priority; canonical path and line break ties.
```

Priority is the sum of soft signals: exact agreement between the pattern and an
enclosing declaration name, an explicitly requested `paths` scope, breadth across
files and declaration owners, and a bounded penalty for repeated line content.
These weights are 160 for a qualified declaration, 128 for a simple declaration,
64 for declaration syntax, 1,024 for explicit scope, 512 divided by the occurrence
within one file, 32 divided by the occurrence within one owner, and at most 31 points
of repetition penalty. No test, generated, snapshot, or unsupported-language category
is excluded or categorically demoted. Project-wide importance ranking is not used.
Equal priorities use canonical relative path and line number, both ordinal, so
filesystem traversal order cannot decide the answer.

The collector retains at most 5,000 matches and 2,000,000 characters of bounded
fragments. It never retains whole transformed files after their pipeline callback.
Declaration extraction is attempted for matched files as they pass, while navigation
data remains resident only for the same best 64 files selected by candidate priority.
Thus a late useful file can displace an early repetitive file in both match storage
and the declaration-naming budget. A stored search result is subject to the same session quota and
least-recently-read eviction as any other stored result, and expiry or eviction of
one is reported in search terms: it names the search result and tells the caller to
call `search_project` again, not `pack_context`.

`max_results` still bounds the matches a response displays. Encountered, retained,
written, and declaration-named counts are deliberately separate; none is presented
as the count for the whole project unless the boundary says `complete`.

### The search result carries the selector

After the matches, inside the same untrusted block, a search that found a hit in a
declaration lists each declaration once, in the shape a caller passes straight back:

```text
Declarations found (path, symbol, lines):
src/Core/LevelOverrideMap.cs Core.LevelOverrideMap 17-58
```

One line per declaration, not per hit: a declaration ten matches landed in is still
one thing to open. The name and inclusive range come directly from the innermost named
declaration the navigation projection reports. The name is accepted unchanged by
`get_file.symbol`. C#, JavaScript, TypeScript, Go, Python, Java, Rust, Kotlin, Ruby, PHP,
C, and C++ include supported members and functions, with their owner chain when names
repeat within a file. When more than one root is configured, the printed `get_file`
arguments also include `project`; a remote selector includes its source project and
branch. The complete printed object can therefore be passed back without editing.
At most 20 are listed.

The declaration body is selected without changing match order or the declaration list.
Literal fragments of at least three characters are taken conservatively from the search
pattern. Exact ordinal equality with the full printed name or its last segment after
a dot, colon, or navigation owner separator wins first. Otherwise a declaration name containing any fragment exactly wins; otherwise a
name containing a fragment after case-folding and removing separators wins. Within the
same name quality, the declaration containing the most distinct matched lines wins.
The existing deterministic declaration order breaks the remaining ties. If the pattern
has no qualifying literal fragment, the existing order chooses the body without using
name or hit-count preference.

When the selected declaration has exactly one declaration with that printed name in its
file, the same untrusted block also carries its protected body:

```text
Best declaration body (1 of 3):
get_file {"path":"src/Core/LevelOverrideMap.cs","symbol":"Core.LevelOverrideMap.GetLevel"}
lines 31-38
public LogEventLevel GetLevel(string source)
{
    ...
}
```

The body comes from the same transformed snapshot that produced the match, so mandatory
secret masking and configured private-data replacement have already run. Only this one
body is included when enabled. By default, it is limited to 1,800 characters within the unchanged 16,000-character
search response budget. Its declaration is selected once from the full displayed
match slice and is not reconsidered during placement. Complete overlapping context
lines from that same declaration are printed in the body rather than twice; matching
lines, context outside the printed body, and other files remain in their original order.
Only spare space is used beyond that reclaimed context. When space is insufficient,
the body is cut at a complete line or omitted, never at the expense of a shown match.
A cut body reports exactly how many
declaration lines remain and prints the complete `get_file` arguments needed to read it.
The trusted `[Declaration body] shown=1/N` notice states how many other declarations need
separate reads. If the selected printed name identifies more than one declaration in its
file, no body is guessed; the response says to use the listed inclusive range instead.

#### Evidence-preserving placement checks

The same real-server queries were compared on the Release build at
`1faac5c637a31e500bc09ef6ce9a63cff605051a` and the placement implementation at
`62432f5ed20147ae2a3acc25a4947af0c4441aa7`. Each used `context_lines: 3`,
`max_results: 200`, default case-insensitive matching, and the same selection.
File counts include every file with a numbered matching line, including documentation,
not just files with supported declarations. Matching lines are counted in the match
section, not counted twice when a body repeats one.

| Pinned repository | Pattern | Distinct files before → after | Shown matches before → after | Body before → after |
|---|---|---:|---:|---|
| Serilog `49b5339ce85385dc52d4d8e8f2b8308becf23506` | `Emit` | 51 → 51 | 62 → 66 | `Serilog.Core.Sinks.AggregateSink.Emit` → omitted |
| client_golang `3f5d6801b95618d04d5057edb1724b02aae69082` | `Observe` | 34 → 34 | 60 → 62 | `Summary.Observe` → omitted |
| Zod `12e6272746eb79c9713f4818e85a15231bd368b6` | `parse` | 57 → 60 | 58 → 60 | `parseArgs` → omitted |

These dense responses use their space for evidence rather than an extra body. No
matching file was lost. In Zod, one previously shown matching line moved to the stored
pack as the full-cap breadth allocation admitted more files; its exact numbered text
was confirmed through `read_pack`. Retention limits and stored whole-file continuation
are unchanged.

Deterministic process fixtures also cover the last fitting exact-name hit: the previous
placement returned 89 of 101 matching lines and gave the body to `Sample.Configure`;
the fixed placement returns all 101, keeps `Sample.Needle` selected, and omits its body
when it cannot fit. A separate 26-file fixture returns 26 files instead of 24, while a
continuation fixture verifies that every retained matching address is present inline
or in `read_pack`. Run these checks with:

```powershell
dotnet test Tests/DevProjex.Tests.Terminal/DevProjex.Tests.Terminal.csproj -c Release -m:1 --filter "FullyQualifiedName~RealProcessPreservesTheExactNameMatchAtTheEndOfTheSearchSlice|FullyQualifiedName~RealProcessKeepsEveryMatchingFileWhenTheBodyWouldNeedItsSpace|FullyQualifiedName~RealProcessPrintsOverlappingDeclarationContextOnlyOnce|FullyQualifiedName~RealProcessBodyPlacementKeepsEveryRetainedMatchInlineOrInTheStoredPack"
```

One trusted constant closes it:

```text
[Read declarations] To read any declaration listed above in full, call get_file with its path and symbol; for several of them, one get_file requests call.
```

The list ships on every search that showed a hit, including a search the character
cap cut, because a cut response is exactly when a caller would otherwise open a whole
file to find a declaration it was already holding. The `in <Name>` headers are a
different thing and keep their rule: they label runs of hits while reading, and a
capped response drops them so the remaining characters go to matches.

### Reading a declaration by name

`get_file` accepts `symbol` beside `path` in place of a line range, and returns the
lines that declare it. It takes a qualified name, or a simple name that is unique
in that file; the last segment is compared when the qualified form does not match.
It is the other half of the naming `search_project` does: a hit tells you the
declaration, and `symbol` reads it without a line arithmetic step in between.

`symbol` cannot be combined with `start_line`, `end_line`, or `start_column`, and
that combination is rejected before the file is read. Three cases return
`DPX-MCP-INVALID-ARGUMENTS` rather than a guess: a name matching more than one
declaration, which reports how many and asks for the qualified form; a name
matching none; and a file no declarations were extracted from, which is how an
unsupported language answers. None of these echoes a declaration name, because the
error text sits outside the untrusted block and a declaration name is project text.

The range is the declaration the navigation projection reports, so its granularity is
the same as the naming on search hits. Every successful scalar read starts with
`File: <path>` and `Lines: <start>-<end> of <total>` inside the untrusted block.
The navigation query runs on the same transformed snapshot as the returned text. Multi-line
replacement, compression, and a source edit between calls therefore cannot mix disk coordinates
from one version with response lines from another.

### Batch `get_file`

Use `requests` when several source excerpts are already known; use `search_project`
when their locations are not known. Exactly one of `path` or `requests` is required;
supplying both or neither returns `DPX-MCP-INVALID-ARGUMENTS` before any file is read.
The batch contains one to eight records. A path-only record reads the whole file;
`{"path":"src/App.cs","ranges":[{"start_line":10,"end_line":30}]}` and
`{"path":"src/App.cs","symbol":"Example.App.Run"}` narrow it. A call contains at most sixteen
whole-file, range, or symbol selections in total. Each range is inclusive, starts at line
one, and requires both `start_line` and `end_line`. Unknown properties, empty ranges,
combined `ranges` and `symbol`, and invalid indexed records fail before project access with
`DPX-MCP-INVALID-ARGUMENTS`.

The server resolves every requested path through the effective selection, then
reads, transforms, and redacts each distinct physical file exactly once. Overlapping
or touching ranges for one file become one content section whose header lists the
served request/range indices. Every requested range receives one explicit status:
`ok` when complete, `partial` when cut by the shared response limit,
`not-returned` when no content fitted, or `unavailable` when mandatory bounded
inspection withheld the file. In live mode a path that disappeared since discovery
is an unavailable item rather than a failure for the whole batch. An unknown,
ambiguous, or unsupported `symbol` is likewise reported only on its item, while
syntactically invalid request records still reject the call before any read. An
unavailable status contains only a count-safe reason.
The complete batch, including section headers, is limited to 1,000 lines and 50,000
characters. A partial section reports the next 1-based `start_line` and
`start_column`; call `get_file` again for that continuation.

### Search, then one batched read

This is the normal reading pattern, not an advanced one. A search returns several
interesting locations; the follow-up is a single call, not one call per location.

```json
{"name": "search_project", "arguments": {"pattern": "createRouter", "context_lines": 2}}
```

```json
{
  "name": "get_file",
  "arguments": {
    "requests": [
      {"path": "src/hono-base.ts", "ranges": [{"start_line": 415, "end_line": 430}]},
      {"path": "src/hono.test.ts", "ranges": [{"start_line": 811, "end_line": 855}]},
      {"path": "src/utils/url.ts", "ranges": [{"start_line": 1, "end_line": 95}]},
      {"path": "src/utils/url.test.ts", "ranges": [{"start_line": 147, "end_line": 175}]}
    ]
  }
}
```

Four single reads of four locations are one call with four records. The batch
reads and redacts each physical file once, reports a status per range, and pays
one set of response notices instead of four. Use the single `path` form only when
exactly one range of one file is wanted.

Numeric parameters accept JSON numbers and decimal numeric strings.
Boolean parameters accept JSON booleans and the exact strings `"true"` and
`"false"`.
Numeric type, sign, published bounds, and line-range ordering are validated before
the server resolves a project, reads a stored pack, plans files, or prepares content.

`max_tokens` is an integer of at least 1 and accepts either a JSON number or a
decimal numeric string. It uses the existing estimate of one token per four
transformed characters, rounded up per file. This estimate is calibrated against
modern tokenizers; on code, it is typically within roughly +/-5% of
cl100k/o200k-class tokenizers. Actual tokenization depends on the selected model,
so clients with a hard context window should set `max_tokens` below the window
limit to leave headroom. Files are considered in the deterministic selection
order: a file is included when its estimate fits the remaining budget; otherwise
it is reported as skipped and later, smaller files are still considered. A budget
that fits no files is a valid empty-content pack. Tree text, file headings,
markup, and other document structure are not charged to this content budget.
Consequently, the report's included-token sum can differ slightly from the
complete document metric, which normalizes line endings and includes document
output differently.

For every budgeted pack, DevProjex verifies admitted file identities after fresh
content preparation. If a selected source changed after token admission, the tool
returns `DPX-MCP-PROJECT-UNAVAILABLE` with advice to retry instead of applying an
old token budget to different bytes. Importance-ranked packing retains its earlier
identity check before transformation as well and reports
`selection changed during packing; retry` when that check fails.

For JSON and XML packs that include content, the existing `metrics` object
describes the complete effective selection after `detail` and mandatory secret
redaction but before the token budget is applied. The `files` collection and
`tokenBudget` object describe the content that the budget admitted; the tree
remains the complete effective selection so clients can see what was omitted.
A tree-only pack does not read or transform file content, so its content metrics
describe the selected source files before `detail` or redaction.

`max_file_bytes` is an optional positive byte count on `get_tree`, `analyze`,
`pack_context`, and `search_project`. It removes files strictly larger than the
limit after profile, ignore, Git, `paths`, and glob narrowing; a file exactly at
the limit remains selected. It is request-only, is not stored in profiles, and
does not apply to `get_file`, which addresses one already-effective file. Its
value is echoed in the effective-filter diagnostics of every tool that accepts
the parameter.

For the six project tools, `project` accepts a unique `name` from `list_projects`
or that entry's absolute `path`. An ambiguous name returns
`DPX-MCP-UNKNOWN-PROJECT` and asks for one of the listed paths; every unknown-project
error names both accepted local forms. `project` may be a Git URL only when the server was
started with `--allow-remote`. The optional `branch` is valid only with a URL.
Remote checkouts are reused from RepoCache and remain pinned for this server
session. `list_projects` continues to report only configured local roots.
Project-tool calls, including clone/acquire, are serialized; a first clone
therefore delays other project-tool calls until its checkout is ready.
Every successful remote project response includes the trusted
`[Remote] commit=<sha>` trailer. The value is a validated lowercase hexadecimal
commit id from the pinned repository-cache session; an invalid or unavailable
value is rendered as `commit=unknown`. A branch name is repository-controlled,
so it appears only as `analyze.remote.branch` inside the untrusted structured
payload and never in the trusted trailer.

When `tracked_only` is `true`, only paths present in the Git index are selected.
The option can strengthen a profile but cannot disable tracked-only filtering
already enabled by that profile. A non-Git project rejects the option with an
actionable error instead of returning an empty result.

`git_scope` accepts only the narrowing values `staged`, `changes`, and
`diff:<ref>..<ref>` on `get_tree`, `analyze`, `pack_context`, and
`search_project`, and `related_files`. It intersects the server/profile baseline and therefore cannot
re-enable paths excluded by `tracked_only` or a tracked profile. Staged selects
paths changed in the index; changes selects staged, unstaged, and non-ignored
untracked paths; diff selects paths changed between two Git references. These
scopes use Git only to select paths: every existing file's content comes from the
current working tree, never from index or reference blobs. The complete value is
limited to 4,096 characters.
Deleted paths are omitted with a `DPX-GIT-STATE-DELETED` warning. A non-Git
project or unavailable/invalid Git state returns an actionable tool error.

Profiles use the existing project profile mechanism. `standard` is the desktop
set of all eight exclusion toggles with `gitignore`, so it is stricter than the
server default. `local` loads the profile saved by the desktop application for
this project; available local profiles appear in `list_projects.profiles`. A
portable profile is a JSON path inside the project root. Profile selection can
enable compression or stripping, but cannot disable secret redaction or alter
the server-level private-data policy.
An explicitly selected profile may broaden file selection relative to the
server's startup exclusion set. `--allow-agent-exclusions` likewise delegates
the documented exclusion toggles. Neither mechanism can cross the configured
root jail, disable mandatory Hide Secrets, or bypass an active remote-host
allowlist; those are the hard authorization and protection ceilings.
After lexical validation, a portable profile is opened through the same guarded
root-jail handle as project content. Replacing it or one of its parent directories
with a symbolic link or junction outside the root causes a request error instead
of reading the external file.

## Detail Levels

`detail` controls additional code reduction for `analyze` and `pack_context`:

- `full` applies no agent-requested code reduction.
- `compact` removes supported comments and blank lines.
- `signatures` also collapses supported method and function bodies.

Unsupported languages remain unchanged. Detail is monotonic: transformations
enabled by the selected user profile are unioned with the requested level, so an
agent can reduce context further but cannot restore bodies, comments, or blank
lines removed by the profile. The `analyze` structured result reports the
effective detail tier; `pack_context` returns the transformed pack itself.
Use `compact` or `signatures` together with `max_tokens` to fit more supported
source files into the same estimated-token budget.

`detail_by_pattern` sets the level per file. It is an ordered array of
`{ "patterns": [glob, ...], "detail": "full" | "compact" | "signatures" }`, at most
16 entries with at most 32 patterns each. The call-level `detail` is the default;
entries apply in order and the **last** matching entry wins, so list general globs
before specific ones. This is last-match, not the first-match rule some other tools
use. Patterns are validated, normalized, and matched exactly like `include_patterns`;
an invalid entry returns `DPX-MCP-INVALID-ARGUMENTS` naming its index. The parameter
is accepted by `analyze` and `pack_context`, and is invalid for a tree-only pack.

The union with profile transformations happens for every file separately, so an
override back to `full` adds no reduction of its own and still never removes one a
saved profile requires. Selection is never widened: a pattern that matches nothing is
reported rather than adding files.

The mix a pack reports describes the files the pack carries: with `max_tokens` it is
counted after admission, not over the selection a budget then narrowed. For `analyze`
it describes the measured selection.

A call that supplies `detail_by_pattern` gains a trusted trailer stating the mix,
`[Detail] full 7 · compact 0 · signatures 3 · overrides 2 of 2 patterns matched`,
followed by `[Detail] unmatched: ...` when some pattern claimed nothing. In JSON and
XML the per-file entry and the skipped-file entries then carry `detail` with that
file's effective level, and the text budget report names the level a skipped file was
costed at. All of that appears only when a call asks for a mix; a call with one
detail level is unchanged, byte for byte.

## Client Configuration

The `devprojex` command must be on `PATH`; otherwise use its absolute executable
path. Replace `/absolute/path/to/project` in the examples.

Desktop's **MCP** menu, Terminal Workspace's `mcp connect` command, and
`devprojex mcp connect` use one connection service and embed the absolute installed
executable path. Desktop connections always include `--live`; the CLI `--mode`
option selects live or standard mode, and Terminal Workspace's `mcp connect` uses
live mode. The Store configuration uses the stable WindowsApps alias;
winget and ZIP paths remain stable while their installation directory is unchanged;
the macOS path remains stable while the `.app` bundle stays in place. AppImage
configurations name the AppImage itself, so moving it requires reconnecting.

The Desktop **MCP** menu contains **Live context ▸**, **Standard ▸**, **Journal…**,
and **Documentation**. The live
submenu contains **Open in Claude Code**, **Open in Codex**, **Open in Cursor**,
**Open in VS Code**, and **Other clients…**; those five actions are enabled only with
an open project.
The first two actions invoke the installed client command, capture its output, replace
an existing `devprojex` entry, and then open a new terminal at the project root.
Claude Code stores the entry in the project-local scope selected by its working
directory; Codex stores it in its global configuration. **Open in Cursor** atomically
merges only the `devprojex` entry in `.cursor/mcp.json`; **Open in VS Code** does the
same in `.vscode/mcp.json`, then each action opens the project through its URL scheme
with its command-line launcher as a fallback.
Unrelated JSON properties and servers are retained. Malformed JSON is never overwritten:
DevProjex reports the error and presents the configuration for manual installation.
**Other clients…** presents the generic `mcpServers` JSON and the standard Claude
Desktop configuration paths on Windows and macOS.

A missing command or failed client process uses the same manual-configuration window.
If removing an existing Claude Code or Codex entry succeeds but adding its replacement
fails, the previous entry has already been removed; the result states this and presents
the manual configuration below. If registration succeeds but opening the client fails,
the window states that the server is connected, names the launch failure, and provides
a manual launch command to copy. A successful open shows no connection toast. The optional
PATH prompt appears only after a successful connection: Windows can install or repair
the command, while macOS and Linux show the shell-profile line to copy. Dismissing that
prompt does not suppress future checks. The title shows the live client name or active-session count.

The CLI performs the connection by default:

```shell
devprojex mcp connect /absolute/path/to/project --client claude-code --mode live
devprojex mcp connect /absolute/path/to/project --client cursor --mode standard
devprojex mcp connect /absolute/path/to/project --client codex --mode live --open
```

Clients are `claude-code`, `codex`, `cursor`, `vscode`, and `json`. The `json` client
always returns the manual configuration. Add `--print` to print the configuration
without discovering a client, starting a process, or writing a project file. Add
`--open` to open the selected client after a successful registration; without it the
CLI only registers the server and prints the result. The manual-only `json` client
rejects `--open`.

### Claude Code

```shell
claude mcp add devprojex -- devprojex mcp --root /absolute/path/to/project
```

### Claude Desktop

Add this server to the `mcpServers` object in the Claude Desktop configuration:

```json
{
  "mcpServers": {
    "devprojex": {
      "command": "devprojex",
      "args": ["mcp", "--root", "/absolute/path/to/project"]
    }
  }
}
```

### OpenAI Codex

Add to `~/.codex/config.toml`:

```toml
[mcp_servers.devprojex]
command = "devprojex"
args = ["mcp", "--root", "/absolute/path/to/project"]
```

### Google Antigravity

Add to `~/.gemini/config/mcp_config.json`:

```json
{
  "mcpServers": {
    "devprojex": {
      "command": "devprojex",
      "args": ["mcp", "--root", "/absolute/path/to/project"]
    }
  }
}
```

### Cursor

Add to the user or workspace `mcp.json`:

```json
{
  "mcpServers": {
    "devprojex": {
      "command": "devprojex",
      "args": ["mcp", "--root", "/absolute/path/to/project"]
    }
  }
}
```

### Visual Studio Code

Add to `.vscode/mcp.json`:

```json
{
  "servers": {
    "devprojex": {
      "type": "stdio",
      "command": "devprojex",
      "args": ["mcp", "--root", "${workspaceFolder}"]
    }
  }
}
```

### Headless package launch (from v5.2)

The v5.2 package channels run the same MCP server without installing the desktop
application. They are published separately; until their first publication, keep
using the installed/direct `devprojex` command above. Node 20+ clients can use
`npx -y`, while machines with .NET SDK 10.0.100+ can use `dnx`.

For Claude Code, choose either command:

```shell
claude mcp add devprojex-npx -- npx -y devprojex mcp --root /absolute/path/to/project
claude mcp add devprojex-dnx -- dnx devprojex mcp --root /absolute/path/to/project
```

For Claude Desktop or Cursor, add one of these entries to `mcpServers`:

```json
{
  "devprojex-npx": {
    "command": "npx",
    "args": ["-y", "devprojex", "mcp", "--root", "/absolute/path/to/project"]
  },
  "devprojex-dnx": {
    "command": "dnx",
    "args": ["devprojex", "mcp", "--root", "/absolute/path/to/project"]
  }
}
```

For OpenAI Codex, add one of these TOML entries:

```toml
[mcp_servers.devprojex_npx]
command = "npx"
args = ["-y", "devprojex", "mcp", "--root", "/absolute/path/to/project"]

[mcp_servers.devprojex_dnx]
command = "dnx"
args = ["devprojex", "mcp", "--root", "/absolute/path/to/project"]
```

For Visual Studio Code, use the same commands in `.vscode/mcp.json`:

```json
{
  "servers": {
    "devprojex-npx": {
      "type": "stdio",
      "command": "npx",
      "args": ["-y", "devprojex", "mcp", "--root", "${workspaceFolder}"]
    },
    "devprojex-dnx": {
      "type": "stdio",
      "command": "dnx",
      "args": ["devprojex", "mcp", "--root", "${workspaceFolder}"]
    }
  }
}
```

### Docker launch

Release automation is configured to publish the headless image at
`ghcr.io/avazbek22/devprojex`. Docker clients should keep stdin open with `-i`,
mount the project read-only, and run the container with a read-only root filesystem.

For Claude Code:

```shell
claude mcp add devprojex-docker -- docker run --rm -i --read-only --tmpfs /tmp -v /absolute/path/to/project:/project:ro ghcr.io/avazbek22/devprojex mcp --root /project --git-mode none
```

For Claude Desktop or Cursor, add this entry to `mcpServers`:

```json
{
  "devprojex-docker": {
    "command": "docker",
    "args": ["run", "--rm", "-i", "--read-only", "--tmpfs", "/tmp", "-v", "/absolute/path/to/project:/project:ro", "ghcr.io/avazbek22/devprojex", "mcp", "--root", "/project", "--git-mode", "none"]
  }
}
```

For Visual Studio Code, place the equivalent entry in `.vscode/mcp.json`:

```json
{
  "servers": {
    "devprojex-docker": {
      "type": "stdio",
      "command": "docker",
      "args": ["run", "--rm", "-i", "--read-only", "--tmpfs", "/tmp", "-v", "${workspaceFolder}:/project:ro", "ghcr.io/avazbek22/devprojex", "mcp", "--root", "/project", "--git-mode", "none"]
    }
  }
}
```

For OpenAI Codex, add:

```toml
[mcp_servers.devprojex_docker]
command = "docker"
args = ["run", "--rm", "-i", "--read-only", "--tmpfs", "/tmp", "-v", "/absolute/path/to/project:/project:ro", "ghcr.io/avazbek22/devprojex", "mcp", "--root", "/project", "--git-mode", "none"]
```

MCP traffic uses stdout exclusively. If startup fails, diagnostics are written
to stderr. Closing the client's stdin terminates the server process.
