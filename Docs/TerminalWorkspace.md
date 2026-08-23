# DevProjex Terminal Workspace

DevProjex Terminal is the interactive terminal interface included in the same
portable executable as DevProjex Desktop.

## Start

```shell
devprojex
devprojex tui .
devprojex tui ./app --screen inline
```

The argument-less command starts the workspace only when stdin and stdout are
interactive and `TERM` is not `dumb`. `devprojex tui` fails with a usage error
instead of blocking when the terminal is redirected.

The workspace applies saved project settings when they exist and otherwise uses
the deterministic default settings. The `standard`, `local`, and portable-file
names remain part of the CLI profile contract, not permanent TUI jargon.

## Welcome

The welcome screen offers:

- open the current directory when it is a reasonable project candidate;
- open Recent Workspaces, combining local folders and Git repositories;
- browse for a folder;
- clone through the existing DevProjex Git service;
- open DevProjex Desktop;
- help and exit.

DevProjex does not automatically scan a filesystem root, a broad home directory,
or another unsafe default location.

Opening a project from Welcome uses saved project settings when they exist and
deterministic defaults otherwise. Recent Workspaces, Browse, Clone, and Help are
nested workflows: canceling or closing them returns to Welcome without ending
the terminal session. Opening a project with a portable settings file remains
available as an advanced Action Palette workflow.

Recent Workspaces reads and updates the same per-user history as Desktop. Its
initial folder-and-repository projection performs no Git process, cache scan, or
network request. Selecting an available entry shows a loading state and opens its
workspace without ending the TUI. A successfully opened entry moves to the front
of the existing history. Unavailable entries remain visible so they can be
removed intentionally.

If recent-project storage is temporarily locked, the workflow offers Retry
instead of presenting an empty list. A valid backup is used when the primary
history is corrupt. An invalid local-profile store offers a deterministic
Standard-profile recovery path; unavailable roots or file types in an otherwise
valid local profile open with diagnostics.

Repository rows come from the repository history in that same store. Cache
resolution is deferred until a row is opened. A healthy cached clone opens
without a network request. A missing or damaged cache remains visible and offers
explicit Back, Remove, or Clone Again actions. Removing history never silently
deletes a cache. Equivalent supported repository URLs are normalized to avoid
duplicate rows.

Cloned workspaces use repository identity rather than the physical cache folder:
the heading, tree root, Preview, analysis, and context documents show the clean
repository name and original safe URL. The internal cache path is available only
from explicit source details and diagnostics. Clone progress shows the repository,
safe URL, elapsed time, current Git phase and real Git measurements when Git
provides them; it never manufactures a percentage or prints credentials.

## Navigation Lifecycle

The Terminal Workspace runs under one persistent application root. Dialogs and
secondary screens are overlays over that root; closing an overlay restores the
previous screen, selected row, and keyboard focus.

- Enter opens or confirms the focused action.
- Esc cancels the current operation or returns exactly one level.
- Ctrl+C cancels active work first and otherwise asks before exiting.
- `q` exits only from a root screen.
- Errors remain visible until dismissed and then return to a usable prior state.

Only an explicit Exit action, root-level `q`, or confirmed Ctrl+C ends the TUI.
Opening Desktop reports the handoff result instead of silently terminating the
Terminal Workspace.

## Interface

The Welcome screen uses the terminal background, a compact action list, a
focused detail pane, and contextual keyboard hints instead of a modal selector.

![DevProjex Terminal Welcome](Media/terminal-workspace/welcome.png)

After a project opens, the wide layout keeps Project Tree, Context Preview, and
Parameters visible together. Parameters uses a fixed 38-column panel so excess
width belongs to Preview. A saved-settings row precedes three framed mini-panels:
Content Processing, Exclusions, and File Types. Content Processing contains five
fixed rows; Exclusions and File Types divide the remaining height and scroll
independently. Narrow layouts present the same three mini-panels at the full
available width without losing selection or navigation state. Export commands
remain available from their shortcuts and the Action Palette instead of being
mixed into the settings lists.

![DevProjex Terminal Workspace](Media/terminal-workspace/workspace.png)

## Layout

The workspace adapts to terminal size:

| Width | Layout |
|---:|---|
| 150 columns or wider | Tree, Preview, and Parameters |
| 120-149 columns | Tree and Preview; Parameters opens as a focused pane |
| 80-119 columns | tabbed single-pane workspace |
| 60-79 columns | compact single-pane workspace |
| below 60 columns, or below 20 rows | resize guidance |

If the terminal is too small to operate safely, the workspace shows a compact
size hint rather than corrupting the screen.

## Workflow

The workspace supports:

- lazy tree expansion and tri-state selection;
- keyboard and mouse navigation;
- a visible Tree Filter and a separate Preview Search;
- extension selection;
- two mutually exclusive Git filtering checkboxes for `.gitignore` and tracked
  files; selecting an active checkbox again returns to no Git filtering;
- ordinary Exclusions independent from Git filtering;
- all five content-processing options: Hide Secrets, Hide Private Data,
  Compress Code, Strip Comments, and Strip Blank Lines;
- redaction counters for Hide Secrets and Hide Private Data after the current
  selection is scanned;
- tree, content, and tree-plus-content preview;
- ASCII, JSON, XML, and Markdown formats;
- file, folder, character, token, and byte metrics;
- saved project settings and portable settings files;
- context, exact-folder, and ZIP export;
- dry-run summaries and cancellation;
- opening the current state in Desktop.

Tracked-files mode uses the installed Git CLI. On startup, an unavailable tracked
selection does not open the workspace. If an interactive mode change cannot load an
applicable index, TUI keeps the last usable Git mode. It never silently substitutes
`.gitignore` or an unfiltered tree.

`Ctrl+P` opens a searchable Action Palette from Welcome or the workspace.
Important features do not depend on memorizing letter shortcuts: the palette and
Parameters and visible action bars call the same actions as the keyboard commands
and restore the
previous pane when canceled.

Changing checked nodes updates the selection projection without rescanning the
filesystem. Structural changes use the canonical refresh pipeline. Preview
refresh is cancelable, debounced, and bounded for large projects.

## Keys

| Key | Action |
|---|---|
| Up / Down | navigate |
| Left / Right | collapse / expand |
| Enter | open or activate |
| Space | toggle the selected node |
| `/` | search |
| Ctrl+P | searchable Action Palette |
| Tab / F6 | focus the next major pane |
| Shift+Tab / Shift+F6 | focus the previous major pane |
| `1`, `2`, `3` | tree, content, tree plus content |
| `F` | format |
| `M` | focus Git filtering in Exclusions |
| `X` | focus Exclusions |
| `T` | focus File Types |
| `E` | export context |
| `Z` | export project or ZIP |
| `A` | analyze |
| `G` | open Desktop |
| F1 or `?` | help |
| Esc | close the active overlay |
| `Q` | quit |

The footer shows actions relevant to the active layout.

When Context Preview has focus, Up/Down and `j`/`k` scroll by line,
Page Up/Page Down scroll by page, and Home/End move to the start or end.
Left/Right scroll horizontally when content overflows. In compact layouts,
moving focus also makes the corresponding Tree, Preview, or Parameters pane visible. Pane
focus and preview position survive Help, settings overlays, refreshes, exports,
cancellation, and terminal resize.

Within Parameters, Up/Down and `j`/`k` move through the active mini-list. At a
list boundary focus crosses to the adjacent mini-panel. Enter or Space toggles
the selected row. Exclusions and File Types each expose an independent vertical
scrollbar only when their content overflows. Project Tree exposes vertical and
horizontal scrollbars under the same overflow-only policy.

When Hide Secrets has findings, `[` and `]` move between highlighted occurrences;
`Enter` or `Space` toggles keep-as-is for the active occurrence. That decision is
used by every output in the current session and is not saved in the project profile.

## Preview

The regular Preview follows the Desktop model: Tree, Content, or Tree + Content
in ASCII, JSON, XML, or Markdown. It is optimized for inspection and does not
show export-document scaffolding such as Markdown headings and fences. The active
mode and format are always visible. Exact context-export output remains available
as an advanced Action Palette action and is never presented as if it were the
regular Preview.

Large contexts use the shared preview document abstractions. Small documents
stay in memory; larger documents use temporary file-backed UTF-8 storage with
line and file-section indexes. The viewport reads only visible lines, Preview
Search scans the indexed document, and Home/End can reach the first and final
selected files. The range line reports visible
files/sections, lines, columns, and totals, so partial visibility is never
silent. Rapid changes cancel stale Preview work, and retired temporary documents
are disposed after the replacement is active.

Vertical and horizontal scrollbars are thin, neutral, and separate from operation
progress. Each appears only for real overflow; horizontal position remains
keyboard and mouse navigable without a progress-colored bar.

## Screen Safety

`--screen auto` selects an appropriate screen mode from terminal capabilities and
environment signals such as `TMUX`, `ZELLIJ`, `TERM`, and redirection.

- `alternate` uses a full-screen alternate buffer.
- `inline` keeps the main buffer and preserves scrollback where possible.

Normal exit, cancellation, and unhandled failure restore the cursor, colors,
mouse mode, terminal settings, and alternate screen through a `try/finally`
boundary.

`--no-mouse`, `--color never`, and `--plain` provide conservative fallbacks.
`NO_COLOR`, `TERM=dumb`, redirected streams, and CI are also respected.

Native macOS TUI validation covers published startup, keyboard navigation,
resize, normal exit, and observable parent-shell usability. Extended `Ctrl+Z`
and job-control scenarios are not part of the certified contract.

Terminal.Gui 2.4.17 may briefly emit a mouse-enable sequence while initializing
its ANSI driver, before DevProjex applies `--no-mouse`. In `--no-mouse` sessions,
DevProjex disables tracking before normal input, ignores mouse events, and
restores the terminal on exit. Release validation checks observable behavior and
shell usability; it does not promise that Terminal.Gui emits no initialization
sequence at all. The upstream behavior is documented in the
[pinned implementation](https://github.com/tui-cs/Terminal.Gui/blob/d0a0ed9b150d3fc8aacf4ab07b7f7d91264fe6d6/Terminal.Gui/Drivers/AnsiDriver/AnsiOutput.cs#L128-L150).

## Export

The export confirmation asks whether to export and presents a compact aligned
table containing destination, file and folder counts, size, estimated tokens,
filters, and diagnostics. Export is the default action. A destination conflict
uses a short error containing only the conflicting path. Successful exports do
not open another dialog; the result path appears transiently in the status bar.

When Hide Secrets is enabled, project-copy confirmation states that matching text
will change, binary files will remain unchanged, and the folder or ZIP may not
build or run. Keep-as-is decisions made in Preview also apply to the output.

Context, project-copy, ZIP, and portable-profile destinations use the same
canonical outside-source and existing-parent policy as direct commands. The
suggested export path uses the external current directory only after the same
canonical source-safety check; otherwise it uses a separately validated sibling
of the source. If neither location can be established safely, no default is
suggested. A filesystem alias into the source is never suggested.
When an external alias remains stable after canonical safety validation, the
suggestion keeps the user's absolute path spelling instead of exposing an
equivalent physical-system alias.

Folder and ZIP exports use measured progress from the shared copy engine:
processed entries, total entries, percentage, written bytes, elapsed time, and
the real operation phase. Context preparation and document writing use an
indeterminate status because those stages do not expose a trustworthy total.
DevProjex never manufactures a percentage.

Repository cloning and project loading use the same operation surface. Stages
without an honest total remain indeterminate and show their current phase;
measured Git object/transfer progress is shown only when emitted by Git.

Esc or Ctrl+C cancels active export work before it can quit the TUI. Cancellation
removes staging data and returns to the same usable workspace and pane focus.
Completion returns directly to the workspace and reports the output path in the
status bar without interrupting keyboard navigation.

For very large explicit selections, save a portable profile and use
`--profile FILE` instead of producing a command with hundreds of `--select`
arguments.
