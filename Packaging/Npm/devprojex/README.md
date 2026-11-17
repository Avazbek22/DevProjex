# DevProjex: codebase context for AI coding agents

**Turn any folder or Git repository into token-efficient codebase context for Claude Code,
Codex, Cursor, and other AI coding agents.**

Your agent reads `.env` as easily as `README.md`, and burns tokens opening whole files to find
one function. DevProjex fixes both.

- **Finds instead of dumping.** Search by text, regex, or declaration name, then read one
  declaration or a line range, not the whole file.
- **Knows how your files connect.** `related_files` follows real imports: what a file uses,
  who uses it, and which tests import it.
- **Shrinks what is left.** Code compression keeps signatures and drops bodies, so pure code
  gets about 3× smaller; Git diffs carry only the changed lines.
- **Keeps secrets in.** Detected credentials are masked before anything reaches the model, and
  in MCP mode there is no switch to turn that off.
- **Live Context.** Tick files in the Terminal Workspace or the desktop app, and they become
  the agent's focus on its very next call. No restart, no new prompt.
- **Agent journal.** A receipt for every session: calls, delivered files, tokens, and masked
  values. Never file bodies.

**Measured, not promised:** in our 18-task benchmark (12 Nov 2026, Claude Haiku 4.5 in Claude
Code), agents used up to 40% fewer context tokens through DevProjex than through Repomix 1.19:
28.8% and 40.9% across two runs. In the 28.8% run they also named 78 of 84 required files
against 66. Raw numbers:
[Docs/Benchmarks](https://github.com/Avazbek22/DevProjex/blob/master/Docs/Benchmarks/README.md).

This package runs the DevProjex CLI, the interactive Terminal Workspace, and a read-only
MCP server for AI agents. npm installs one self-contained binary for your platform, so no
.NET runtime is needed. Analysis is read-only, and DevProjex collects no telemetry.

## Quick start

```shell
npx -y devprojex tui .
npx -y devprojex tree .
npx -y devprojex analyze . --compress-code
npx -y devprojex export context . --format markdown -o ../context.md
npx -y devprojex search Configure . --symbols
```

- `tui` opens the keyboard-first Terminal Workspace: check files in a tree, preview, copy.
- `tree` prints the project tree after Smart Ignore and `.gitignore`.
- `analyze` reports files, size, characters, and estimated tokens, and what code
  compression would save.
- `export context` writes the tree and file contents as Markdown, JSON, XML, or text.
- `search` finds text, `--regex` patterns, or declarations by name with `--symbols`.

## MCP server for AI agents

To generate or register a connection from this package, use
`npx -y devprojex mcp connect /absolute/path/to/project --client claude-code`.
Add `--print` to print the configuration without changing client settings, or
`--mode live` to follow the saved window selection. Generated connections use
`npx -y devprojex`, not the temporary binary path in npm's cache. The client needs
Node.js/npm on `PATH`; npm may restore the package after a cache cleanup.

Claude Code:

```shell
claude mcp add devprojex -- npx -y devprojex mcp --root /absolute/path/to/project
```

Cursor, Claude Desktop, Windsurf, and other clients with an `mcpServers` file:

```json
{
  "mcpServers": {
    "devprojex": {
      "command": "npx",
      "args": ["-y", "devprojex", "mcp", "--root", "/absolute/path/to/project"]
    }
  }
}
```

VS Code, in `.vscode/mcp.json`:

```json
{
  "servers": {
    "devprojex": {
      "type": "stdio",
      "command": "npx",
      "args": ["-y", "devprojex", "mcp", "--root", "${workspaceFolder}"]
    }
  }
}
```

OpenAI Codex, in `~/.codex/config.toml`:

```toml
[mcp_servers.devprojex]
command = "npx"
args = ["-y", "devprojex", "mcp", "--root", "/absolute/path/to/project"]
```

Six read-only tools are published by default: `get_tree`, `search_project`, `get_file`,
`related_files`, `get_diff`, and `read_pack`. Start the server with `--tool-set full` for all
nine, adding `list_projects`, `analyze`, and `pack_context`.

- Secret redaction is always on in MCP mode and has no off switch.
- Access is pinned to the startup root; symlink and junction escapes are rejected.
- The agent can only narrow the selection. By default it sees the repository the way
  Git does, and every tree ends with a trusted line naming the active filters.
- Returned file contents are wrapped in untrusted-data markers against prompt injection.
- Oversized results are stored as a session pack and read back in line ranges.

## What else it does

- **Smart Ignore** hides build output, dependency folders, and caches only when it finds
  evidence for them, and stays monorepo-safe.
- **Hide Secrets** masks detected credential values and keeps the rest of the file.
  `--hide-private-data` also masks emails, IP and MAC addresses, phone numbers, and user paths.
- **Code compression** keeps declarations and signatures and empties implementation
  bodies; pure code gets about three times smaller.
- **Git scopes** select tracked files, staged files, current changes, or a ref-to-ref diff.
- **CI gate**: `analyze . --findings --fail-on-findings` fails a build when secrets would
  reach packed context, without printing the values.

## Requirements

- Node.js 20 or later.
- Windows, Linux (glibc), or macOS on x64 or arm64. Alpine Linux and other musl systems
  are not supported yet.
- The launcher has no dependencies, install scripts, downloads, or version checks. The
  binary arrives as an optional dependency, so do not install with `--omit=optional`.

DevProjex is also available as a .NET tool (`dnx devprojex`), a Docker image, and a
desktop app with a visual file tree and live preview (Microsoft Store, WinGet, and
[GitHub releases](https://github.com/Avazbek22/DevProjex/releases)).

## Documentation

- [Command line](https://github.com/Avazbek22/DevProjex/blob/master/Docs/CommandLine.md)
- [Terminal Workspace](https://github.com/Avazbek22/DevProjex/blob/master/Docs/TerminalWorkspace.md)
- [MCP server](https://github.com/Avazbek22/DevProjex/blob/master/Docs/McpServer.md)
- [Installation](https://github.com/Avazbek22/DevProjex/blob/master/Docs/Installation.md)

Apache-2.0 © Avazbek Olimov
