# CLI Architecture

## Product Boundary

One published DevProjex application executable (`DevProjex.exe` on Windows,
`DevProjex` on Linux and macOS) contains three presentation surfaces:

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

One bounded parser-boundary integrity check retains the distinction that
System.CommandLine 2.0.10 loses for an empty inline assignment: `--name=`.
It inspects only exact tokens before `--`, matches them to value-taking options
on the already resolved command model, and returns a missing-value error. It
does not resolve commands, parse option values, or reinterpret positional data.

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
- user-facing source identity for local folders or cached Git repositories;
- resolved selection and profile source;
- available and selected roots/extensions;
- effective and projected trees;
- selected full paths;
- included files and folders;
- analysis and output metrics;
- Git readiness and partial-index evidence;
- diagnostics;
- deterministic fingerprint.

Repository identity is deliberately separate from the physical cache path. A
cached clone retains its internal unique directory while human surfaces use the
clean repository name and safe original URL. Machine documents may expose this
identity as additive source metadata without changing filesystem discovery.

Direct CLI commands and Terminal Workspace consume this plan. Desktop retains
its established selection-refresh and text-output orchestration; sharing the
product workflow does not imply that all three presentation surfaces currently
use one interchangeable implementation pipeline. No nonfunctional placeholder
command is published.

## Completion Transport

Generated completion scripts query a hidden `dev complete` endpoint backed by
the same command tree. Bash, Zsh, and Fish pass the current command line after
`--`. Windows PowerShell 5.1 cannot preserve an unfinished quoted command line
through its native argument binder, so the generated PowerShell script sends
the unfinished command line as strict UTF-8 Base64 with `--base64` and the
working directory with `--working-directory-base64`. The endpoint decodes both
values before requesting path-aware completions and returns each candidate as
UTF-8 Base64. The script decodes those ASCII-safe records before constructing
PowerShell completion results, avoiding the host console code page at both
native-process boundaries. These transport flags are hidden, produce no
user-facing contract, and never appear in normal help or completion.

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

## Terminal Test Boundary

Published-binary interaction tests use two test-only components:

- Hex1b `0.165.0` (MIT) provides the native PTY/ConPTY transport and preserves
  signal exit codes on Windows, Linux, and macOS;
- XTerm.NET `1.0.15` maintains the deterministic visible-cell and style model
  used by assertions and visual artifacts.

Neither package is referenced by the Terminal or Avalonia product projects.
Release Validation drives the published single-file executable through this
test boundary; the automation dependencies are never bundled into the DevProjex
application executable.

Deterministic progress snapshots run through the separate
`DevProjex.Tests.Terminal.ProgressHost` test project. It injects an internal
operation observer from test composition; the published application always uses
the no-op observer and contains no environment-driven checkpoint or pause logic.
