# DevProjex

**Turn a folder or repository into clean, AI-ready context, and see exactly what you're sending.**

This .NET tool runs the DevProjex CLI, the interactive Terminal Workspace, and a read-only
MCP server for AI agents. NuGet delivers one self-contained package for your platform, so
no separate .NET runtime is needed. Analysis is read-only, and DevProjex collects no telemetry.

## Quick start

Requires the .NET SDK 10.0.100 or later; `dnx` runs the tool without installing it.

```shell
dnx devprojex tui .
dnx devprojex tree .
dnx devprojex analyze . --compress-code
dnx devprojex export context . --format markdown -o ../context.md
dnx devprojex search Configure . --symbols
```

For a persistent `devprojex` command, install it as a global tool:

```shell
dotnet tool install --global devprojex
```

- `tui` opens the keyboard-first Terminal Workspace: check files in a tree, preview, copy.
- `tree` prints the project tree after Smart Ignore and `.gitignore`.
- `analyze` reports files, size, characters, and estimated tokens, and what code
  compression would save.
- `export context` writes the tree and file contents as Markdown, JSON, XML, or text.
- `search` finds text, `--regex` patterns, or declarations by name with `--symbols`.

## MCP server for AI agents

Claude Code:

```shell
claude mcp add devprojex -- dnx devprojex mcp --root /absolute/path/to/project
```

Cursor, Claude Desktop, Windsurf, and other clients with an `mcpServers` file:

```json
{
  "mcpServers": {
    "devprojex": {
      "command": "dnx",
      "args": ["devprojex", "mcp", "--root", "/absolute/path/to/project"]
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
      "command": "dnx",
      "args": ["devprojex", "mcp", "--root", "${workspaceFolder}"]
    }
  }
}
```

OpenAI Codex, in `~/.codex/config.toml`:

```toml
[mcp_servers.devprojex]
command = "dnx"
args = ["devprojex", "mcp", "--root", "/absolute/path/to/project"]
```

Eight read-only tools cover the workflow: `list_projects`, `get_tree`, `analyze`,
`search_project`, `related_files`, `get_file`, `pack_context`, and `read_pack`.

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

## Platforms

Windows, Linux (glibc), and macOS on x64 and arm64. Alpine Linux and other musl systems
are not supported yet.

DevProjex is also available as an npm package (`npx devprojex`), a Docker image, and a
desktop app with a visual file tree and live preview (Microsoft Store, WinGet, and
[GitHub releases](https://github.com/Avazbek22/DevProjex/releases)).

## Documentation

- [Command line](https://github.com/Avazbek22/DevProjex/blob/master/Docs/CommandLine.md)
- [Terminal Workspace](https://github.com/Avazbek22/DevProjex/blob/master/Docs/TerminalWorkspace.md)
- [MCP server](https://github.com/Avazbek22/DevProjex/blob/master/Docs/McpServer.md)
- [Installation](https://github.com/Avazbek22/DevProjex/blob/master/Docs/Installation.md)

Apache-2.0 © Avazbek Olimov

<!-- mcp-name: io.github.Avazbek22/devprojex -->
