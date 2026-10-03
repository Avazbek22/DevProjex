# DevProjex MCP Server: codebase context for AI coding agents

**Give Claude Code, Cursor, VS Code, Codex, or any MCP client read-only, always-redacted access to your codebase.** DevProjex lets an agent explore a local folder or Git repository the way a careful engineer would: look at the tree, search, follow dependencies, read exact ranges, and pack multi-file context within a token budget. Secrets are masked before anything reaches the model, and the agent can never widen what you allowed.

[![npm](https://img.shields.io/npm/v/devprojex)](https://www.npmjs.com/package/devprojex) [![NuGet](https://img.shields.io/nuget/v/devprojex)](https://www.nuget.org/packages/devprojex) [![License](https://img.shields.io/badge/license-Apache--2.0-blue)](https://github.com/Avazbek22/DevProjex/blob/master/LICENSE)

![DevProjex demo: the desktop app and Terminal Workspace](https://raw.githubusercontent.com/Avazbek22/DevProjex/master/Docs/Media/readme-demo/devprojex-demo.gif)

## Why agents work better with it

- **Targeted reads instead of whole-repo dumps.** Search names the declaration behind every hit, `get_file` reads one declaration or line range, and `related_files` follows real imports. The agent spends tokens on the code that matters.
- **No secrets in the context window.** Redaction is mandatory in MCP mode. There is no flag or tool parameter that turns it off.
- **A boundary the agent cannot cross.** Access is jailed to the roots you start the server with; agent paths and globs can only narrow the selection.
- **Local and telemetry-free.** The server runs on your machine over stdio and never uploads project contents.

## Start the server

No install step. Pick the runtime you already have:

```shell
npx -y devprojex mcp --root /absolute/path/to/project
dnx devprojex mcp --root /absolute/path/to/project
docker run --rm -i --read-only --tmpfs /tmp -v /absolute/path/to/project:/project:ro ghcr.io/avazbek22/devprojex mcp --root /project --git-mode none
```

`npx` needs Node.js 20 or later, and `dnx` needs the .NET SDK 10.0.100 or later. Both fetch one self-contained binary for Windows, Linux (glibc), or macOS on x64 or arm64. The desktop app and the WinGet and GitHub release binaries include the same server as `devprojex mcp`.

## Connect your client

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

Then ask your agent something like:

- "Through DevProjex, show the tree of the src folder."
- "Find where the HTTP client is configured and read only that method."
- "Which files depend on src/Services/OrderService.cs?"
- "Pack the files changed since main into one document under 20,000 tokens."

## Tools

| Tool | What it does |
|---|---|
| `list_projects` | Lists the allowed project roots, saved profiles, and baseline filters. |
| `get_tree` | Returns the filtered structure without file contents. |
| `search_project` | Searches redacted text, or declarations by name, and names the declaration that contains each hit. |
| `get_file` | Reads a file, a line range, or a declaration, or several of them in one batched call. |
| `related_files` | Lists the files a file uses and the files that use it, from a static dependency index over up to 16 seed files. |
| `analyze` | Measures a selection before packing: files, characters, estimated tokens, and the largest files. |
| `pack_context` | Builds one multi-file context document, fits files into an optional token budget, and reports what was skipped. |
| `read_pack` | Reads an oversized stored result back in line ranges. |

Every tool is annotated read-only and non-destructive, and long operations report standard MCP progress notifications.

## Safety by design

- **Read-only.** Tools cannot modify project files or run project code. Network access is off unless `--allow-remote` is set for Git URL projects.
- **Secret redaction is always on** in MCP mode, with no off switch in the server flags or the tool schemas. `--hide-private-data` also masks emails, IP and MAC addresses, phone numbers, and user paths.
- **Root jail.** Access is pinned to the startup roots, and symlink and junction escapes are rejected.
- **The agent can only narrow the selection.** By default it sees the repository the way Git does, with Smart Ignore and `.gitignore` applied, and every tree ends with a trusted line naming the active filters. Widening is your startup decision: `--exclude`, `--unrestricted`, or per-call control behind `--allow-agent-exclusions`.
- **Prompt-injection hardening.** Returned file contents are wrapped in untrusted-data markers.

Secret detection is heuristic. The missing off switch is a control guarantee, not a detection guarantee, so review a pack before publishing it outside your environment.

## Built for token efficiency

- Trees default to compact Markdown, and content declares the root once and uses relative paths.
- `max_tokens` packs files in a deterministic order and reports the skipped ones; `top_files` shows where the tokens go.
- `git_scope` narrows trees, analysis, search, packing, and dependencies to staged files, current changes, or a ref-to-ref diff.
- Oversized results become session packs that `read_pack` returns in line ranges, so a large answer never floods the context window.

## Live Context with the desktop app

Start the server with `--live`, or choose **MCP → Live context** in the DevProjex desktop app. The files you tick in the window become the agent's focus on its next call, with no session restart. The window's filters stay the hard limit of what the agent can read. An agent journal records every call, metadata and counts only and never file bodies, and exports a receipt of what was delivered.

## FAQ

**Does my code leave my machine?** No. The server runs locally over stdio, collects no telemetry, and only touches the network for Git URL projects when you start it with `--allow-remote`.

**Can the agent change my files?** No. Every tool is read-only, and the server never runs project code.

**Do I need the desktop app?** No. `npx`, `dnx`, or Docker are enough for the MCP server. The desktop app adds a visual file tree, a live preview, and Live Context.

**Which platforms are supported?** Windows, Linux, and macOS on x64 and arm64.

## More than an MCP server

The same engine runs as a desktop app with a visual file tree and live preview, a keyboard-first Terminal Workspace (`npx -y devprojex tui .`), and a scriptable CLI. That includes a CI gate: `npx -y devprojex analyze . --findings --fail-on-findings` fails a build when secrets would reach packed context, without printing them.

Install the desktop app from the Microsoft Store, WinGet (`winget install OlimoffDev.DevProjex`), or [GitHub releases](https://github.com/Avazbek22/DevProjex/releases).

## Links

- [Full README](https://github.com/Avazbek22/DevProjex#readme)
- [MCP server reference](https://github.com/Avazbek22/DevProjex/blob/master/Docs/McpServer.md): every tool, parameter, and client setup
- Official MCP Registry entry: `io.github.Avazbek22/devprojex`

Apache-2.0 © Avazbek Olimov
