# Clean, token-efficient codebase context for AI coding agents, with your secrets masked

**Your AI agent gets just the code it needs, in a smaller form that saves tokens. Works with Claude Code and Codex, right on your computer.**

[![npm](https://img.shields.io/npm/v/devprojex)](https://www.npmjs.com/package/devprojex) [![NuGet](https://img.shields.io/nuget/v/devprojex)](https://www.nuget.org/packages/devprojex) [![License](https://img.shields.io/badge/license-Apache--2.0-blue)](https://github.com/Avazbek22/DevProjex/blob/master/LICENSE) [![Glama score](https://glama.ai/mcp/servers/Avazbek22/DevProjex/badges/score.svg)](https://glama.ai/mcp/servers/Avazbek22/DevProjex)

![DevProjex demo: the desktop app and Terminal Workspace](https://raw.githubusercontent.com/Avazbek22/DevProjex/master/Docs/Media/readme-demo/devprojex-demo.gif)

- **Precise, not bulk.** The agent searches by text or declaration name, follows real imports with `related_files`, and reads exact line ranges instead of whole files.
- **Fewer tokens.** Compact trees, optional code compression, and token-budgeted packs leave the context window for the code that matters.
- **Secrets stay masked.** Redaction is always on in MCP mode, and no flag or tool parameter turns it off.
- **Your boundary, not the agent's.** The server is read-only, jailed to the folders you start it with, and the agent can only narrow what it sees.

## Without DevProjex, and with it

**Without:** an agent with plain file access opens whole files to find one method, pulls `.env` values into the conversation, wanders into build output and vendored code, and reads instructions planted in repository files as if you had written them.

**With DevProjex:** the agent searches first, reads only the declaration or range it needs, and follows dependencies on purpose. Every response is redacted, filtered the way Git sees the project, and fenced off as untrusted data.

## Quick start: Claude Code in 30 seconds

```shell
claude mcp add devprojex -- npx -y devprojex mcp --root /absolute/path/to/project
```

Run `/mcp` in Claude Code and check that `devprojex` is connected. Then ask: "Through DevProjex, show the tree of the current project."

The npm package needs Node.js 20 or later and downloads one self-contained binary for your platform. There is nothing else to install.

## Live Context: steer the agent with checkboxes

![Live Context: the agent's focus follows the files ticked in the DevProjex desktop app](https://raw.githubusercontent.com/Avazbek22/DevProjex/25b05d6c9632ee1a1e99d51632852638cccb66a3/Packaging/Windows/StoreListing/ImportFolder/Screenshots/2_Live_Context/EN.png)

Open your project in the DevProjex desktop app and choose **MCP → Live context → Open in Claude Code** (Codex, Cursor, and VS Code are in the same menu). From then on, the files you tick in the tree are the agent's focus:

- **Tick, untick, done.** The agent works with the new selection on its very next call, with no restart and no new prompt.
- **Your filters are the hard limit.** Files outside your ticks can still be read by name, with a notice; files your filters exclude stay out of reach.
- **See what the agent took.** Turn on **View → Agent activity**, and every file the agent received gets a ✦ marker with a count, such as "Agent received 5 times".
- **Keep a receipt.** The window title shows the connected client, and the agent journal records each call, metadata and counts only and never file bodies, ready to export as Markdown or JSON.

Live mode also works without the window: `devprojex mcp --root <project> --live` follows the selection the desktop app last saved.

![One click in the MCP menu connects Claude Code, Codex, Cursor, or VS Code](https://raw.githubusercontent.com/Avazbek22/DevProjex/25b05d6c9632ee1a1e99d51632852638cccb66a3/Packaging/Windows/StoreListing/ImportFolder/Screenshots/3_Mcp_Menu/EN.png)

## Connect any MCP client

OpenAI Codex, in `~/.codex/config.toml`:

```toml
[mcp_servers.devprojex]
command = "npx"
args = ["-y", "devprojex", "mcp", "--root", "/absolute/path/to/project"]
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

No Node.js? Use the .NET SDK 10.0.100 or later, or Docker:

```shell
dnx devprojex mcp --root /absolute/path/to/project
docker run --rm -i --read-only --tmpfs /tmp -v /absolute/path/to/project:/project:ro ghcr.io/avazbek22/devprojex mcp --root /project --git-mode none
```

`npx` and `dnx` fetch one self-contained binary for Windows, Linux (glibc), or macOS on x64 or arm64. The desktop app and the WinGet and GitHub release binaries include the same server as `devprojex mcp`.

## Try these prompts

- "Through DevProjex, find where the HTTP client is configured and read only that method."
- "Which files use src/Services/OrderService.cs, and what does it depend on?"
- "Search the payment module for TODO comments and tell me which declarations they sit in."
- "Before packing the src folder, measure it: how many tokens, and which files are the largest?"
- "Pack the files changed since main into one document under 20,000 tokens."

## Tools

| Tool | Returns | The agent reaches for it when |
|---|---|---|
| `list_projects` | Allowed project roots, saved profiles, and baseline filters | It does not know which project to work in |
| `get_tree` | The filtered structure, without file contents | It needs the layout of the project or a folder |
| `search_project` | Text or declaration matches, each named by the declaration that contains it | It does not know where something lives |
| `get_file` | A file, a line range, or a declaration, or several of them in one batched call | It knows the location |
| `related_files` | The files a file uses and the files that use it, from a static dependency index over up to 16 seed files | It follows a thread through the code |
| `analyze` | Files, characters, estimated tokens, and the largest files of a selection | It must size the context before producing it |
| `pack_context` | One multi-file document within an optional token budget, plus a report of skipped files | It needs a whole area at once |
| `read_pack` | Line ranges of an oversized stored result | A result was too large to return inline |

All eight tools are annotated read-only and non-destructive, and long operations report standard MCP progress notifications.

## Can an MCP server leak my secrets?

DevProjex is built so that it does not:

- **Redaction you cannot switch off.** In MCP mode, detected credentials such as API keys, tokens, and passwords are replaced before a response leaves the server. No startup flag, profile, or tool parameter disables it. `--hide-private-data` also masks emails, IP and MAC addresses, phone numbers, and user paths.
- **`.env` stays readable, its values do not.** Configuration files remain visible, so the agent understands how the project is wired, while the secret values inside them are masked.
- **Prompt-injection hardening.** Returned file contents are wrapped in randomized untrusted-data markers, so repository text arrives clearly separated from instructions.
- **Read-only and offline.** Tools cannot modify files or run project code. Network access stays off unless you start the server with `--allow-remote` for Git URL projects.
- **Jailed and narrowed.** Access is pinned to the startup roots, and symlink and junction escapes are rejected. The agent can only narrow the selection; widening it is your startup decision, through `--exclude`, `--unrestricted`, or `--allow-agent-exclusions`.

Secret detection is heuristic. The guarantee is control, not detection, so review a pack before sharing it outside your environment. DevProjex protects what it serves: if your client also has built-in file tools, deny them access to secret files in the client's permission settings.

## Built for large codebases and tight token budgets

- **Compact by default.** Trees use compact Markdown, and content declares the root once and then uses relative paths.
- **Noise filtered out.** Smart Ignore hides build output, dependency folders, and caches only when it finds evidence for them, and `.gitignore` applies.
- **Code compression.** Turned on in the desktop window or a saved profile, it keeps declarations and signatures and empties implementation bodies; pure code gets about three times smaller.
- **Budgets that hold.** `max_tokens` fits files into a budget in a deterministic order and lists what was skipped, and `top_files` shows where the tokens go.
- **Only what changed.** `git_scope` narrows trees, search, analysis, packing, and dependencies to staged files, current changes, or a ref-to-ref diff.
- **No flooding.** Oversized results become session packs that `read_pack` returns in line ranges.

Measured on Flask with the task "Find where the session cookie is signed and which configuration keys affect it": packing `src/` up front took 21 MCP calls and 79,653 estimated response tokens. Searching first, reading the three relevant files, and packing only those took 14 calls and 51,075 estimated tokens. These figures measure transport volume, not answer quality; the method is in [Docs/Benchmarks.md](https://github.com/Avazbek22/DevProjex/blob/master/Docs/Benchmarks.md).

## Coming from Repomix or gitingest?

If you have been packing repositories into one file for a chat, DevProjex does that too, through `pack_context`, the CLI, and the desktop app. Its MCP server is designed for the step after that: an agent that explores. It searches, follows dependencies, reads exact ranges, and packs only what the task needs. Redaction cannot be disabled, and you can steer the agent's focus from the desktop app.

## FAQ

**Does my code leave my machine?**
No. The server runs locally over stdio and collects no telemetry. It uses the network only for Git URL projects, and only when you start it with `--allow-remote`.

**How do I keep `.env` secrets away from Claude Code?**
Everything DevProjex returns has secret values masked. If the agent also uses Claude Code's built-in file tools, deny them access to `.env` and other secret files in Claude Code's permission settings.

**Does it handle large codebases?**
Yes. The project inventory is cached and watched for changes, search and reads are bounded, and oversized results are paged. The published benchmarks include Godot, with more than 14,000 tracked files.

**How does it reduce token usage?**
By letting the agent read less. Search hits name their declaration, `get_file` reads one method instead of a whole file, compression drops implementation bodies, and packs respect a token budget.

**Can the agent change my files?**
No. All eight tools are read-only, and the server never runs project code.

**Do I need the desktop app?**
No. `npx`, `dnx`, or Docker are enough for the MCP server. The desktop app adds the visual tree, a live preview, and Live Context.

**Which platforms are supported?**
Windows, Linux, and macOS on x64 and arm64.

## Why you can rely on it

- Selected by the Avalonia UI team for the official App Showcase.
- More than 20,000 automated tests run in CI on Windows, Linux, and macOS.
- Distributed through the Microsoft Store, WinGet, npm, NuGet, Docker, and the official MCP Registry as `io.github.Avazbek22/devprojex`.
- Open source under Apache-2.0, with no telemetry.

## More than an MCP server

The same engine runs as a desktop app with a visual file tree and live preview, a keyboard-first Terminal Workspace (`npx -y devprojex tui .`), and a scriptable CLI. That includes a CI gate: `npx -y devprojex analyze . --findings --fail-on-findings` fails a build when secrets would reach packed context, without printing them.

Install the desktop app from the Microsoft Store, WinGet (`winget install OlimoffDev.DevProjex`), or [GitHub releases](https://github.com/Avazbek22/DevProjex/releases).

## Get started

Add DevProjex to your agent with the one-line quick start above, then explore the rest:

- [GitHub repository](https://github.com/Avazbek22/DevProjex): source, releases, and the full README
- [MCP server reference](https://github.com/Avazbek22/DevProjex/blob/master/Docs/McpServer.md): every tool, parameter, and client setup
- [Issues](https://github.com/Avazbek22/DevProjex/issues): questions, bugs, and ideas

Apache-2.0 © Avazbek Olimov
