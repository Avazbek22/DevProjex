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
- browse for a folder;
- clone through the existing DevProjex Git service;
- open a portable profile;
- open DevProjex Desktop;
- help and exit.

DevProjex does not automatically scan a filesystem root, a broad home directory,
or another unsafe default location.

## Layout

The workspace adapts to terminal size:

| Width | Layout |
|---:|---|
| 120 columns or wider | project tree and context preview side by side |
| 80-119 columns | tree and preview tabs |
| below 80 columns | compact single-pane workspace |

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
- text, Markdown, JSON, and XML tree formats;
- file, folder, character, token, and byte metrics;
- standard, local, and portable profiles;
- context, exact-folder, and ZIP export;
- dry-run summaries and cancellation;
- opening the current state in Desktop.

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
| Tab | switch panes |
| `1`, `2`, `3` | tree, content, tree plus content |
| `F` | format |
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

For very large explicit selections, save a portable profile and use
`--profile FILE` instead of producing a command with hundreds of `--select`
arguments.
