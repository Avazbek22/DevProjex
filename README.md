<h1 align="center">DevProjex 📁🌳</h1>

<h2 align="center">🏆 Officially Selected by the Avalonia UI Team for the <a href="https://avaloniaui.net/showcase">App Showcase</a></h2>

<p align="center">
  <a href="https://github.com/Avazbek22/DevProjex/releases"><img alt="Downloads" src="https://img.shields.io/github/downloads/Avazbek22/DevProjex/total"></a>
  <a href="https://github.com/Avazbek22/DevProjex/actions"><img alt="Build" src="https://img.shields.io/github/actions/workflow/status/Avazbek22/DevProjex/dotnet.yml"></a>
  <a href="https://github.com/Avazbek22/DevProjex/blob/master/LICENSE"><img alt="License" src="https://img.shields.io/github/license/Avazbek22/DevProjex"></a>
  <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10-purple">
  <img alt="Platforms" src="https://img.shields.io/badge/platform-Windows%20%7C%20Linux%20%7C%20macOS-green">
  <img alt="WinGet" src="https://img.shields.io/badge/winget-available-blue">
  <img alt="Repository size" src="https://img.shields.io/github/repo-size/Avazbek22/DevProjex">
</p>

<p align="center">
  <strong>The fastest way to turn a real codebase into clean, AI-ready context.</strong>
</p>

DevProjex is a cross-platform app for turning folders and codebases into **clean, controlled context for AI chats, reviews, and documentation** — with GUI, TUI, and CLI workflows.

Select only what matters, preview the result, copy or export it as **tree, content, or both** in ASCII, MD, JSON, or XML, or create a separate project copy as a folder or ZIP archive.

It’s built for real projects where terminal output is noisy, IDE integrations are limited, and you still need fast, controlled context for an AI chat or a human reviewer.

DevProjex is not an autonomous coding agent. It gives you a manual, fully controlled way to prepare project context when agents, IDE plugins, or remote indexing cannot be used.

> 🔒 Source-project read-only & without telemetry by design — opening, analyzing, or exporting from a project never modifies that source tree. Explicit file, folder, ZIP, and portable-profile destinations are accepted only outside it; application-owned settings, local profiles, clones, caches, and runtime state are stored outside the source tree.

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

## Quick Start ⚡

1. Open or drop a project folder.
2. Select the folders, files, filters, ignore rules, and output mode you need.
3. Preview the result, then copy, export, or run the same workflow from the terminal.



---

## Feature overview ✨

* **Clean, controlled project context** for AI chats, reviews, and documentation
* **TreeView with checkbox selection**
* **Multiple copy/export modes** (tree / content / combined)
* **Preview mode** (tree / content / combined) before copy/export
* **ASCII, MD, JSON, and XML tree formats** for AI prompts, documentation, and parsers
* **Per-project local parameter profiles** (saved per local project path)
* **Export to file** from menu (tree / content / tree + content)
* **Export project copies** to a separate folder or ZIP archive, preserving the effective tree, binary files, and included empty folders
* **Search & name filtering** for large projects
* **Two Git-aware filtering modes**: hierarchical `.gitignore` evaluation or an index-backed tracked-files-only view across nested repositories and worktrees
* **Scope-aware Smart Ignore** for mixed workspaces and monorepos
* **Extensionless files handling** via dedicated ignore option
* **Git integration** (clone by URL, switch branches, get updates in cached copies)
* **Status bar with live metrics** (tree/content lines, chars, ~tokens)
* **Progress bar + operation cancellation** with safe fallback behavior
* **Modern appearance system**

  * System / Light / Dark
  * Transparency / blur / Mica where supported
  * Presets stored locally
  * Island-based layout and smooth UI animations
* **Animated toasts** for user feedback
* **Localization** (11 languages)
* **Responsive async scanning** (UI stays smooth on big folders)
* **Unified terminal workflows**: use the keyboard-first TUI for interactive project inspection, or the deterministic CLI for AI-ready context, JSON reports, CI-friendly diagnostics, tree/content exports, and project copies — all from the same application executable

## DevProjex vs the alternatives ⚖️

| Feature | DevProjex | Repomix | gitingest | code2prompt | GPTree GUI | files-to-prompt |
|---|---|---|---|---|---|---|
| Desktop GUI | ✅ | ❌ | ❌ | ❌ | ✅ | ❌ |
| TUI | ✅ | ❌ | ❌ | ✅ | ❌ | ❌ |
| CLI | ✅ | ✅ | ✅ | ✅ | ❌¹ | ✅ |
| Interactive checkbox/tree file selection | ✅ | ❌ | ❌ | ✅ | ✅ | ❌ |
| Live preview before export | ✅ | ❌ | ❌ | ✅ | ✅ | ❌ |
| .gitignore support | ✅ | ✅ | ✅ | ✅ | ✅ | ✅ |
| Tracked-files-only Git mode | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ |
| Scope-aware, evidence-based artifact filtering (monorepo-safe) | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ |
| Dedicated ASCII-tree-only export mode | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ |
| Export a clean project copy as folder/ZIP | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ |
| Multiple structured formats (Markdown/JSON/XML) | ✅ | ✅ | ❌ | ✅ | ❌ | MD/XML only |
| Token / char / line counting | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ |
| Remote repository via URL | ✅ | ✅ | ✅ | ❌ | ❌ | ❌ |
| GUI-managed Git workflow (clone, branch switch, cache updates) | ✅ | ❌ | ❌ | ❌ | ❌ | ❌ |
| Per-project saved configuration/profiles | ✅ | ✅ | ❌ | not confirmed² | ✅ | ❌ |
| Runs fully local by default | ✅ | ✅ CLI³ | ✅ CLI/self-host³ | ✅ | ✅ | ✅ |

¹ GPTree GUI itself has no CLI/TUI; a separate `gptree` CLI package exists with its own interactive selection mode.
² code2prompt supports templating and CLI flags; a dedicated per-project config file wasn't confirmed in public docs at time of writing.
³ Hosted web versions (repomix.com, gitingest.com) process code on their servers; CLI usage is local.

*Every row above is a feature DevProjex supports. Reflects public documentation as of August 2026 — check each project's repo for the latest.*

## How Smart Ignore Works 🧠

Smart Ignore is a local, deterministic filtering engine — not an AI model, and not a single global blacklist. It understands where each project starts and ends, and applies the right rules only where they belong.

**Scope-aware.** DevProjex detects project boundaries from markers like `.csproj`, `package.json`, `pyproject.toml`, `go.mod`, `Cargo.toml`, and similar files. Stack-specific rules (`node_modules`, `bin`/`obj`, virtual envs, build caches) apply only inside the project that owns them — so a `.NET` service won't hide `bin` in an unrelated sibling folder, and a frontend app won't apply `node_modules` rules somewhere else in the workspace.

**Evidence, not guesswork.** Ambiguous folder names like `build`, `dist`, or `vendor` can mean generated output or real source code. Smart Ignore checks for actual signatures — compiler output, package metadata, known artifact layouts — before excluding anything. If there's no evidence, the folder stays visible.

**Monorepo-safe.** Every nested project (frontend, backend, tools, docs) is filtered independently, based on its own markers — no cross-contamination between unrelated parts of a workspace.

**Two Git-aware modes, your choice:**
| Mode | What it shows |
|---|---|
| `.gitignore` mode | Tracked files + untracked files not excluded by `.gitignore` (per-repository, with nested rules and negations) |
| Tracked-files-only | Only files currently recorded in the Git index — nothing untracked |

**You stay in control.** Smart Ignore, Git mode, and basic filters (hidden files, dot-files, empty folders) all combine and can be toggled independently. Ignored means excluded from the current view, copy, or export — never deleted from your project.

📖 Full technical details — signature matching, worktree handling, edge cases — are documented in [`Docs/SmartIgnore.md`](Docs/SmartIgnore.md).

---

## Typical use cases 🎯

* Prepare **clean input for AI assistants** (ChatGPT, Claude, DeepSeek, Qwen, etc.)
* Work in **policy-restricted environments** where AI agents, remote indexing, or IDE plugins are not allowed
* Share project structure in code reviews or chats
* Extract only relevant modules from large codebases
* Teach or explain project architecture
* Inspect large folders without noisy CLI scripts


DevProjex works with any language, repository, or project structure.

---

## Project Copy Export 📦

Use **File → Export Project → To Folder…** or **To ZIP Archive…** to create a separate copy of the current effective tree.

Project copies respect the selected root folders, file types, ignore rules, and checked items. If nothing is checked, the entire current tree is exported. Directory structure, binary files, and included empty folders are preserved.

The source project is not modified, and the result cannot be written inside it. The same workflow is available through `devprojex export project`.

---

## Command Line ⚙️

DevProjex is not only a desktop context builder. The same app can run from the terminal for repeatable, script-friendly project analysis and AI-context export.

```bash
devprojex
devprojex open . --preview
devprojex analyze . --format json
devprojex export context . --format markdown -o ../devprojex-context.md
devprojex export project . --as folder -o ../devprojex-submission
devprojex export project . --as zip -o ../devprojex-submission.zip
devprojex analyze . --git-mode tracked --exclude smart-ignore
```

Use it to:

* work interactively in the Terminal Workspace without starting Avalonia;
* reopen local folders or cached Git repositories from one shared Recent Workspaces history;
* inspect Tree, Content, or Tree + Content in ASCII, JSON, XML, or Markdown through Tree, Preview, and Parameters;
* discover every important workflow through the searchable Action Palette;
* open or control the desktop app through semantic local IPC;
* generate clean AI-ready context without opening the UI;
* export selected context to stdout or deterministic files;
* create exact folder or ZIP project copies;
* produce stable machine-readable JSON analysis;
* share the same Git filtering, Exclusions, profiles, and project engine as Desktop.

See [Docs/CommandLine.md](Docs/CommandLine.md) for commands and examples, and [Docs/TerminalWorkspace.md](Docs/TerminalWorkspace.md) for the interactive terminal interface.

---

## Safety boundaries 🛡️

DevProjex keeps user-owned source projects read-only and makes its operational boundaries explicit:

* Does not edit, rename, move, or delete files in the opened source project
* Does not execute project code or perform commits, merges, pushes, or branch changes in a user-owned repository
* Does not include binary file contents in text or AI-context documents
* Writes generated files and project copies only to explicit destinations outside the source project

---

## Tech stack 🧩

* **.NET 10**
* **Avalonia UI** (cross-platform)
* Clean Architecture layers (Kernel / Application / Infrastructure / Terminal / Avalonia)
* JSON-based resources (localization, icon mappings, presets)
* 10000+ automated tests (unit + integration + UI)

---

## Contributing 🤝

Issues and pull requests are welcome.

Good contribution areas:

* UX improvements
* Performance tuning
* Tests
* Localization
* Documentation & screenshots

See `CONTRIBUTING.md` for details.

<p align="center">
  <a href="https://boosty.to/avazbek22">
    <img src=".github/assets/boosty-support.svg" width="800" alt="Support DevProjex on Boosty">
  </a>
</p>

---

## License (GPL-3.0) 📄

DevProjex is licensed under the **GNU General Public License v3.0 (GPL-3.0)**.
* Copyright (c) 2025–present Avazbek Olimov.

See `LICENSE` for details.
