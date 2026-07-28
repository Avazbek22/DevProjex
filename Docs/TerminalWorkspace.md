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

The default profile is `local` when the project has a local DevProjex profile,
otherwise `standard`.

## Welcome

The welcome screen offers:

- open the current directory when it is a reasonable project candidate;
- open a recent project;
- open a recent Git repository;
- browse for a folder;
- clone through the existing DevProjex Git service;
- open a portable profile;
- open DevProjex Desktop;
- help and exit.

DevProjex does not automatically scan a filesystem root, a broad home directory,
or another unsafe default location.

Opening a project from Welcome uses its local profile when one exists and the
deterministic standard profile otherwise. Recent Projects, Browse, Clone, Help,
and Open Profile are nested workflows: canceling or closing them returns to
Welcome without ending the terminal session.

Recent Projects reads and updates the same per-user history as Desktop. Selecting
an available entry shows a loading state and opens its workspace without ending
the TUI. A successfully opened entry moves to the front of the existing history.
Unavailable entries remain visible so they can be removed intentionally.

If recent-project storage is temporarily locked, the workflow offers Retry
instead of presenting an empty list. A valid backup is used when the primary
history is corrupt. An invalid local-profile store offers a deterministic
Standard-profile recovery path; unavailable roots or file types in an otherwise
valid local profile open with diagnostics.

Recent Git Repositories reads the repository history from the same
`RecentProjectsStore` used by Desktop. A healthy cached clone opens without a
network request. A missing or damaged cache remains visible and offers explicit
Back, Remove, or Clone Again actions. Removing history never silently deletes a
cache. Equivalent supported repository URLs are normalized to avoid duplicate
rows.

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
Context Controls visible together. Context Controls exposes the profile, source,
selection, Git filtering, Exclusions, Preview settings, diagnostics, and all
three export entry points. Narrow terminals switch to focused panes without
losing selection or navigation state.

![DevProjex Terminal Workspace](Media/terminal-workspace/workspace.png)

## Layout

The workspace adapts to terminal size:

| Width | Layout |
|---:|---|
| 150 columns or wider | Tree, Preview, and Context Controls |
| 120-149 columns | Tree and Preview; Controls opens as a focused pane |
| 80-119 columns | tabbed single-pane workspace |
| 60-79 columns | compact single-pane workspace |
| below 60 columns, or below 20 rows | resize guidance |

If the terminal is too small to operate safely, the workspace shows a compact
size hint rather than corrupting the screen.

## Workflow

The workspace supports:

- lazy tree expansion and tri-state selection;
- keyboard and mouse navigation;
- project search;
- root and extension selection;
- one Git filtering choice: none, `.gitignore`, or tracked files;
- ordinary Exclusions independent from Git filtering;
- tree, content, and tree-plus-content preview;
- readable Preview and exact Raw output presentation;
- text, Markdown, JSON, and XML tree formats;
- file, folder, character, token, and byte metrics;
- standard, local, and portable profiles;
- context, exact-folder, and ZIP export;
- dry-run summaries and cancellation;
- opening the current state in Desktop.

`Ctrl+P` opens a searchable Action Palette from Welcome or the workspace.
Important features do not depend on memorizing letter shortcuts: the palette and
Context Controls call the same actions as the keyboard commands and restore the
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
| `V` | readable Preview or Raw output |
| `M` | Git filtering |
| `X` | Exclusions |
| `R` | roots |
| `T` | file types |
| `E` | export context |
| `Z` | export project or ZIP |
| `P` | profile |
| `A` | analyze |
| `G` | open Desktop |
| F1 or `?` | help |
| Esc | close the active overlay |
| `Q` | quit |

The footer shows actions relevant to the active layout.

When Context Preview has focus, Up/Down and `j`/`k` scroll by line,
Page Up/Page Down scroll by page, and Home/End move to the start or end.
Left/Right scroll horizontally when wrapping is disabled. In compact layouts,
moving focus also makes the corresponding Tree, Preview, or Controls pane visible. Pane
focus and preview position survive Help, settings overlays, refreshes, exports,
cancellation, and terminal resize.

## Preview

Readable Preview is the default human-facing representation. It removes
document-format scaffolding such as Markdown headings and fences while keeping
tree and file sections visually distinct. Raw output displays the exact Text,
Markdown, JSON, or XML payload produced by context export. The active
presentation, content view, and format are always visible.

Large contexts use the shared preview document abstractions. Small documents
stay in memory; larger documents use temporary file-backed UTF-8 storage with
line indexes. Readable Preview also indexes file sections. Raw Preview streams
the complete exact export document directly into the backing store instead of
first constructing one complete managed string. The viewport reads only visible
lines, global Preview search scans the indexed document, and Home/End can reach
the first and final selected files. The range line reports visible
files/sections, lines, columns, and totals, so partial visibility is never
silent. Rapid changes cancel stale Preview work, and retired temporary documents
are disposed after the replacement is active.

The subtle position indicator is separate from operation progress. Horizontal
position is reported only when long raw lines overflow; no progress-colored
horizontal bar is drawn.

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

## Export

The export dialog reports output kind, view/format, destination, selected counts,
estimated metrics, Git mode, Exclusions, conflicts, and warnings. After a
successful interactive operation it shows an equivalent direct command.

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
Completion remains visible until dismissed and reports the output path, file and
folder counts, size, measured duration, and equivalent direct command.

For very large explicit selections, save a portable profile and use
`--profile FILE` instead of producing a command with hundreds of `--select`
arguments.
