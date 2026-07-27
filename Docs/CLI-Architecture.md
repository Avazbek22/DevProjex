# CLI Architecture

## Product Boundary

One published `DevProjex.exe` contains three presentation surfaces:

- Avalonia Desktop;
- Terminal.Gui Terminal Workspace;
- System.CommandLine direct CLI with Spectre.Console human rendering.

`DevProjex.Terminal` is a class library referenced by the Avalonia executable.
It references Application, Infrastructure, Kernel, and Assets, but never
`MainWindow`, `MainWindowViewModel`, Avalonia controls, Skia, or desktop
presentation services.

## Early Routing

The executable routes before Avalonia initialization:

1. direct commands run the Terminal command tree;
2. the generated terminal wrapper sets `DEVPROJEX_TERMINAL_HOST=1`;
3. no arguments in an interactive terminal start Terminal Workspace;
4. no arguments with redirected streams print plain help;
5. a desktop launch without a usable console starts Avalonia.

On Windows, parent-console attachment occurs before terminal rendering. Desktop
child processes remove the terminal marker. A normal GUI launch never creates a
visible console window. `open` hands a size-bounded request file to the desktop
through a private internal invocation token that is consumed before public
command parsing. Windows ShellExecute and a positional Unix `exec` handoff keep
the long-running desktop from retaining redirected CLI streams.

## Command Tree

`System.CommandLine` owns parsing, validation, hierarchy, help metadata, and
completion. There is no manual flat argument loop or parallel public parser.
Human descriptions come from the shared 11-locale localization catalog.

`Spectre.Console` is used only for direct human output. `Terminal.Gui` owns the
terminal only during a TUI session; the two libraries never run concurrent live
renderers.

## Shared Planning

`ProjectSelectionSpec` describes user intent:

- roots;
- extensions;
- selected relative paths;
- one Git filtering mode;
- ordinary Exclusions;
- profile source.

`ProjectSelectionResolver` applies standard/local/portable profile precedence and
explicit command overrides.

`ProjectContextPlanner` orchestrates the existing project analysis, inventory,
ignore rules, Git tracked-index cache, tree construction, selection projection,
and metrics. It produces one `ProjectContextPlan` consumed by analyze, context
documents, project export, and Terminal Workspace.

The planner does not introduce a second scanner or ignore engine.

## ProjectContextPlan

The plan contains:

- canonical source root;
- resolved selection and profile source;
- available and selected roots/extensions;
- effective and projected trees;
- selected full paths;
- included files and folders;
- analysis and output metrics;
- Git readiness and partial-index evidence;
- diagnostics;
- deterministic fingerprint.

Future content representations and policy transformations can consume the plan
without replacing filesystem discovery. No nonfunctional future command is
published.

## Output and Failures

Command handlers return typed exit outcomes. Dedicated renderers keep stdout
payloads separate from stderr operations. Stable `DPX-*` codes are mapped to
localized user text; raw platform exception messages are not normal output.

File output uses adjacent staging and atomic moves where replacement is allowed.
Project-copy destination checks and cancellation cleanup remain in the shared
application service.

## Desktop Control

Desktop IPC uses versioned semantic requests over Named Pipes on Windows and Unix
domain sockets on Linux/macOS. The endpoint and registry are per-user. UI work is
dispatched to the Avalonia UI thread and acknowledged only after application.

The public protocol cannot invoke arbitrary methods or set arbitrary view-model
properties.

## Dependency Direction

```text
Kernel
  ↑
Application
  ↑
Infrastructure
  ↑
DevProjex.Terminal class library
  ↑
DevProjex.Avalonia executable
```

Terminal presentation packages do not leak into Kernel or Application. Desktop
and terminal use the same composition services without sharing UI types.
