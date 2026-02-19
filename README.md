# DevProjex 📁🌳

**Visual project context builder for humans and AI**

DevProjex is a cross-platform desktop application for **quickly exploring folder/project structures**, selecting what matters, and preparing **clean, controlled context in ASCII or JSON** (tree, file contents, or both) for clipboard and file export.

It’s designed for real projects where CLI output is noisy, IDE tools are unavailable or limited, and you need **clarity, speed, and control**.

> 🔒 Read-only by design — DevProjex never modifies your files.

---

## Download 🚀

**Download from Microsoft Store:**
👉 [Download from Microsoft Store](https://apps.microsoft.com/detail/9ndq3nq5m354?hl=en-EN&gl=EN)

**Latest GitHub release:**
👉 [https://github.com/Avazbek22/DevProjex/releases/latest](https://github.com/Avazbek22/DevProjex/releases/latest)

Older versions are available on the Releases page.

**Install via WinGet (Windows):**
```powershell
winget install OlimoffDev.DevProjex
```

---

## App Screenshots 🖼️

> <img width="1723" height="1183" src="https://github.com/user-attachments/assets/a6752e4d-2616-42d4-85fd-cd541506f0cf" />
> <img width="1696" height="1188" src="https://github.com/user-attachments/assets/f09737e7-56ac-43d5-8fa7-95e3a36f76ab" />
> <img width="1692" height="1175" src="https://github.com/user-attachments/assets/99e7de19-8a23-4171-90ec-6f0441130e98" />
> <img width="1715" height="1171" src="https://github.com/user-attachments/assets/6812e8c5-bcaa-45d9-acfa-8a21d3084a03" />

---

## Feature overview ✨

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

---

## Typical use cases 🎯

* Share project structure in code reviews or chats
* Prepare **clean input for AI assistants** (ChatGPT, Copilot, etc.)
* Extract only relevant modules from large codebases
* Teach or explain project architecture
* Inspect large folders without CLI scripts

DevProjex is not tied to a specific language or IDE.

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
* 3050+ automated tests (unit + integration)

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

## License 📄

DevProjex is licensed under the **GNU General Public License v3.0 (GPL-3.0)**.

See `LICENSE` for details.

---

## Keywords 🔎

project tree viewer, folder structure, context builder, AI prompt preparation, llm, clipboard export, avalonia ui, .net 10, cross-platform desktop app, repository visualization, developer tools
