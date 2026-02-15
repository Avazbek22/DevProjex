# DevProjex 📁🌳

**Visual project context builder for humans and AI**

DevProjex is a cross-platform desktop application for **quickly exploring folder/project structures**, selecting what matters, and preparing **clean, controlled context** (tree, file contents, or both) for clipboard and file export.

It’s designed for real projects where CLI output is noisy, IDE tools are unavailable or limited, and you need **clarity, speed, and control**.

> 🔒 Read-only by design — DevProjex never modifies your files.

---

## Download 🚀

**Download from Microsoft Store:**
👉 [Download from Microsoft Store](https://apps.microsoft.com/detail/9ndq3nq5m354?hl=en-EN&gl=EN)

**Latest GitHub release:**
👉 [https://github.com/Avazbek22/DevProjex/releases/latest](https://github.com/Avazbek22/DevProjex/releases/latest)

Older versions are available on the Releases page.

---

## App Screenshots 🖼️

> <img width="1981" height="1267" alt="image" src="https://github.com/user-attachments/assets/41daf582-aa7f-4c79-8c3b-ea750d6132ac" />

> <img width="2000" height="1276" alt="image" src="https://github.com/user-attachments/assets/49699c5f-f39d-4924-865e-317f83b506a2" />

---

## Feature overview ✨

* **TreeView with checkbox selection**
* **Multiple copy/export modes** (tree / content / combined)
* **Preview mode** (tree / content / combined) before copy/export
* **ASCII/JSON tree format toggle** for tree-based operations
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
* 2300+ automated tests (unit + integration)

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

DevProjex is **source-available** under the **Business Source License (BSL) 1.1**.

* Free for non-commercial use
* Commercial use restricted until **2030-01-01**
* Automatically converts to **MIT** on that date

See `LICENSE` for details.

---

## Keywords 🔎

project tree viewer, folder structure, context builder, AI prompt preparation, clipboard export, avalonia ui, .net 10, cross-platform desktop app, repository visualization, developer tools
