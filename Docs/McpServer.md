# DevProjex MCP Server

DevProjex includes a local Model Context Protocol server for read-only project
inspection and context packaging. It uses standard input/output only; no HTTP or
other network transport is exposed. Remote Git sources are disabled unless the
server starts with `--allow-remote`.

```shell
devprojex mcp --root /absolute/path/to/project
```

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

The recommended tool sequence is:

```text
list_projects -> get_tree/analyze -> search_project/related_files/get_file -> pack_context -> read_pack
```

The MCP `initialize` result publishes the same workflow in `instructions`, along
with the redaction placeholder grammar and allowlisted example classes, the
trusted/untrusted boundary, response limits, and the segment-aware glob rules.
Tool descriptions remain self-contained because some clients do not display
server instructions; each description states the tool's purpose, when to use a
named alternative, its result, and its key limit.

Use `list_projects` first to obtain the unique project name or absolute path that
the other tools accept. Use `get_tree` for structure without content, `analyze`
for transformed size and token estimates before packing, `search_project` for
textual locations, `related_files` for statically evidenced relationships,
`get_file` for one file page, and `pack_context` for multi-file context. A large
pack returns an id that `read_pack` pages; `read_pack` does not recreate expired
packs. When a step wants more than one file or more than one range, send one
batched `get_file` call instead of several single reads; see
[Search, then one batched read](#search-then-one-batched-read).

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
- A root listed at startup stays addressable in any spelling that lexically normalises
  to the recorded one — a trailing separator, or the extended-length prefix — because
  deciding that compares strings and opens nothing. It resolves no symbolic link and no
  difference of case. What the refusal cannot cover is a
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
  only for the whole server process with `--hide-private-data`, mirroring the
  CLI flag. Tool schemas intentionally expose no redaction controls.
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
  Private Data processing is added only when the server starts with
  `--hide-private-data`. Root paths in `list_projects` and project-derived
  details in tool errors remain data inside the untrusted boundary; remote tools
  use the safe Git URL as the project address.
  These addresses form the contract for the `project` argument. Without the
  flag, a pack retains real addresses like a default CLI export. With the flag,
  the pack is private-data-redacted in full, including its tree header.
  File names and paths are address fields and are not masked by secret or
  private-data protection. Callers need their literal values for `project`,
  `path`, and `paths`; when response text contains project-controlled addresses,
  they stay inside the per-response untrusted-data boundary.
- Searches run against content after mandatory secret redaction and any enabled
  private-data redaction, not the original file text. Static-dependency bodies
  produced by `related_files` pass through the same synthetic-document
  redaction before inline delivery or storage, so evidence, specifiers, and
  candidate paths cannot bypass the content policy.
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
  After a server restart, call `pack_context` again to create a new id.
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

### What a connection costs

Before a client asks anything about a project it has already paid for the tool schemas and the
server instructions. Measured on 2026-09-11 from the characters a client received:

| Payload | Characters |
|---|---:|
| `tools/list` result, default server | 35,176 |
| `tools/list` result, `--allow-agent-exclusions` | 39,094 |
| `instructions` | 1,044 |

`analyze` is the largest single tool at 9,219 characters, most of it schema. The `exclusions`
parameter costs a flat 3,918 characters, 653 on each of the six tools that take it. A process
test holds the default `tools/list` result and the instructions under ceilings with deliberate
headroom, and pins the exclusion parameter's cost as an exact difference, so a new parameter or
description has to fit a budget rather than grow one silently.

| Tool | Parameters | Result and limits |
|---|---|---|
| `list_projects` | none | First-call session inventory: allowed local roots with path, name, type, and profiles, plus the server `baseline`. The profile database is read once per call and `profilesStatus` reports an unavailable bounded read. The baseline reports secret/private-data policy and the optional remote-host allowlist. A project tool accepts either a unique listed name or its absolute path. Remote projects are addressed by URL and are not added to this list. |
| `get_tree` | `project?`, `branch?`, `paths?`, `include_patterns?`, `exclude_patterns?`, `tracked_only?`, `git_scope?`, `max_file_bytes?`, `max_depth?`, `format?` | Effective tree in `markdown` (default), `text`, `json`, or `xml`; at most 2,000 lines and 50,000 characters. Without `max_depth`, a large human-readable tree uses the deepest complete depth that fits. |
| `analyze` | `project?`, `branch?`, `paths?`, `include_patterns?`, `exclude_patterns?`, `profile?`, `detail?`, `detail_by_pattern?`, `tracked_only?`, `git_scope?`, `top_files?`, `max_file_bytes?`, `max_tokens?`, `rank?`, `focus?` | File, character, and token metrics plus the requested largest files by tokens. `contentMetrics` separates measured transformed bodies from size-based estimates; `documentMetrics` models `pack_context` with `view=content`, `format=text`, relative file headings, and its Root line. Every ranked file carries `estimated`; an uninspected one also carries `uninspected: true`. The `topFiles` array has a 32,000-character aggregate budget; `topFilesTruncated` and `topFilesRemaining` make any omission explicit. With `max_tokens` the result also carries `admission`: which files that budget would admit, from the same greedy pass `pack_context` uses and without producing content. `rank` and `focus` order that admission and are invalid without `max_tokens`. |
| `pack_context` | `project?`, `branch?`, `paths?`, `include_patterns?`, `exclude_patterns?`, `profile?`, `detail?`, `detail_by_pattern?`, `tracked_only?`, `git_scope?`, `max_tokens?`, `rank?`, `focus?`, `max_file_bytes?`, `view?`, `format?` | Exact DevProjex context pipeline. `max_tokens` measures the safe transformed selection, applies the ordinary token admission order, and materializes only admitted content without changing the budget report. `rank: "importance"` opts into importance-aware admission and document order; `focus` seeds graph-hop order within it. `detail_by_pattern` overrides `detail` per file. Inline through 50,000 characters; otherwise returns a `pack_id` valid until this server process exits. After restart, call `pack_context` again. |
| `read_pack` | `pack_id`, `start_line?`, `end_line?`, `start_column?` | Pages a stored result from `pack_context` or `related_files`. Inclusive, 1-based line range; `start_column` continues within `start_line` using 1-based Unicode characters. At most 1,000 lines or 50,000 characters per call. An `end_line` after EOF is clamped and reported. Call the originating tool again after server restart or quota eviction. |
| `search_project` | `project?`, `branch?`, `pattern`, `paths?`, `include_patterns?`, `exclude_patterns?`, `tracked_only?`, `git_scope?`, `max_file_bytes?`, `context_lines?`, `ignore_case?`, `max_results?` | Grep-style `path:line:text` matches over safe transformed text; line numbers refer to that returned text after replacements. `search_project` matches file content only and never matches paths; use `get_tree` with `include_patterns` to find files by name. The returned match text is capped at 16,000 characters. Overlapping or adjacent context windows are merged and distinct groups use `--`. Regex patterns are limited to 4,096 characters and a 2-second timeout; `max_results` cannot exceed 200, while all additional matches inside the inspected prefix are counted. A request inspects at most 64 MiB of selected source bytes and reports `[Search incomplete]` when later files were not searched. Actual text inserted by redaction never matches. Withheld files are counted in the partial-result warning. |
| `related_files` | `project?`, `branch?`, `path`, `direction?`, `include_patterns?`, `exclude_patterns?`, `profile?`, `tracked_only?`, `git_scope?`, `max_file_bytes?` | Statically evidenced dependencies and dependents for one seed or up to 16 seeds. `direction` is `dependencies`, `dependents`, or `both` (default). The trusted `[Resolution]` line counts resolved, ambiguous, unresolved, and external edges for the call. Coverage distinguishes recognized supported languages from unsupported files and reports configuration diagnostics. Results larger than 50,000 characters use `read_pack`. |
| `get_file` | `project?`, `branch?`, `profile?`, either `path` with `start_line?`, `end_line?`, `start_column?`, or `requests` | Redacted text from one effective file or a batch of up to eight file requests and sixteen ranges. Batch ranges use inclusive `start_line`/`end_line`, read and redact each physical file once, merge overlaps, and report `ok`, `partial`, `not-returned`, or `unavailable` for every range. Both forms share the 1,000-line/50,000-character limit. Coordinates refer to returned text after replacements. A non-empty file that cannot pass the 16 MiB mandatory-redaction boundary is withheld; the single form returns `DPX-MCP-PAYLOAD-TRUNCATED` and never returns an empty success, while batch output reports the count-only unavailable status. `profile` applies the same effective selection and transformations as `analyze` and `pack_context`. Markdown-escaped names copied from default `get_tree` are accepted (`\_` and other ASCII punctuation); use `format: "text"` to copy unescaped names. |

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
an optional `cross-scope` marker. One ambiguous reference is one row with its complete
candidate list. Self-file edges are omitted. For example:

```text
Dependencies:
Application/Context/ProjectContextPlan.cs — one visible declaration identity in csharp:Apps/Mcp/DevProjex.Mcp.csproj · type reference ProjectContextPlan at line 18 — resolved — 642 tokens — cross-scope
Models/Alpha/User.cs — multiple visible declaration identities · type reference User at line 27 — ambiguous — 84 tokens — candidates: Models/Alpha/User.cs, Models/Beta/User.cs
Dependents:
Apps/Terminal/Execution/AnalyzeCommandHandler.cs — one visible declaration identity in csharp:Apps/Terminal/DevProjex.Terminal.csproj · type reference ProjectContextPlan at line 44 — resolved — 1830 tokens — cross-scope
```

Project-derived paths and evidence remain inside the random `untrusted-data` block.
Outside that block, the server appends `[Facts coverage] files=N, supported=N,
unsupported=N, extraction-failed=N` with a short explanation that supported files
produced facts for a recognized language while unsupported files had no extractor.
Configuration state is summarized outside the block as `[Dependency configuration]
problems=N · missing=A · corrupt=B · unsupported-semantics=C · affected-scopes=M`.
At most eight `path · problem` rows plus an `and N more` row stay inside the
untrusted block; parser reasons are not returned. `[Search scope] files=N` and the ordinary
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

## Result Contract

Only `list_projects` and `analyze` declare an MCP `outputSchema`. Their
authoritative result is the complete object in `structuredContent`; the first
text block in `content` is a spotlighted JSON serialization of that same object.
Every field in these two output schemas has a short description. `analyze`
results include an `exclusions` array that echoes the exclusion
tokens effective for the call, so the agent and a human reading the transcript
always see which toggles shaped the measurement. `list_projects` results
include a `baseline` object with the server `git` mode token, the baseline
`exclusions` tokens, and an `agentExclusions` flag. Both fields are new in v5.2
and required on every server, including servers started without the exclusion
flags; consumers that pinned an earlier output schema must refresh it.
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
`privateData` value reflects the server startup policy. A remote result adds
`remote.commit` and `remote.branch` from the pinned checkout session.
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

The budget report and `[Budget accounting]` for `analyze` are appended as trusted text
after the structured block, never inside it, because the first text block stays a
byte-identical serialization of `structuredContent`.

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
paths or project-controlled message text.

### Service notices repeat only when they change

The `[Effective filters]` and `[Protection]` lines describe server state, not the
call, so a session receives them once and then only when what they say changes.
The change signal is the state the lines are made of: the project, the profile,
the effective exclusion set, the Git mode, and the protection policy. A response
that withholds them carries the constant
`[Unchanged] filters, protection; see list_projects.` instead, which is never
longer than the shortest set it can replace, so no response grows by omitting a
notice.

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
`[Search scope]`, `[Budget accounting]`, `[Search totals]`, search and tree
truncation notices, and every `[Warning ...]` — are computed and sent for every
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
does not return every match it found — because `max_results`, the cap, or both
withheld some — it also reports `[Search totals] matches=N · files=M`, the exact
number of matches and the exact number of files containing at least one match inside
the inspected selection. A group cut only in its trailing context lines withheld no
match, so it receives the cap notice without the totals line. Both lines are trusted
counts and constants; no path enters them. A call whose output was not cut and that
returned every match it found keeps its previous response unchanged. The
`[N additional matches not shown]` count keeps its existing meaning and precision.

Unavailable compression is reduced optimization, not unsafe output. The affected
file remains complete, and `analyze`, `pack_context`, and `get_file` append
`[Compression unavailable] failures=N · languages=M` when their effective selection requests
compression. This trusted trailer is outside every project spotlight block and
contains counts only; structured analysis data remains inside its untrusted representation.
Unsupported languages and files rejected by parse or structural safety checks are
separate unchanged-file outcomes and do not produce this trailer.

`get_tree`, `pack_context`, `read_pack`, `search_project`, `related_files`, and `get_file` are
text tools. They do not declare `outputSchema`, omit `structuredContent`, and
return the useful payload directly in the first text block in `content`. This
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

Future tools must declare `outputSchema` only when their useful result is
genuinely structured and can be returned completely in `structuredContent`.
Metadata about a text payload is not sufficient reason to add a schema.

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
glob metacharacters have meaning only in the pattern parameters. Every array
parameter requires a JSON array: a bare string where an array is expected returns
`DPX-MCP-INVALID-ARGUMENTS` naming the argument, for example
`'paths' must be an array of strings.`, instead of a partial or empty result.

### Batch `get_file`

Use `requests` when several source excerpts are already known; use `search_project`
when their locations are not known. Exactly one of `path` or `requests` is required;
supplying both or neither returns `DPX-MCP-INVALID-ARGUMENTS` before any file is read.
The batch contains one to eight records, each shaped as
`{"path":"src/App.cs","ranges":[{"start_line":10,"end_line":30}]}`. A call
contains at most sixteen ranges in total. Each range is inclusive, starts at line
one, and may omit `end_line` to read through EOF. Unknown properties, empty ranges,
and invalid indexed records fail before project access with
`DPX-MCP-INVALID-ARGUMENTS`.

The server resolves every requested path through the effective selection, then
reads, transforms, and redacts each distinct physical file exactly once. Overlapping
or touching ranges for one file become one content section whose header lists the
served request/range indices. Every requested range receives one explicit status:
`ok` when complete, `partial` when cut by the shared response limit,
`not-returned` when no content fitted, or `unavailable` when mandatory bounded
inspection withheld the file. The latter status contains only a count-safe reason.
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

For importance-ranked packing, DevProjex verifies selected file identities again
after ranking and measurement and before transformation. If any selected source
changed, the tool returns `selection changed during packing; retry` instead of
combining measurements and content from different revisions.

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
index changes; changes adds unstaged and non-ignored untracked paths; diff uses
two Git references. The complete value is limited to 4,096 characters. File
content always comes from the current working tree.
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
claude mcp add devprojex-docker -- docker run --rm -i --read-only --tmpfs /tmp -v /absolute/path/to/project:/project:ro ghcr.io/avazbek22/devprojex mcp --root /project
```

For Claude Desktop or Cursor, add this entry to `mcpServers`:

```json
{
  "devprojex-docker": {
    "command": "docker",
    "args": ["run", "--rm", "-i", "--read-only", "--tmpfs", "/tmp", "-v", "/absolute/path/to/project:/project:ro", "ghcr.io/avazbek22/devprojex", "mcp", "--root", "/project"]
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
      "args": ["run", "--rm", "-i", "--read-only", "--tmpfs", "/tmp", "-v", "${workspaceFolder}:/project:ro", "ghcr.io/avazbek22/devprojex", "mcp", "--root", "/project"]
    }
  }
}
```

For OpenAI Codex, add:

```toml
[mcp_servers.devprojex_docker]
command = "docker"
args = ["run", "--rm", "-i", "--read-only", "--tmpfs", "/tmp", "-v", "/absolute/path/to/project:/project:ro", "ghcr.io/avazbek22/devprojex", "mcp", "--root", "/project"]
```

MCP traffic uses stdout exclusively. If startup fails, diagnostics are written
to stderr. Closing the client's stdin terminates the server process.
