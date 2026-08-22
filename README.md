<h1 align="center">DevProjex 📁🌳</h1>

<h2 align="center">🏆 Officially Selected by the Avalonia UI Team for the <a href="https://avaloniaui.net/showcase">App Showcase</a></h2>

<p align="center">
  <a href="https://github.com/Avazbek22/DevProjex/releases"><img alt="Downloads" src="https://img.shields.io/github/downloads/Avazbek22/DevProjex/total"></a>
  <a href="https://github.com/Avazbek22/DevProjex/actions"><img alt="Build" src="https://img.shields.io/github/actions/workflow/status/Avazbek22/DevProjex/dotnet.yml"></a>
  <a href="https://github.com/Avazbek22/DevProjex/blob/master/LICENSE"><img alt="License" src="https://img.shields.io/github/license/Avazbek22/DevProjex"></a>
  <img alt="Last commit" src="https://img.shields.io/github/last-commit/Avazbek22/DevProjex">
  <img alt="Platforms" src="https://img.shields.io/badge/platform-Windows%20%7C%20Linux%20%7C%20macOS-green">
</p>

<p align="center">
  <strong>The fastest way to turn a real codebase into clean, AI-ready context.</strong>
</p>

DevProjex turns any folder or codebase into clean, ready-to-use context for AI chats, code reviews, and documentation. Use it as a **GUI**, a **TUI**, or a **CLI** — whatever fits your workflow.

Choose what you need in an interactive file tree, check the result in a live preview, then export it as **ASCII, Markdown, JSON, or XML**. Need more than text? Export a real copy of your project — a clean **folder or ZIP file** — with the same filters applied.

> 🔒 **Read-only and telemetry-free by design.** DevProjex does not upload your project contents or collect telemetry.

---

## App Demo 🖼️

<img src="Docs/Media/readme-demo/devprojex-demo-04-readme.gif" alt="DevProjex desktop app demo" width="100%" />

---

## Download 🚀

**Download from Microsoft Store:**
👉 [DevProjex](https://apps.microsoft.com/detail/9ndq3nq5m354)

**Latest GitHub release:**
👉 [https://github.com/Avazbek22/DevProjex/releases/latest](https://github.com/Avazbek22/DevProjex/releases/latest)

**Install via WinGet (Windows):** `winget install OlimoffDev.DevProjex`

---

## Why DevProjex? 💡

Copying files into a chat window one by one **doesn't scale**. CLI tools pack a whole project fast, but you **can't see what you're sending** before it's sent.

DevProjex works differently — visual, precise, and local-first:

* **You see what leaves your project** — file tree, live preview, token estimate
* **You control what leaves it** — Smart Ignore, Git modes, secret and private-data redaction, name filters, saved profiles
* **You get more than text** — a real project copy as a folder or ZIP

### Use it for

* **AI assistants** — clean input for ChatGPT, Claude, DeepSeek, Qwen
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
* **Smart Ignore** — filters stack-specific build output, dependency folders, and caches without touching your source. [How it works ↓](#how-smart-ignore-works-)
* **Hide Secrets** — replaces detected credential values in place while keeping the file and surrounding code. [Details](Docs/HideSecrets.md)
* **Hide private data** — hides selected-content email, global IP, local-user path, MAC, and international-phone findings. [Details](Docs/HidePrivateData.md)
* **Code compression** — keeps declarations and state while shortening named implementations across 14 languages
* **Strip comments** — removes comments and documentation comments across 20 language packs without modifying source files
* **Strip blank lines** — removes whitespace-only source lines across the same 20 syntax-aware language packs while preserving multiline literals and markup text
* File tree with checkboxes, search, and name filters
* Two Git-aware modes: follow `.gitignore`, or show only tracked files

**Preview and export**
* Live preview (tree / content / both) before you copy or export
* Scrollbar markers show search matches and redaction findings at a glance
* Export as ASCII, Markdown, JSON, or XML
* Save to file or clipboard — tree only, content only, or both
* Export a clean copy of your project to a folder or ZIP archive

**Workflow**
* GUI, TUI, and CLI — the same engine, three ways to work
* Git tools built in: clone by URL, switch branches, update cached copies
* Local profiles remember your settings per project
* Live counters for lines, characters, and estimated tokens
* Progress bar with safe cancellation

**Interface**
* Light, dark, and system themes, with transparency and blur where supported
* Platform-native keyboard shortcuts — ⌘-based on macOS, Ctrl-based on Windows and Linux
* Tree context menu — reveal in the system file manager, copy full or relative paths, copy a file's transformed contents, select only one item, expand or collapse a branch
* Localization in 11 languages
* Stays smooth even on very large folders

---

## DevProjex vs the alternatives ⚖️

| Feature | DevProjex | Repomix | gitingest | code2prompt | GPTree | files-to-prompt |
|---|---|---|---|---|---|---|
| GUI + TUI + CLI — all in one app | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ |
| Live preview before export | ✅ | ❌ | ❌ | ✅ | ✅ | ❌ |
| Tracked-files-only Git mode | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ |
| Scope-aware, evidence-based Smart Ignore (monorepo-safe) | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ |
| In-place secret redaction with per-match Preview override | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ |
| Dedicated ASCII-tree-only export | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ |
| Export a clean project copy as folder/ZIP | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ |
| GUI-managed Git workflow (clone, branch switch, cache updates) | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ |
| Run with no install (npx / uvx / browser) | ❌ | ✅ | ✅ | ❌ | ❌ | ✅ |

---

## Project Copy Export 📦

Use **File → Export Project → To Folder…** or **To ZIP Archive…** to create a separate copy of your current selection.

Project copies respect your chosen root folders, file types, ignore rules, and checked items. If nothing is checked, the whole current tree is exported. Directory structure, binary files, and included empty folders are preserved.

When **Hide Secrets** or **Hide private data** is enabled, detected values in text files are replaced. Binary files remain unchanged. This result is intentionally not a byte-for-byte copy and may not build or run.

The source project is never modified, and the result can't be written inside it. The same workflow is available through `devprojex export project`.

---

## Command Line ⚙️

DevProjex isn't only a desktop context builder. The same app runs from the terminal too, for repeatable, script-friendly project analysis and AI-context export.

```bash
devprojex
devprojex open . --preview
devprojex tree .
devprojex analyze . --format json
devprojex analyze . --hide-secrets --hide-private-data --findings --fail-on-findings
devprojex export context . --format markdown -o ../devprojex-context.md
devprojex export context https://github.com/owner/repo -o -
git diff --name-only | devprojex export context . --select-from - -o -
devprojex export project . --as zip --hide-secrets -o ../devprojex-submission.zip
devprojex analyze . --git-mode tracked --exclude smart-ignore
```

### What the CLI adds

* Scriptable exports for CI pipelines — JSON reports, stdout output, deterministic files
* Git repository URLs as project sources — analyze, export, open, or start the TUI on a repository straight from its URL through a managed clone cache (`devprojex cache`, `devprojex recent`)
* A redaction pre-flight for CI — `--findings` lists rule, category, file, and source line (never the values), and `--fail-on-findings` fails the pipeline when findings exist
* Composable selection — pipe a file list from `git diff` or any tool into `--select-from -`
* Documented aliases and short flags (`export ctx`, `export proj`, `-f`, `-n`, `-q`) plus `devprojex help <command>` and shell completion for bash, zsh, fish, and PowerShell
* A keyboard-first Terminal Workspace for interactive work, no desktop app needed
* A searchable Action Palette to find any workflow fast
* The same Git filtering, exclusions, and profiles as the desktop app

See [Docs/CommandLine.md](Docs/CommandLine.md) for the full command reference, and [Docs/TerminalWorkspace.md](Docs/TerminalWorkspace.md) for the interactive terminal interface.

---

## Safety boundaries 🛡️

DevProjex keeps your source projects read-only, with clear limits on what it does:

* Does not edit, rename, move, or delete files in the opened source project
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
| `.gitignore` mode | Tracked files, plus untracked files not excluded by `.gitignore` (with nested rules and negations) |
| Tracked-files-only | Only files currently recorded in the Git index |

**You stay in control.** Smart Ignore, the Git mode, and basic filters (hidden files, dot-files, empty folders) all work together and can be turned on or off one by one. "Ignored" means excluded from the current view, copy, or export — it never deletes anything from your project.

📖 Full technical details — signature matching, worktree handling, edge cases — are in [`Docs/SmartIgnore.md`](Docs/SmartIgnore.md).

---

## Documentation 📚

[Smart Ignore](Docs/SmartIgnore.md) · [Hide Secrets](Docs/HideSecrets.md) · [Hide private data](Docs/HidePrivateData.md) · [Command Line](Docs/CommandLine.md) · [Terminal Workspace](Docs/TerminalWorkspace.md) · [Contributing](CONTRIBUTING.md) · [Code of Conduct](CODE_OF_CONDUCT.md)

---

## Tech stack 🧩

<p>
  <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10-purple">
  <img alt="WinGet" src="https://img.shields.io/badge/winget-available-blue">
  <img alt="Repository size" src="https://img.shields.io/github/repo-size/Avazbek22/DevProjex">
  <img alt="Tests" src="https://img.shields.io/badge/tests-10000%2B-brightgreen">
</p>

* **.NET 10**
* **Avalonia UI** (cross-platform)
* Clean Architecture layers (Kernel / Application / Infrastructure / Terminal / Avalonia)
* JSON-based resources (localization, icon mappings, presets)
* 10,000+ automated tests (unit + integration + UI)

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

<p align="center">
  <a href="https://boosty.to/avazbek22">
    <img src=".github/assets/boosty-support.svg" width="800" alt="Support DevProjex on Boosty">
  </a>
</p>

---

## License (GPL-3.0) 📄

DevProjex is licensed under the **GNU General Public License v3.0** — this keeps the project, and any tool built on top of it, open source. Copyright (c) 2025–present Avazbek Olimov. See [LICENSE](LICENSE) for details.
