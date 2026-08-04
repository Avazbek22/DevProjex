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

> 🔒 **Read-only and telemetry-free by design.** DevProjex never modifies your source files, and nothing ever leaves your machine.

---

## App Demo 🖼️

<img src="Docs/Media/readme-demo/devprojex-demo-04-readme.gif" alt="DevProjex desktop app demo" width="100%" />

---

## Download 🚀

**Download from Microsoft Store:**
👉 [Download from Microsoft Store](https://apps.microsoft.com/detail/9ndq3nq5m354)

**Latest GitHub release:**
👉 [https://github.com/Avazbek22/DevProjex/releases/latest](https://github.com/Avazbek22/DevProjex/releases/latest)

**Install via WinGet (Windows):** `winget install OlimoffDev.DevProjex`

---

## Why DevProjex? 💡

Copying files into a chat window one by one doesn't scale past a few files. CLI tools can pack a whole project fast, but you can't see what you're sending before it's sent.

DevProjex works differently: visual, precise, and fully offline. You see and choose exactly what leaves your project, check it in a live preview, and export a real project copy when you need one — not just a block of text.

### Use it for

* Clean input for AI assistants (ChatGPT, Claude, DeepSeek, Qwen, etc.)
* Policy-restricted environments where AI agents, remote indexing, or IDE plugins are not allowed
* Sharing project structure in code reviews or chats
* Pulling only the relevant modules out of a large codebase
* Explaining a project's architecture to a teammate or student
* Looking through large folders without messy CLI output

### Get started

1. Open or drop a project folder.
2. Choose the folders, files, filters, ignore rules, and output mode you need.
3. Check the result in preview, then copy, export, or run the same workflow from the terminal.

DevProjex works with any language, repository, or project structure.

---

## Feature overview ✨

**Choose and control**
* Export a clean copy of your project to a folder or ZIP archive
* File tree with checkboxes, search, and name filters
* Two Git-aware modes: follow `.gitignore`, or show only tracked files
* Smart Ignore — understands each project's structure, safe for monorepos

**Preview and export**
* Live preview (tree / content / both) before you copy or export
* Export as ASCII, Markdown, JSON, or XML
* Save to file or clipboard — tree only, content only, or both

**Workflow**
* GUI, TUI, and CLI — the same engine, three ways to work
* Git tools built in: clone by URL, switch branches, update cached copies
* Local profiles remember your settings per project
* Live counters for lines, characters, and estimated tokens
* Progress bar with safe cancellation

**Details that matter**
* Light, dark, and system themes, with transparency and blur where supported
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
| Dedicated ASCII-tree-only export | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ |
| Export a clean project copy as folder/ZIP | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ |
| GUI-managed Git workflow (clone, branch switch, cache updates) | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ |

---

## Project Copy Export 📦

Use **File → Export Project → To Folder…** or **To ZIP Archive…** to create a separate copy of your current selection.

Project copies respect your chosen root folders, file types, ignore rules, and checked items. If nothing is checked, the whole current tree is exported. Directory structure, binary files, and included empty folders are preserved.

The source project is never modified, and the result can't be written inside it. The same workflow is available through `devprojex export project`.

---

## Command Line ⚙️

DevProjex isn't only a desktop context builder. The same app runs from the terminal too, for repeatable, script-friendly project analysis and AI-context export.

```bash
devprojex
devprojex open . --preview
devprojex analyze . --format json
devprojex export context . --format markdown -o ../devprojex-context.md
devprojex export project . --as folder -o ../devprojex-submission
devprojex export project . --as zip -o ../devprojex-submission.zip
devprojex analyze . --git-mode tracked --exclude smart-ignore
```

### What the CLI adds

* Scriptable exports for CI pipelines — JSON reports, stdout output, deterministic files
* A keyboard-first Terminal Workspace for interactive work, no desktop app needed
* A searchable Action Palette to find any workflow fast
* The same Git filtering, exclusions, and profiles as the desktop app

See [Docs/CommandLine.md](Docs/CommandLine.md) for the full command reference, and [Docs/TerminalWorkspace.md](Docs/TerminalWorkspace.md) for the interactive terminal interface.

---

## Safety boundaries 🛡️

DevProjex keeps your source projects read-only, with clear limits on what it does:

* Does not edit, rename, move, or delete files in the opened source project
* Does not run project code, and does not commit, merge, push, or switch branches in a user-owned repository
* Does not include binary file contents in text or AI-context output
* Writes generated files and project copies only to destinations you choose, outside the source project

---

## How Smart Ignore Works 🧠

Smart Ignore is a local, deterministic filter — not an AI model, and not one big blacklist for the whole folder. It knows where each project starts and ends, and applies the right rules only inside that project.

**It knows project boundaries.** DevProjex finds project markers like `.csproj`, `package.json`, `pyproject.toml`, `go.mod`, or `Cargo.toml`. Rules for that stack (`node_modules`, `bin`/`obj`, virtual environments, build caches) apply only inside the project that owns them. A .NET service won't hide `bin` in an unrelated folder next to it, and a frontend app won't hide `node_modules` somewhere it doesn't belong.

**It checks before it hides anything.** Folder names like `build`, `dist`, or `vendor` can mean generated files — or real source code. Smart Ignore looks for real signs first: package files, compiler output, known build layouts. If there's no clear sign, the folder stays visible.

**It keeps monorepos separate.** Each nested project — frontend, backend, tools, docs — is filtered on its own, based on its own markers. Nothing crosses over between unrelated parts of your workspace.

**Two Git modes, your choice:**

| Mode | What it shows |
|---|---|
| `.gitignore` mode | Tracked files, plus untracked files not excluded by `.gitignore` (with nested rules and negations) |
| Tracked-files-only | Only files currently recorded in the Git index |

**You stay in control.** Smart Ignore, the Git mode, and basic filters (hidden files, dot-files, empty folders) all work together and can be turned on or off one by one. "Ignored" means excluded from the current view, copy, or export — it never deletes anything from your project.

📖 Full technical details — signature matching, worktree handling, edge cases — are in [`Docs/SmartIgnore.md`](Docs/SmartIgnore.md).

---

## Documentation 📚

* [Smart Ignore — full technical details](Docs/SmartIgnore.md)
* [Command Line reference](Docs/CommandLine.md)
* [Terminal Workspace guide](Docs/TerminalWorkspace.md)
* [Contributing Guide](CONTRIBUTING.md)
* [Code of Conduct](CODE_OF_CONDUCT.md)

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

---

## Contributing 🤝

Issues and pull requests are welcome.

Good places to start:

* UX improvements
* Performance tuning
* Tests
* Localization
* Documentation & screenshots

Not sure where to start? Check issues labeled [`good first issue`](https://github.com/Avazbek22/DevProjex/labels/good%20first%20issue).

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

DevProjex is licensed under the **GNU General Public License v3.0 (GPL-3.0)**. This keeps the project — and any tool built on top of it — open source.

* Copyright (c) 2025–present Avazbek Olimov.

See [LICENSE](LICENSE) for details.
