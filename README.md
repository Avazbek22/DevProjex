# DevProjex 📁🌳

**🏆 Officially selected by the Avalonia UI team for the [App Showcase](https://avaloniaui.net/showcase)**

[![Downloads](https://img.shields.io/github/downloads/Avazbek22/DevProjex/total)](https://github.com/Avazbek22/DevProjex/releases) [![npm](https://img.shields.io/npm/v/devprojex)](https://www.npmjs.com/package/devprojex) [![NuGet](https://img.shields.io/nuget/v/devprojex)](https://www.nuget.org/packages/devprojex) [![Build](https://img.shields.io/github/actions/workflow/status/Avazbek22/DevProjex/dotnet.yml)](https://github.com/Avazbek22/DevProjex/actions) [![License](https://img.shields.io/badge/license-Apache--2.0-blue)](LICENSE) ![Last commit](https://img.shields.io/github/last-commit/Avazbek22/DevProjex) ![Platforms](https://img.shields.io/badge/platform-Windows%20%7C%20Linux%20%7C%20macOS-green) [![Glama score](https://glama.ai/mcp/servers/Avazbek22/DevProjex/badges/score.svg)](https://glama.ai/mcp/servers/Avazbek22/DevProjex)

**Turn a real codebase into clean, AI-ready context — and see exactly what you're sending.**

DevProjex turns any folder or codebase into clean, ready-to-use context for AI chats, code reviews, and documentation. Use it as a **GUI**, a **TUI**, a **CLI**, or an **MCP server** for AI agents — whatever fits your workflow.

Choose what you need in an interactive file tree, check the result in a live preview, then export it as **ASCII, Markdown, JSON, or XML**. Need more than text? Export a real copy of your project — a clean **folder or ZIP file** — with the same filters applied.

> 🔒 **Read-only analysis and telemetry-free by design.** DevProjex does not upload your project contents or collect telemetry.

---

## App Demo 🖼️

![DevProjex demo: the desktop app and Terminal Workspace](Docs/Media/readme-demo/devprojex-demo.gif)

---

## Download 🚀

**Microsoft Store:**
👉 [DevProjex](https://apps.microsoft.com/detail/9ndq3nq5m354)

**Latest GitHub release (Windows, macOS, Linux):**
👉 [github.com/Avazbek22/DevProjex/releases/latest](https://github.com/Avazbek22/DevProjex/releases/latest)

**WinGet (Windows):** `winget install OlimoffDev.DevProjex`

### Run without installing

The CLI, TUI, and MCP server run straight from [npm](https://www.npmjs.com/package/devprojex)
or [NuGet](https://www.nuget.org/packages/devprojex), or from a direct release binary:

```shell
npx devprojex tree .    # Node.js 20+
dnx devprojex tree .    # .NET SDK 10
./DevProjex tree .      # release binary
```

The npm and NuGet packages contain the CLI, TUI, and MCP server, but not the
desktop application. Requirements per platform, the direct headless archives, and
the non-root Docker image are all covered in [Docs/Installation.md](Docs/Installation.md).

---

## Why DevProjex? 💡

Copying files into a chat window one by one **doesn't scale**. CLI tools pack a whole project fast, but you **can't see what you're sending** before it's sent.

DevProjex works differently — visual, precise, and local-first:

* **You see what leaves your project** — file tree, live preview, token estimate
* **You control what leaves it** — Smart Ignore, Git modes, secret and private-data redaction, name filters, saved profiles
* **You get more than text** — a real project copy as a folder or ZIP

### Use it for

* **AI assistants** — clean input for ChatGPT, Claude, DeepSeek, Qwen, Kimi
* **AI agents** — Claude Code, Cursor, or any MCP client inspects your project through a read-only, always-redacted server
* **CI pipelines** — fail a build when secrets would leak into packed context, without ever printing the values
* **Restricted environments** where AI agents, remote indexing, or IDE plugins aren't allowed
* **Code reviews** — share structure and only the files that matter
* **Large codebases** — pull out one module instead of the whole repo
* **Teaching** — explain a project's architecture to a teammate or student

### Get started

1. **Open or drop** a project folder.
2. **Choose** folders, files, filters, ignore rules, and output mode.
3. **Preview**, then copy, export, or run the same workflow from the terminal.

Works with any language, repository, or project structure.

---

## Feature overview ✨

**Choose and control**
* **Smart Ignore** — hides build output, dependency folders, and caches, and checks for evidence before hiding anything. [How it works ↓](#how-smart-ignore-works-)
* **Hide Secrets** — masks detected credential values in everything you export. [How it works ↓](#how-hide-secrets-and-hide-private-data-work-)
* **Hide private data** — optionally masks emails, IPs, MACs, phones, and user paths. [How it works ↓](#how-hide-secrets-and-hide-private-data-work-)
* **Code compression** — keeps declarations and signatures, empties implementation bodies; pure code gets ~3× smaller. [How it works ↓](#how-code-compression-works-)
* **Strip comments** and **strip blank lines** — syntax-aware cleanup across 20 language packs, without modifying source files
* File tree with checkboxes, search, and name filters
* Git-aware scopes for `.gitignore`, tracked files, staged files, all current changes, or a ref-to-ref diff

**Preview and export**
* Live preview (tree / content / both) before you copy or export
* Scrollbar markers show search matches and redaction findings at a glance
* Export as ASCII, Markdown, JSON, or XML
* Save to file or clipboard — tree only, content only, or both
* Export a clean copy of your project to a folder or ZIP archive

**Workflow**
* GUI, TUI, CLI, and MCP server — the same engine, four ways to work
* Git tools built in: clone by URL, switch branches, update cached copies
* Local profiles remember your settings per project
* Live counters for lines, characters, and estimated tokens
* Progress bar with safe cancellation

**Interface**
* Light, dark, and system themes, with transparency and blur where supported
* Platform-native keyboard shortcuts — ⌘-based on macOS, Ctrl-based on Windows and Linux
* Tree context menu — reveal in the file manager, copy paths or transformed contents, select one item, expand or collapse a branch
* Localization in 20 languages
* Stays smooth even on very large folders

---

## Project Copy Export 📦

Use **File → Export project → To folder…** or **To ZIP archive…** to create a separate copy of your current selection.

Project copies respect your chosen root folders, file types, ignore rules, and checked items. If nothing is checked, the whole current tree is exported. Directory structure, binary files, and included empty folders are preserved.

When **Hide Secrets** or **Hide private data** is enabled, detected values in text files are replaced. Binary files remain unchanged. This result is intentionally not a byte-for-byte copy and may not build or run.

The source project is never modified, and the result can't be written inside it. The same workflow is available through `devprojex export project`.

---

## Command Line ⚙️

DevProjex isn't only a desktop context builder. The same app runs from the terminal too: a keyboard-first **Terminal Workspace** for interactive work, and a CLI for repeatable, script-friendly project analysis and AI-context export.

![DevProjex Terminal Workspace: the project tree with checkboxes, the context preview, and the parameters panel with content processing, exclusions, and file types](Docs/Media/terminal-workspace/workspace.png)

```bash
devprojex                                                      # open the Terminal Workspace
devprojex open . --preview                                     # open this folder in the desktop app with the preview pane
devprojex tree .                                               # project tree
devprojex tree https://github.com/owner/repo --branch main     # tree of a remote repository
devprojex analyze . --format json                              # files, lines, tokens, largest files
devprojex analyze . --git-mode tracked --exclude smart-ignore  # tracked files only, with Smart Ignore applied
devprojex analyze . --hide-secrets --hide-private-data --findings --fail-on-findings   # CI gate: findings, never values
devprojex export context . --format markdown -o ../devprojex-context.md   # packed context for a chat
devprojex export context https://github.com/owner/repo -o -    # straight from a Git URL to stdout
git diff --name-only | devprojex export context . --select-from - -o -   # only the changed files
devprojex export project . --as zip --hide-secrets -o ../devprojex-submission.zip   # redacted project copy
devprojex search Configure . --symbols                         # declarations by name
devprojex related src/App.cs --direction both                  # what a file uses, and who uses it
devprojex profile save . --hide-secrets on                     # remember these settings for this project
devprojex cache update https://github.com/owner/repo           # refresh a cached clone
```

### What the CLI adds

* Scriptable exports for CI pipelines — JSON reports, stdout output, deterministic files
* Git repository URLs as project sources — analyze, export, open, or start the TUI on a repository straight from its URL through a managed clone cache (`devprojex cache`, `devprojex recent`)
* A redaction pre-flight for CI — `--findings` lists rule, category, file, and source line (never the values), and `--fail-on-findings` fails the pipeline when findings exist
* Composable selection — pipe a file list from `git diff` or any tool into `--select-from -`
* Project search and relations — `devprojex search` finds text, `--regex` patterns, or declarations by name with `--symbols`; `devprojex related` lists the files a file uses and the files that use it
* Documented aliases and short flags (`export ctx`, `export proj`, `-f`, `-n`, `-q`) plus `devprojex help <command>` and shell completion for bash, zsh, fish, and PowerShell
* A keyboard-first Terminal Workspace for interactive work, no desktop app needed
* A searchable Action Palette to find any workflow fast
* The same Git filtering, exclusions, and profiles as the desktop app

See [Docs/CommandLine.md](Docs/CommandLine.md) for the full command reference, and [Docs/TerminalWorkspace.md](Docs/TerminalWorkspace.md) for the interactive terminal interface.

---

## MCP Server 🤖

DevProjex ships a built-in **secure [Model Context Protocol](https://modelcontextprotocol.io) server** (on the official [C# MCP SDK](https://github.com/modelcontextprotocol/csharp-sdk)) that turns any local folder or Git repository into safe, token-efficient codebase context for AI agents — **Claude Code, Codex, Claude Desktop, Cursor, Windsurf, VS Code, or any MCP client** — through the same engine as the GUI, TUI, and CLI:

```bash
devprojex mcp --root /path/to/project
```

### Connect a client

* **Desktop:** **MCP → Live context** or **MCP → Standard**, then **Open in Claude Code**, Codex, Cursor, or VS Code. The submenu you pick decides whether the registration follows the checked tree.
* **Terminal Workspace:** `mcp connect <client> [live|standard]` registers the server without opening another terminal.
* **CLI:** `devprojex mcp connect . --client <client>` (standard mode unless `--mode live` is given). Add `--open` to launch the client, or `--print` when only a manual configuration fragment is needed.
* **Without the desktop app:** `claude mcp add devprojex -- npx -y devprojex mcp --root /absolute/path/to/project`. Snippets for Codex, Claude Desktop, Cursor, and VS Code are in [Docs/McpServer.md](Docs/McpServer.md#client-configuration).

Claude Code and Codex are configured through their installed command; Cursor and VS Code receive a project-local `.cursor/mcp.json` or `.vscode/mcp.json`, merged without changing other servers.

For a first-call check, inspect DevProjex with `/mcp` in Claude Code or run
`codex mcp list`. Then ask: “Through DevProjex, show the tree of the current selection.”
That call appears in the agent journal.

### Live Context: your checkboxes become the agent's focus

![Live Context: the DevProjex window title shows the connected Claude Code session, the ticked files are the agent's focus, and a tooltip on a received file reads "Agent received 5 times"](Docs/Media/readme-demo/live-context.png)

Open the project in the DevProjex window and connect through **MCP → Live context**. From then on the tree you tick is what the agent works with:

* **Tick, untick, done.** The agent sees the new selection on its very next call, through the shared local project profile. No restart, no new prompt.
* **Focus, not a cage.** Tree, search, analysis, dependency, and pack operations stay inside the checked selection. A file outside the ticks can still be read by name, with an outside-focus notice.
* **Your filters stay the limit.** The window's filters and secret protection apply to everything the agent reads; files your filters exclude stay out of reach.
* **Saved results stay honest.** A stored pack is pinned to the revision that created it and must be rebuilt explicitly after the selection changes.
* **See what the agent took.** Every file the agent received is marked in the tree with a count, the window title names the active client or session count, and focused paths are restored when you reopen the project.

Live context uses `--live`; **Standard** connects the same server but does not follow the window selection. The compact **MCP** menu, between File and Git, also holds **Journal** and documentation entries, and after a successful connection an optional PATH dialog can offer the platform-appropriate terminal setup.

### Eight read-only tools

Eight read-only tools cover the whole workflow: `list_projects`, `get_tree`, `analyze`, `search_project`, `related_files`, `get_file`, `pack_context`, and `read_pack`.

* `search_project` finds text or declarations by name, and `get_file` reads an exact line range or one declaration instead of a whole file.
* `related_files` answers "what does this file actually use, and who uses it" from a static dependency index over up to 16 seed files, so following a thread never widens the selection.
* `pack_context` packs a selection under `max_tokens`: files are considered in deterministic order, each is included if its estimated transformed-content tokens fit the remaining budget, otherwise it is reported as skipped and packing continues. `top_files` shows where the tokens go.
* `pack_context` and `related_files` store an oversized result as a session pack; `read_pack` reads it back in line ranges instead of flooding the agent's context. Long operations report standard MCP progress notifications.
* Trees default to compact markdown, content declares the root once and uses relative paths, and every tree ends with a trusted line naming the active filters. `git_scope` narrows any tool to staged files, current changes, or a ref-to-ref diff; `profile` switches between built-in defaults, your saved Desktop selections, or a portable profile file.

### Agent journal: a receipt for every session

![Agent journal: sessions per client with mode, calls, characters, tokens, files, and masked values, and the list of tool calls inside the selected session](Docs/Media/readme-demo/agent-journal.png)

The local **agent journal** records metadata and counts for MCP sessions, never file
bodies or detected secret values. Each session shows the client, the mode, and its totals: calls, characters, estimated tokens, delivered files, masked values, and duration. Open a session and every tool call is listed with its arguments summary, revision, timing, and notices. Read it from the GUI (**MCP → Journal**), TUI, or CLI, and export a
Markdown or JSON **context receipt** showing calls, delivered paths, and totals. Want to audit what the agent gets? Open the same project in the GUI: the engine and redaction pipeline are shared, and filters match when the same profile and parameters are used.

### Security boundaries

The server enforces hard security boundaries on top of DevProjex's read-only design:

* **Read-only by design** — tools cannot modify project files or run project code; network access is disabled unless `--allow-remote` is explicitly enabled for Git URL projects
* **Secret redaction is always on in MCP mode and has no off switch** — not in the server flags, not in the tool schemas, so neither a config mistake nor the agent itself can turn it off
* **Optional private-data masking** via `devprojex mcp --hide-private-data`, mirroring the CLI flag
* **Root jail** — local access is pinned to startup roots; opt-in remote Git URL checkouts are pinned on first use; symlink and junction escapes are rejected
* **The agent can only narrow the view** — agent paths and globs can only narrow the selection, and the `.git` administrative area is never exposed
* **Git's view by default** — Smart Ignore and `.gitignore` apply, while `Dockerfile`, `.github/`, dot-files, and empty files stay visible; widening or narrowing that view is your startup decision, never the agent's by default: a `--exclude` baseline, the widest-baseline `--unrestricted` preset, or per-call agent control behind the opt-in `--allow-agent-exclusions` flag
* **Prompt-injection hardening** — returned file contents are wrapped in untrusted-data markers

The missing off switch is a control guarantee, not a detection guarantee. DevProjex
detects common secret formats, but detection is heuristic; review each pack before
publishing it outside your environment.

See [Docs/McpServer.md](Docs/McpServer.md) for client setup, the full tool reference, and the security model. The measurements behind the MCP server, from 5.1 to 5.2, with the agents' and the blind judge's verdicts verbatim, are in [Docs/Benchmark-History.md](Docs/Benchmark-History.md).

---

## Safety boundaries 🛡️

DevProjex keeps normal processing and MCP tools read-only. The only write inside an
opened project is an explicit Cursor or VS Code MCP connection, which atomically
creates or updates `.cursor/mcp.json` or `.vscode/mcp.json`:

* Does not otherwise edit, rename, move, or delete files in the opened source project
* Does not commit, merge, push, or switch branches in the source repository opened from the user's filesystem. Branch operations are limited to application-owned cached clones.
* Does not include binary file contents in text or AI-context output; redaction rules do not scan binary data
* Writes generated files and project copies only to destinations you choose, outside the source project

---


## How Smart Ignore Works 🧠

Smart Ignore is a local, deterministic filter — not an AI model, and not one big blacklist for the whole folder. It knows where each project starts and ends, and applies the right rules only inside that project.

**It knows project boundaries.** DevProjex finds project markers like `.csproj`, `package.json`, `pyproject.toml`, `go.mod`, or `Cargo.toml`. Rules for that stack (`node_modules`, `bin`/`obj`, virtual environments, build caches) apply only inside the project that owns them. A .NET service won't hide `bin` in an unrelated folder next to it, and a frontend app won't hide `node_modules` somewhere it doesn't belong.

**It checks before it hides anything.** Folder names like `build`, `dist`, or `vendor` can mean generated files — or real source code. Smart Ignore looks for real signs first: package files, compiler output, known build layouts. If there's no clear sign, the folder stays visible.

**It keeps monorepos separate.** Each nested project — frontend, backend, tools, docs — is filtered on its own, based on its own markers. Nothing crosses over between unrelated parts of your workspace.

**Git modes — a separate setting, not part of Smart Ignore:**

| Mode | What it shows |
|---|---|
| `.gitignore` mode | Tracked files and untracked files allowed by repository-local `.gitignore` and `info/exclude`, with opaque embedded repositories and independent declared submodules, following `git status` |
| Tracked-files-only | Only files currently recorded in the Git index |
| Staged | Files with staged changes |
| Changes | Staged, unstaged, and non-ignored untracked files |
| Diff | Files changed between two Git references |

**You stay in control.** Smart Ignore, the Git mode, and basic filters (hidden files, dot-files, empty folders) all work together and can be turned on or off one by one. "Ignored" means excluded from the current view, copy, or export — it never deletes anything from your project.

📖 Full technical details — signature matching, worktree handling, edge cases — are in [`Docs/SmartIgnore.md`](Docs/SmartIgnore.md).

---

## How Code Compression Works 🗜️

An AI rarely needs every implementation line — it needs the shape of your code. Compression keeps declarations, signatures, fields, and class structure, and empties named implementation bodies:

```csharp
// Before — what's in your file (unchanged on disk)
private Command BuildMcpCommand()
{
    var command = new Command("mcp", L("Terminal.Command.Mcp"));
    // … ~40 more lines of implementation
}

// After — what goes into the packed context
private Command BuildMcpCommand()
{ }
```

Measured on DevProjex's own C# sources (619 files), the packed context shrinks from 5.4 MB to 1.7 MB — a 69% cut, roughly 3× smaller. On a mixed repository the saving is lower, because compression only touches code, never test fixtures, JSON assets, or documentation.

It covers 14 languages, parsing with [Tree-sitter](https://tree-sitter.github.io/tree-sitter/) grammars and applying DevProjex's own per-language rules about what must survive (properties in Kotlin, `val`/`var` in Scala, free lambdas everywhere), and it is deliberately conservative: a file it can't process safely stays complete. Comment and blank-line stripping extend the same syntax engine to 20 packs in total — the 14 plus six comments-only packs such as HTML, CSS, YAML, and XML project files. The same transformed content feeds token metrics, context documents, and folder/ZIP exports, in the GUI and via `--compress-code` in the CLI.

📖 Per-language rules and edge cases are in [`Docs/CodeCompression.md`](Docs/CodeCompression.md).

---

## How Hide Secrets and Hide Private Data Work 🔒

Two independent layers. Both replace values inside the output — they never delete a file and never modify your project.

**Hide Secrets** finds credential values — API keys, tokens, connection strings — and masks each one where it stands, keeping the file and all surrounding code:

```text
var botToken = "110201543:AAHdqTcvE…";                          // in your file
var botToken = "DEVPROJEX_REDACTED[telegram-bot-api-token#1]";  // in the export
```

Detection runs a pinned, reviewed [Gitleaks](https://github.com/gitleaks/gitleaks) rule set on DevProjex's own engine, and one secret never costs you the whole file. Findings are marked on the Preview scrollbar, and a false positive can be excluded per match right in Preview. You enable it with one switch in the GUI or `--hide-secrets` in the CLI; in MCP mode it is forced on with no off switch. For pipelines, `--findings` prints rule, file, and line — never the value — and `--fail-on-findings` fails the build.

**Hide private data** is the second, optional layer for personal traces: emails, global IPs, local-user paths, MAC addresses, and international phone numbers become `DEVPROJEX_REDACTED[email#1]`, `[ipv4#1]`, `[local-user#1]`, and so on — the same placeholder for the same finding on every surface, so exported code stays consistent. It is off by default: real projects are full of version strings that look like IPs and sample emails that are not personal. Turn it on per profile in the GUI, or with `--hide-private-data` in the CLI and MCP.

📖 Detection rules and edge cases: [`Docs/HideSecrets.md`](Docs/HideSecrets.md) · [`Docs/HidePrivateData.md`](Docs/HidePrivateData.md)

---

## Documentation 📚

[Installation](Docs/Installation.md) · [Smart Ignore](Docs/SmartIgnore.md) · [Hide Secrets](Docs/HideSecrets.md) · [Hide private data](Docs/HidePrivateData.md) · [Code Compression](Docs/CodeCompression.md) · [Command Line](Docs/CommandLine.md) · [Terminal Workspace](Docs/TerminalWorkspace.md) · [MCP Server](Docs/McpServer.md) · [Agent Journal](Docs/AgentJournal.md) · [Related files](Docs/Dependencies.md) · [Contributing](CONTRIBUTING.md) · [Code of Conduct](CODE_OF_CONDUCT.md)

---

## Tech stack 🧩

![.NET 10](https://img.shields.io/badge/.NET-10-purple) ![WinGet](https://img.shields.io/badge/winget-available-blue) ![Repository size](https://img.shields.io/github/repo-size/Avazbek22/DevProjex) ![Tests](https://img.shields.io/badge/tests-20000%2B-brightgreen)

* **.NET 10**
* **Avalonia UI** (cross-platform)
* **Tree-sitter** parsing · **Gitleaks** rule source · official **MCP** C# SDK — attributions in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)
* Clean Architecture layers (Kernel / Application / Infrastructure / Terminal / Avalonia)
* JSON-based resources (localization, icon mappings, presets)
* 20,000+ automated tests (unit + integration + terminal + UI), run on Windows, Linux, and macOS

**Build from source**

```bash
git clone https://github.com/Avazbek22/DevProjex.git
cd DevProjex
dotnet build -c Release
dotnet test
```

---

## Contributing 🤝

Issues and pull requests are welcome. Good places to start: **UX**, **performance tuning**, **tests**, **localization**, **documentation & screenshots**. Not sure where? Check issues labeled [`good first issue`](https://github.com/Avazbek22/DevProjex/labels/good%20first%20issue).

See [CONTRIBUTING.md](CONTRIBUTING.md) for details.

---

## Support 💛

[![Support DevProjex on Boosty](.github/assets/boosty-support.svg)](https://boosty.to/avazbek22)

---

## License (Apache-2.0) 📄

DevProjex is licensed under the **Apache License 2.0** — free to use, modify, and distribute, with an explicit patent grant. Copyright (c) 2025–present Avazbek Olimov. See [LICENSE](LICENSE) for details.

**Code signing:** Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org). See the [code signing policy](Docs/Code-Signing-Policy.md).
