# DevProjex 📁🌳

![Downloads](https://img.shields.io/github/downloads/Avazbek22/DevProjex/total)
![Build](https://img.shields.io/github/actions/workflow/status/Avazbek22/DevProjex/dotnet.yml)
![License](https://img.shields.io/github/license/Avazbek22/DevProjex)
![.NET](https://img.shields.io/badge/.NET-10-purple)
![Platform](https://img.shields.io/badge/platform-Windows%20%7C%20Linux%20%7C%20macOS-green)
![WinGet](https://img.shields.io/badge/winget-available-blue)
![Repo size](https://img.shields.io/github/repo-size/Avazbek22/DevProjex)

**The fastest way to turn a real codebase into clean AI-ready context**

DevProjex is a cross-platform desktop app for turning real folders and codebases into **clean, controlled context for AI chats, reviews, and documentation**.

Select only what matters, preview the result, and copy or export it as **tree, content, or both** in ASCII or JSON.

It’s built for real projects where terminal output is noisy, IDE integrations are limited, and you still need fast, controlled context for an AI chat or a human reviewer.

DevProjex is not an autonomous coding agent. It gives you a manual, fully controlled way to prepare project context when agents, IDE plugins, or remote indexing cannot be used.

> 🔒 Read-only & without telemetry by design — DevProjex never modifies your files.

---

## Download 🚀

**Download from Microsoft Store:**
👉 [Download from Microsoft Store](https://apps.microsoft.com/detail/9ndq3nq5m354)

**Latest GitHub release:**
👉 [https://github.com/Avazbek22/DevProjex/releases/latest](https://github.com/Avazbek22/DevProjex/releases/latest)

**Install via WinGet (Windows):** `winget install OlimoffDev.DevProjex`

---

## App Screenshots 🖼️

> <img width="1723" height="1183" src="https://github.com/user-attachments/assets/a6752e4d-2616-42d4-85fd-cd541506f0cf" />
> <img width="1696" height="1188" src="https://github.com/user-attachments/assets/f09737e7-56ac-43d5-8fa7-95e3a36f76ab" />
> <img width="1692" height="1175" src="https://github.com/user-attachments/assets/99e7de19-8a23-4171-90ec-6f0441130e98" />
> <img width="1715" height="1171" src="https://github.com/user-attachments/assets/6812e8c5-bcaa-45d9-acfa-8a21d3084a03" />

---

## Feature overview ✨

* **Clean, controlled project context** for AI chats, reviews, and documentation
* **TreeView with checkbox selection**
* **Multiple copy/export modes** (tree / content / combined)
* **Preview mode** (tree / content / combined) before copy/export
* **ASCII/JSON tree format toggle** for tree-based operations
* **Per-project local parameter profiles** (saved per local project path)
* **Export to file** from menu (tree / content / tree + content)
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
* **Localization** (8 languages)
* **Responsive async scanning** (UI stays smooth on big folders)
* **Command-line startup automation** with optional JSON analysis reports

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

DevProjex can also be launched from a terminal to open a folder, preselect filters, or generate a JSON analysis report for automation.

```bash
devprojex --path "/path/to/project" --no-ui --report
```

See [Docs/CommandLine.md](Docs/CommandLine.md) for the full CLI contract, supported options, exit codes, and report behavior.

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

  * tree (`.txt` in ASCII mode, `.json`/`.txt` in JSON mode)
  * content (`.txt`)
  * tree + content (`.txt`, with selected tree format)
* Shows preview output before copy/export
* Shows live output metrics and operation progress in status bar
* Restores previously applied parameters for each local project folder
* Supports smart ignore rules (VCS, IDEs, build outputs)
* Works well on large, layered projects

### ❌ Does not

* Edit, rename, move, or delete files
* Run code or modify your repositories (no commits/merges)
* Export binary file contents

---

## Tech stack 🧩

* **.NET 10**
* **Avalonia UI** (cross-platform)
* Cleanly separated architecture (Core / Services / UI)
* JSON-based resources (localization, icon mappings, presets)
* 6000+ automated tests (unit + integration)

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
