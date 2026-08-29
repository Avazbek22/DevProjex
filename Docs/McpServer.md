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
  `tracked_only`, the server may also start the local Git executable solely to
  read the repository index. It never runs project executables or arbitrary
  project commands.
- Secret redaction is always enabled for returned file content and cannot be
  disabled. Private-data redaction is disabled by default and can be enabled
  only for the whole server process with `--hide-private-data`, mirroring the
  CLI flag. Tool schemas intentionally expose no redaction controls.
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
- Product exclusions, including built-in rules and `.gitignore`, remain active.
  Agent paths and globs can only narrow the effective selection.
- Large packs are kept in an application-owned temporary session directory. Pack
  ids are random, valid only in the current server process, and removed at exit.
  Stale session directories older than 24 hours are scavenged at startup. A
  stored pack is limited to 200 MiB and all packs in one server session are
  limited to 1 GiB. The server does not evict valid packs; a request that would
  exceed either limit returns `DPX-MCP-PACK-TOO-LARGE` with narrowing guidance.

Errors returned by tools have `isError: true` and stable `DPX-MCP-*` codes. They
describe the valid roots, ranges, or retry action. Malformed JSON-RPC traffic is
the only case reported as a protocol error.

Remote-specific errors are `DPX-MCP-REMOTE-DISABLED` when a URL is passed to a
server started without `--allow-remote`, `DPX-MCP-INVALID-ARGUMENTS` for an
unsupported URL or a branch used with a local path, and `DPX-MCP-REMOTE-FAILED`
when Git, cloning, cache publication, or branch checkout fails. Error text uses
the credential-free display form of the URL.

## Tools

The tool order is stable.

| Tool | Parameters | Result and limits |
|---|---|---|
| `list_projects` | none | Allowed local roots with path, name, type, and available local profiles. Remote projects are addressed by URL and are not added to this list. |
| `get_tree` | `project?`, `branch?`, `include_patterns?`, `exclude_patterns?`, `tracked_only?`, `max_file_bytes?`, `max_depth?` | Effective text tree; at most 2,000 lines. |
| `analyze` | `project?`, `branch?`, `paths?`, `include_patterns?`, `exclude_patterns?`, `profile?`, `detail?`, `tracked_only?`, `top_files?`, `max_file_bytes?` | File, character, and token metrics plus the requested largest files by tokens. Metrics reflect the effective detail level. |
| `pack_context` | `project?`, `branch?`, `paths?`, `include_patterns?`, `exclude_patterns?`, `profile?`, `detail?`, `tracked_only?`, `max_tokens?`, `max_file_bytes?`, `view?`, `format?` | Exact DevProjex context pipeline. `max_tokens` limits estimated content tokens. Inline through 50,000 characters; otherwise returns a session-scoped `pack_id` and tree. |
| `read_pack` | `pack_id`, `start_line?`, `end_line?` | Inclusive, 1-based range; at most 1,000 lines or 50,000 characters per call. Call `pack_context` again after server restart. |
| `search_project` | `project?`, `branch?`, `pattern`, `include_patterns?`, `exclude_patterns?`, `tracked_only?`, `max_file_bytes?`, `context_lines?`, `ignore_case?`, `max_results?` | Grep-style redacted matches. Regex patterns are limited to 4,096 characters and a 2-second timeout; `max_results` cannot exceed 200, and oversized text responses are explicitly truncated with a narrowing hint. |
| `get_file` | `project?`, `branch?`, `path`, `start_line?`, `end_line?` | Redacted text from one effective file; at most 1,000 lines or 50,000 characters. |

## Result Contract

Only `list_projects` and `analyze` declare an MCP `outputSchema`. Their
authoritative result is the complete object in `structuredContent`; the first
text block in `content` is a JSON serialization of that same object.

`get_tree`, `pack_context`, `read_pack`, `search_project`, and `get_file` are
text tools. They do not declare `outputSchema`, omit `structuredContent`, and
return the useful payload directly in the first text block in `content`. This
avoids JSON escaping and unnecessary token overhead for trees, source text,
search context, and packs. Truncation and continuation metadata is embedded in
plain-text trailers such as `[Tree truncated ...]` and `[Showing lines ...]`.

An inline `pack_context` result contains the complete pack. A stored result is
self-contained: it starts with `Pack stored as '<id>' (<N> characters). Call
read_pack ...`, followed by a preview of the project tree. Clients extract the
session-scoped `pack_id` from that text and pass it to `read_pack`.

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

- `pack_context.view`: `tree-content`
- `pack_context.format`: `markdown`
- `pack_context.detail`: `full`
- `pack_context.max_tokens`: unlimited
- `analyze.detail`: `full`
- `analyze.top_files`: `10` (`1..1000`)
- `tracked_only`: `false`
- `search_project.context_lines`: `2`
- `search_project.ignore_case`: `true`
- `search_project.max_results`: `50`

`include_patterns` and `exclude_patterns` are arrays of project-relative globs
using `/`. Each array accepts at most 256 non-empty patterns, with at most 512
characters per pattern. `paths` contains existing project-relative files or
directories.
Numeric parameters accept JSON numbers and decimal numeric strings.
Boolean parameters accept JSON booleans and the exact strings `"true"` and
`"false"`.

`max_tokens` is an integer of at least 1 and accepts either a JSON number or a
decimal numeric string. It uses the existing estimate of one token per four
transformed characters, rounded up per file. Files are considered in the
deterministic selection order: a file is included when its estimate fits the
remaining budget; otherwise it is reported as skipped and later, smaller files
are still considered. A budget that fits no files is a valid empty-content
pack. Tree text, file headings, markup, and other document structure are not
charged to this content budget. Consequently, the report's included-token sum
can differ slightly from the complete document metric, which normalizes line
endings and includes document output differently.

`max_file_bytes` is an optional positive byte count on `get_tree`, `analyze`,
`pack_context`, and `search_project`. It removes files strictly larger than the
limit after profile, ignore, Git, `paths`, and glob narrowing; a file exactly at
the limit remains selected. It is request-only, is not stored in profiles, and
does not apply to `get_file`, which addresses one already-effective file.

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

Profiles use the existing project profile mechanism: `standard`, `local`, or a
portable profile JSON path inside the project root. Profile selection can enable
compression or stripping, but cannot disable secret redaction or alter the
server-level private-data policy.

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
