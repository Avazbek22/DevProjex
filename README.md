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

Select only what matters, preview the result, copy or export it as **tree, content, or both** in ASCII, MD, JSON, or XML, or create a separate project copy as a folder or ZIP archive.

It’s built for real projects where terminal output is noisy, IDE integrations are limited, and you still need fast, controlled context for an AI chat or a human reviewer.

DevProjex is not an autonomous coding agent. It gives you a manual, fully controlled way to prepare project context when agents, IDE plugins, or remote indexing cannot be used.

> 🔒 Source-project read-only & without telemetry by design — opening, analyzing, or exporting from a project never modifies that source tree. Explicit file, folder, ZIP, and portable-profile destinations are accepted only outside it; application-owned settings, local profiles, clones, caches, and runtime state are stored outside the source tree.

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
* **Export project copies** to a separate folder or ZIP archive, preserving the effective tree, binary files, and included empty folders
* **Search & name filtering** for large projects
* **Two Git-aware filtering modes**: hierarchical `.gitignore` evaluation or an index-backed tracked-files-only view across nested repositories and worktrees
* **Scope-aware Smart Ignore** for mixed workspaces and monorepos
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
* **Terminal automation mode**: generate AI-ready context, JSON reports, CI-friendly diagnostics, tree/content exports, and physical project copies from the same desktop executable

---

## How Smart Ignore Works 🧠

**Smart Ignore is DevProjex’s deterministic, local, scope-aware filtering algorithm.** It is not an AI model and it does not treat an opened folder as one global blacklist. Instead, DevProjex combines project ownership and filesystem evidence in its own **Scope + Evidence** pipeline.

### 1. Detect the project scope

DevProjex recognizes project boundaries from markers such as `.csproj`, `package.json`, `pyproject.toml`, `pom.xml`, `go.mod`, `Cargo.toml`, `composer.json`, and `Gemfile`.

Stack-specific rules are applied only inside the scope that owns those markers:

* .NET: `bin`, `obj`
* JavaScript/frontend: `node_modules`, framework caches, build and coverage output
* Python: virtual environments, bytecode and tool caches
* JVM: Gradle/Maven caches and build output
* Go, Rust, PHP, and Ruby: their corresponding dependency and generated-output folders

### 2. Require evidence for ambiguous folders

A directory name alone is not always enough. Names such as `build`, `dist`, `vendor`, `packages`, `cache`, `pkg`, or `Library` can contain either generated output or real source code.

Smart Ignore first performs a cheap candidate-name check, then looks for strong local signatures such as package metadata, compiler output, cache tags, generated manifests, or known artifact layouts. If the evidence is missing, the folder stays visible. This conservative two-stage check reduces false positives without turning every project load into an expensive deep content scan.

The signature layer also recognizes common generated layouts from CMake, Dart/Flutter, Swift, Unity, Unreal Engine, Terraform, Serverless, Zig, and package-manager caches.

### 3. Keep mixed monorepos isolated

Each nested project keeps its own rules. A .NET service does not make `bin` disappear from an unrelated sibling, and a frontend app does not apply `node_modules` rules to an ordinary folder elsewhere in the workspace.

```text
repo/
├── apps/web/package.json       -> frontend artifacts are filtered in this scope
├── services/api/api.csproj     -> .NET artifacts are filtered in this scope
├── tools/data/pyproject.toml   -> Python artifacts are filtered in this scope
└── docs/build/                 -> remains visible without artifact evidence
```

Deep projects are not limited to the repository root. When an artifact candidate is encountered during tree traversal, DevProjex validates it against the applicable ancestor project markers. This keeps deeply nested and mixed-language workspaces predictable.

### 4. Choose the Git view you need

`.gitignore` answers “which paths should Git ignore?” It does not mean “show only files owned by the repository.” DevProjex supports both workflows as two mutually exclusive modes:

| Mode | Result |
|---|---|
| **Use `.gitignore`** | Keeps tracked files and untracked files that are not excluded by reachable `.gitignore` rules. Each path uses the index of its owning repository/worktree scope; a scope without a readable index safely falls back to pattern-only evaluation. |
| **Tracked Git files only** | Uses the installed Git CLI to start from existing working-tree files recorded in each readable Git index. Modified tracked files and staged additions remain included; untracked files are excluded. |

In `.gitignore` mode, every reachable `.gitignore` is evaluated in its own directory scope, including the ancestor rule chain when a repository subfolder is opened, nested rules, and negations. Pattern case matching follows the effective repository `core.ignoreCase` value; macOS Unicode comparison also follows Git's `core.precomposeUnicode` behavior. This keeps the tree, extension list, preview, and exports on the same path semantics as Git.

Only regular working-tree `.gitignore` files are rule sources. DevProjex does not apply `.git/info/exclude`, global Git excludes, or a `.gitignore` symbolic link; this matches Git's own control-file behavior and prevents a link target outside the project from changing the result. If a regular `.gitignore` cannot be read, its directory scope is excluded fail-closed and the scan reports partial access instead of silently including files without applying all ignore rules.

DevProjex resolves every reachable nested repository and worktree independently, at any reachable nesting level. A child repository never inherits tracked state from its parent or sibling. Tracked-files mode is fail-closed and never silently falls back to `.gitignore`: direct commands, including `devprojex open`, return a policy failure before output or Desktop handoff if the Git CLI cannot load any applicable index; an already-open Desktop workspace keeps the explicit selection visible so it can be turned off; TUI startup does not open an unavailable tracked workspace, while an unsuccessful interactive TUI mode change keeps the last usable Git mode. A readable empty index is valid; unreadable nested indexes are excluded with a warning when another applicable index was loaded.

This is a view of the current working tree, not a historical snapshot of `HEAD` or a promise that the files match a remote Git host. The current bytes of modified tracked files are used.

### 5. Compose filters predictably

The selected Git mode runs first. Smart Ignore processes the remaining items, followed by the explicit dot-file, hidden-item, empty-item, and extensionless-file rules. Root-folder, file-type, and checkbox selections can narrow the same effective tree further.

The built-in `standard` profile selects `gitignore` mode and all eight exclusion groups: `smart-ignore`, `hidden-folders`, `hidden-files`, `dot-folders`, `dot-files`, `empty-folders`, `empty-files`, and `extensionless-files`. Explicit CLI options replace the corresponding profile field for that invocation.

Inside a Git repository, the two Git modes remain visible as a stable toggle pair. Smart Ignore and the evidence-based basic options appear only when they can affect the current tree. Smart Ignore may therefore stay hidden while the selected Git mode already excludes all matching artifacts, then appear after that mode is changed.

### Control stays with you

* Switch between hierarchical `.gitignore` evaluation and the tracked-files-only Git view.
* Combine the selected Git mode with Smart Ignore and the basic ignore switches.
* Put project-specific patterns in `.gitignore`; the curated Smart Ignore rules are intentionally not an arbitrary user-editable pattern list.
* Control dot-prefixed and hidden items through their separate switches instead of having Smart Ignore hide them implicitly.
* Narrow the result by top-level folders and file types.
* Narrow the final export through tree checkboxes and verify it in preview.
* Applied choices are remembered in local project profiles.

Ignored means excluded from the current tree, copy, and export result — never deleted from the source project. The implementation is open source: see the [stack rules](Infrastructure/SmartIgnore) and the [evidence-based signature matcher](Kernel/Models/SmartArtifactIgnoreMatcher.cs).

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
* Exports project copies:

  * to a separate folder
  * to a ZIP archive
  * with the effective directory structure, binary files, and included empty folders preserved
* Shows preview output before copy/export
* Shows live output metrics and operation progress in status bar
* Restores previously applied parameters for each local project folder
* Supports smart ignore rules (VCS, IDEs, build outputs)
* Works well on large, layered projects

### ❌ Does not

* Edit, rename, move, or delete files
* Run code or modify your repositories (no commits/merges)
* Include binary file contents in text/context exports

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
