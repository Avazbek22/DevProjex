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

The server baseline Git mode can be selected at startup with
`--git-mode none|gitignore|tracked`. This applies only when a tool does not name
an explicit profile. Momentary Git state belongs to request-level `git_scope`
and is intentionally rejected at server startup. `off` is accepted as an input
alias for the canonical `none` token.

Without a Git flag the baseline is `gitignore`, the standard profile's mode.

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
`search_project`, and `get_file` gain an `exclusions` array parameter that
carries the full desired toggle set for that call; an empty array turns every
toggle off, and the value outranks both the server baseline and profile
exclusions. Tokens match case-insensitively and duplicates are rejected.
Without the flag the parameter does not exist in any schema and is rejected as
an unknown argument, so a default server keeps today's narrowing-only contract
unchanged. Turning toggles off widens the per-call scan to trees the baseline
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
list_projects -> get_tree/analyze -> search_project/get_file -> pack_context -> read_pack
```

## Security Model

- Every local project is pinned when the process starts. With `--allow-remote`, a
  remote checkout is pinned on first use and its RepoCache session lease remains
  held until the server stops. Canonical path and symbolic-link checks reject
  access outside those roots. Content files are opened before their final handle
  path is validated, and the validated handle is the one read, so a path swap
  cannot escape the root jail.
- Without `--allow-remote`, tools perform no network operations. With the flag,
  network access is limited to the RepoCache clone/acquire step for a Git URL;
  all project inspection handlers operate only on the pinned checkout. With
  `tracked_only` or `git_scope`, the server may also start the local Git
  executable solely to read repository state. It never runs project executables
  or arbitrary project commands.
- Remote network sources use HTTP(S), SSH, Git protocol, or SCP syntax. Query
  strings and fragments are rejected so credentials cannot enter Git process
  arguments. A `file://` source is accepted only when it resolves inside an
  already configured local root and never expands the local root jail.
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
  `--hide-private-data`. Root paths in `list_projects`, `get_tree`, and tool
  errors are returned as-is; remote tools use the safe Git URL as that address.
  These addresses form the contract for the `project` argument. Without the
  flag, a pack retains real addresses like a default CLI export. With the flag,
  the pack is private-data-redacted in full, including its tree header.
- Searches run against content after mandatory secret redaction and any enabled
  private-data redaction, not the original file text.
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
  limited to 1 GiB. The server does not evict valid packs; a request that would
  exceed either limit returns `DPX-MCP-PACK-TOO-LARGE` with narrowing guidance.

Errors returned by tools have `isError: true` and stable `DPX-MCP-*` codes. They
describe the valid roots, ranges, or retry action. Malformed JSON-RPC traffic is
reported as a protocol error; an unknown tool name is also a protocol-level error
and returns JSON-RPC code `-32602`.

Remote-specific errors are `DPX-MCP-REMOTE-DISABLED` when a URL is passed to a
server started without `--allow-remote`, `DPX-MCP-INVALID-ARGUMENTS` for an
unsupported URL or a branch used with a local path, and `DPX-MCP-REMOTE-FAILED`
when Git, cloning, cache publication, or branch checkout fails.
`DPX-MCP-REMOTE-LIMIT` reports that the 16-source session cap was reached. Error
text uses the credential-free display form of the URL.

### Redaction placeholders

Secrets are replaced before text is returned with placeholders shaped as
`DEVPROJEX_REDACTED[<category>#<n>]`; `search_project` searches that already
redacted text. Known example values, including documentation domains, 555
numbers, keys containing `EXAMPLE`, and reserved IP ranges, are intentionally
allowlisted to keep documentation and fixtures readable.

## Tools

The tool order is stable. Every tool is annotated read-only and non-destructive
because none modifies the source project. `pack_context` is non-idempotent because
each stored result gets a new session id. Without `--allow-remote`, every tool is
closed-world; with it, the five tools that accept `project` Git URLs are annotated
open-world.

| Tool | Parameters | Result and limits |
|---|---|---|
| `list_projects` | none | Allowed local roots with path, name, type, and available local profiles, plus the server `baseline` (`git` mode, `exclusions`, and whether `agentExclusions` is enabled). Remote projects are addressed by URL and are not added to this list. |
| `get_tree` | `project?`, `branch?`, `include_patterns?`, `exclude_patterns?`, `tracked_only?`, `git_scope?`, `max_file_bytes?`, `max_depth?`, `format?` | Effective tree in `markdown` (default), `text`, `json`, or `xml`; at most 2,000 lines. Markdown is the compact, token-efficient default. |
| `analyze` | `project?`, `branch?`, `paths?`, `include_patterns?`, `exclude_patterns?`, `profile?`, `detail?`, `tracked_only?`, `git_scope?`, `top_files?`, `max_file_bytes?` | File, character, and token metrics plus the requested largest files by tokens. Metrics reflect the effective detail level. |
| `pack_context` | `project?`, `branch?`, `paths?`, `include_patterns?`, `exclude_patterns?`, `profile?`, `detail?`, `tracked_only?`, `git_scope?`, `max_tokens?`, `max_file_bytes?`, `view?`, `format?` | Exact DevProjex context pipeline. `max_tokens` limits estimated content tokens. Inline through 50,000 characters; otherwise returns a `pack_id` valid until this server process exits. After restart, call `pack_context` again. |
| `read_pack` | `pack_id`, `start_line?`, `end_line?` | Inclusive, 1-based range; at most 1,000 lines or 50,000 characters per call. Call `pack_context` again after server restart. |
| `search_project` | `project?`, `branch?`, `pattern`, `include_patterns?`, `exclude_patterns?`, `tracked_only?`, `git_scope?`, `max_file_bytes?`, `context_lines?`, `ignore_case?`, `max_results?` | Grep-style redacted matches. Regex patterns are limited to 4,096 characters and a 2-second timeout; `max_results` cannot exceed 200, and oversized text responses are explicitly truncated with a narrowing hint. |
| `get_file` | `project?`, `branch?`, `path`, `start_line?`, `end_line?` | Redacted text from one effective file; at most 1,000 lines or 50,000 characters. Paths copied from any `get_tree` format are accepted. |

On a server started with `--allow-agent-exclusions`, `get_tree`, `analyze`,
`pack_context`, `search_project`, and `get_file` additionally accept the
`exclusions` array parameter described in the startup section, so a file
revealed by a per-call value stays readable through the same value.

For `analyze` and `pack_context`, `paths` accepts at most 256 entries and each
entry is limited to 4,096 Unicode scalar values. Lexically equivalent entries are
deduplicated before root-jail resolution; every unique path still passes the full
physical containment check. Regex, glob, and `git_scope` schema lengths use the
same Unicode scalar-value semantics at runtime.

## Result Contract

Only `list_projects` and `analyze` declare an MCP `outputSchema`. Their
authoritative result is the complete object in `structuredContent`; the first
text block in `content` is a JSON serialization of that same object.
`analyze` results include an `exclusions` array that echoes the exclusion
tokens effective for the call, so the agent and a human reading the transcript
always see which toggles shaped the measurement. `list_projects` results
include a `baseline` object with the server `git` mode token, the baseline
`exclusions` tokens, and an `agentExclusions` flag. Both fields are new in v5.2
and required on every server, including servers started without the exclusion
flags; consumers that pinned the earlier output schemas must refresh them.

Filters are never silent. `get_tree` and `pack_context` end with a trusted
`[Effective filters] git: ...; exclusions: ...` line naming the Git mode and
exclusion toggles that shaped the tree and who can widen them: the server
startup line, or a per-call `exclusions` value on a delegation server. When
`max_file_bytes` is supplied, every tool that accepts it also reports
`; max_file_bytes: <bytes>` in its effective-filter diagnostics. Every selection
tool adds an `[Empty selection]` line when no file survived the
filters and the request arguments, `search_project` adds a `[No matches]` line
with the searched-file count when the pattern matched nothing, and a
`DPX-MCP-PATH-NOT-FOUND` error for a filtered file names the effective filters
and the party able to widen them — the startup line, or a per-call `exclusions`
value on a delegation server. These diagnostics never reveal hidden paths.
When selection produces warnings, `analyze` appends separate human-readable
trusted warning text blocks without changing its structured schema. Warning
messages contain stable codes and safe counts or retry guidance, never diagnostic
paths or project-controlled message text.

If mandatory secret redaction cannot inspect a selected file, including text
larger than the 16 MiB inspection boundary, selection-wide content tools return
a partial success plus a trusted `DPX-MCP-PAYLOAD-TRUNCATED` notice. `search_project`
searches the inspected files, `analyze` preserves its structured metrics
envelope while identifying metrics that may be estimated, and `pack_context`
withholds uninspected content. The notice is outside spotlight delimiters and
reports only a count, never file paths or uninspected content.

`get_tree`, `pack_context`, `read_pack`, `search_project`, and `get_file` are
text tools. They do not declare `outputSchema`, omit `structuredContent`, and
return the useful payload directly in the first text block in `content`. This
avoids JSON escaping and unnecessary token overhead for trees, source text,
search context, and packs. Truncation and continuation metadata is appended as
trusted plain-text trailers outside every project spotlight block, such as
`[Tree truncated ...]` and `[Showing lines ...]`.
For `get_tree`, only `text` and `markdown` use the truncation trailer. If a JSON
or XML tree would exceed 2,000 lines, the tool returns
`DPX-MCP-PAYLOAD-TRUNCATED` with narrowing guidance instead of a partial document.
The `text` tree writes its project address once, followed directly by the real
top-level children; it does not repeat the project name as a synthetic tree node.
Markdown tree Root values and node names escape active CommonMark, HTML, and
entity syntax so project-controlled labels remain literal data.
Markdown context project headings and content-only Root lines use the same
literal escaping. `get_file.path` and `paths` in `analyze` and `pack_context`
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

Future tools must declare `outputSchema` only when their useful result is
genuinely structured and can be returned completely in `structuredContent`.
Metadata about a text payload is not sufficient reason to add a schema.

## Progress Notifications

`pack_context` and `analyze` report measured selection, content-transformation,
and output phases through MCP `notifications/progress`. Notifications are sent
only when the caller supplies a `progressToken` in the request `_meta`; without
that token the server sends none. `progressToken` is transport metadata, not a
tool argument, so tool input schemas are unchanged. Progress is monotonic and
intermediate file-count updates are rate-limited; the other five tools do not
report progress.

Defaults:

- `get_tree.format`: `markdown`
- `pack_context.view`: `tree-content`
- `pack_context.format`: `markdown`
- `pack_context.detail`: `full`
- `pack_context.max_tokens`: unlimited
- `analyze.detail`: `full`
- `analyze.top_files`: `10` (`1..1000`)
- `tracked_only`: `false`
- `git_scope`: absent
- `exclusions`: absent — the server baseline or profile set applies (parameter
  exists only on `--allow-agent-exclusions` servers)
- `search_project.context_lines`: `2`
- `search_project.ignore_case`: `true`
- `search_project.max_results`: `50`

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
`paths` contains existing project-relative files or directories.
Numeric parameters accept JSON numbers and decimal numeric strings.
Boolean parameters accept JSON booleans and the exact strings `"true"` and
`"false"`.

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

For the five project tools, `project` may be a Git URL only when the server was
started with `--allow-remote`. The optional `branch` is valid only with a URL.
Remote checkouts are reused from RepoCache and remain pinned for this server
session. `list_projects` continues to report only configured local roots.
Project-tool calls, including clone/acquire, are serialized; a first clone
therefore delays other project-tool calls until its checkout is ready.

When `tracked_only` is `true`, only paths present in the Git index are selected.
The option can strengthen a profile but cannot disable tracked-only filtering
already enabled by that profile. A non-Git project rejects the option with an
actionable error instead of returning an empty result.

`git_scope` accepts only the narrowing values `staged`, `changes`, and
`diff:<ref>..<ref>` on `get_tree`, `analyze`, `pack_context`, and
`search_project`. It intersects the server/profile baseline and therefore cannot
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

MCP traffic uses stdout exclusively. If startup fails, diagnostics are written
to stderr. Closing the client's stdin terminates the server process.
