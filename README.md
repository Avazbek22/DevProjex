<h1 align="center">DevProjex 📁🌳</h1>

<h2 align="center">🏆 Officially Selected by the Avalonia UI Team for the <a href="https://avaloniaui.net/showcase">Avalonia App Showcase</a></h2>

<p align="center">
  <a href="https://github.com/Avazbek22/DevProjex/releases"><img alt="Downloads" src="https://img.shields.io/github/downloads/Avazbek22/DevProjex/total"></a>
  <a href="https://github.com/Avazbek22/DevProjex/actions"><img alt="Build" src="https://img.shields.io/github/actions/workflow/status/Avazbek22/DevProjex/dotnet.yml"></a>
  <a href="https://github.com/Avazbek22/DevProjex/blob/master/LICENSE"><img alt="License" src="https://img.shields.io/github/license/Avazbek22/DevProjex"></a>
  <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10-purple">
  <img alt="Platforms" src="https://img.shields.io/badge/platform-Windows%20%7C%20Linux%20%7C%20macOS-green">
  <img alt="Repository size" src="https://img.shields.io/github/repo-size/Avazbek22/DevProjex">
 <a href="https://avaloniaui.net/showcase"><img alt="Avalonia App Showcase" src="https://img.shields.io/badge/Avalonia%20Team-Showcase%20Selection-7B61FF?logo=avaloniaui&logoColor=white"></a>
</p>

<p align="center">
  <a href="https://boosty.to/avazbek22/donate">
    <img
      alt="Support DevProjex on Boosty"
      src="https://img.shields.io/badge/Support_DevProjex-on_Boosty-F15F2C?style=for-the-badge&logo=boosty&logoColor=white">
  </a>
</p>

<p align="center">
  <strong>The fastest way to turn a real codebase into clean, AI-ready context.</strong>
</p>

DevProjex is a cross-platform desktop app for turning real folders and codebases into **clean, controlled context for AI chats, reviews, and documentation**.

Select only what matters, preview the result, and copy or export it as **tree, content, or both** in ASCII, MD, JSON, or XML.

It’s built for real projects where terminal output is noisy, IDE integrations are limited, and you still need fast, controlled context for an AI chat or a human reviewer.

DevProjex is not an autonomous coding agent. It gives you a manual, fully controlled way to prepare project context when agents, IDE plugins, or remote indexing cannot be used.

> 🔒 Read-only & without telemetry by design — DevProjex never modifies the opened source project; project copies are written only to the destination you choose.

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

## App Demo 🖼️

<img src="Docs/Media/readme-demo/devprojex-demo-04-readme.gif" alt="DevProjex desktop app demo" width="100%" />

---

## Feature overview ✨

* **Clean, controlled project context** for AI chats, reviews, and documentation
* **TreeView with checkbox selection**
* **Multiple copy/export modes** (tree / content / combined)
* **Preview mode** (tree / content / combined) before copy/export
* **ASCII, MD, JSON, and XML tree formats** for AI prompts, documentation, and parsers
* **Per-project local parameter profiles** (saved per local project path)
* **Export to file** from menu (tree / content / tree + content)
* **Export a project copy** as a separate folder or ZIP using the current tree, exclusions, and checkbox selection
* **Search & name filtering** for large projects
* **Smart Ignore + .gitignore support** (scope-aware behavior for mixed workspaces)
* **Extensionless files handling** via dedicated ignore option
* **Git integration** (clone by URL, switch branches, get updates in cached copies)
* **Status bar with live metrics** (tree/content lines, chars, ~tokens)
* **Progress bar + operation cancellation** with safe fallback behavior
* **Modern appearance system**

  * Light / Dark
  * Transparency & blur where supported
  * Presets stored locally
  * Island-based layout and smooth UI animations
* **Animated toasts** for user feedback
* **Localization** (11 languages)
* **Responsive async scanning** (UI stays smooth on big folders)
* **Terminal automation mode**: generate AI-ready context, JSON reports, CI-friendly diagnostics, and tree/content exports from the same desktop executable

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

## Command Line ⚙️

DevProjex is not only a desktop context builder. The same app can run from the terminal for repeatable, script-friendly project analysis and AI-context export.

```bash
devprojex "/path/to/project" --preview-mode tree-content --tree-format md
devprojex --last --preview
devprojex "/path/to/project" --export tree-content -o ./context.txt --roots src --ext cs
devprojex --path "/path/to/project" --no-ui --report -
devprojex --path "/path/to/project" --no-ui --report ./devprojex-report.json --strict
devprojex --benchmark-ui "/path/to/project"
devprojex --session-metrics "/path/to/project" --preview --tree-format md
```

Use it to:

* open the desktop app directly in preview/filter/search states;
* generate clean AI-ready context without opening the UI;
* export selected tree/content payloads directly to files or stdout;
* produce machine-readable JSON analysis reports;
* benchmark the headless report pipeline or a repeatable desktop UI workflow;
* record interactive UI session metrics for practical CPU/RAM diagnostics;
* fail CI when selected roots/extensions produce diagnostics;
* reuse the same ignore logic as the desktop app.

See [Docs/CommandLine.md](Docs/CommandLine.md) for the full CLI contract, supported options, export modes, exit codes, and report behavior.

---

## What DevProjex does (short & honest)

### ✅ Does

* Builds a visual tree of any folder or project
* Lets you select files/folders via checkboxes
* Supports drag & drop opening (folder or file path)
* Copies:

  * tree (selection-aware, falls back to full)
  * content (selection-aware, falls back to all files)
  * tree + content (selection-aware, falls back to full)
* Exports:

  * tree (`.txt`, `.md`, `.json`, `.xml` depending on the selected tree format)
  * content (`.txt`)
  * tree + content (`.txt`, with selected tree format)
  * current effective project tree as a separate folder or ZIP, including binary files and empty directories; the source project is never modified
* Shows preview output before copy/export
* Shows live output metrics and operation progress in status bar
* Restores previously applied parameters for each local project folder
* Supports smart ignore rules (VCS, IDEs, build outputs)
* Works well on large, layered projects

### ❌ Does not

* Edit, rename, move, or delete files
* Run code or modify your repositories (no commits/merges)
* Include binary file contents in text/clipboard exports (binary files are preserved by project-copy export)

---

## Tech stack 🧩

* **.NET 10**
* **Avalonia UI** (cross-platform)
* Cleanly separated architecture (Core / Services / UI)
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

---

## License (GPL-3.0) 📄

DevProjex is licensed under the **GNU General Public License v3.0 (GPL-3.0)**.
* Copyright (c) 2025-2026 Avazbek Olimov.

See `LICENSE` for details.

---

## Keywords 🔎

project tree viewer, folder structure viewer, directory tree generator, project structure visualizer, repository tree viewer, source code tree generator, repository visualization tool, codebase explorer, codebase visualization, directory structure export, AI prompt preparation, LLM context builder, codebase context extraction, AI developer tools, repository inspection tool, developer productivity tools, Avalonia UI desktop app, .NET 10 application, cross-platform developer tool
