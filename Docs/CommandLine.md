# DevProjex Command Line

DevProjex Desktop, Terminal, and CLI are presentation layers over the same
project-analysis, selection, exclusion, preview, and export services. The portable
distribution contains one executable: `DevProjex.exe`.

## Getting Started

After enabling **Help > Launch from terminal**:

```shell
devprojex --help
devprojex
devprojex analyze .
devprojex export context . -o context.md
devprojex open . --preview
```

`devprojex` opens the interactive Terminal Workspace when stdin and stdout are
interactive. With redirected streams it prints plain root help and exits. A
desktop shortcut or direct launch without an attached terminal opens Avalonia.

The executable name is shown as `devprojex` throughout this document. Portable
users can invoke the same commands through the generated wrapper or directly with
the platform-specific executable path.

## Command Tree

```text
devprojex
├── tui
├── open
├── analyze
├── export
│   ├── context
│   └── project
├── profile
│   ├── show
│   ├── export
│   ├── import
│   ├── validate
│   └── reset
├── ui
│   ├── list
│   ├── status
│   ├── activate
│   ├── preview
│   ├── tree
│   ├── filter
│   └── search
├── doctor
├── completion
└── dev
    ├── benchmark
    │   ├── analysis
    │   └── ui
    └── session
```

`dev` is a hidden maintainer namespace. See `CONTRIBUTING.md` for its supported
diagnostic workflows.

Commands, option names, enum tokens, JSON properties, and XML element names are
stable English identifiers. `--language CODE` localizes human-readable help,
status, diagnostics, and Terminal Workspace labels.

## Common Selection Options

`analyze`, `export context`, `export project`, and `open` accept the same typed
selection:

```text
--profile <standard|local|FILE>
--root <PATH>                 repeatable
--extension <EXT>            repeatable
--select <RELATIVE_PATH>     repeatable
--git-mode <none|gitignore|tracked>
--exclude <NAME>             repeatable
```

Git filtering is independent from ordinary Exclusions.

Git modes:

| Token | Behavior |
|---|---|
| `none` | No Git-based filtering |
| `gitignore` | Respect applicable hierarchical `.gitignore` rules |
| `tracked` | Include only paths known to applicable Git indexes |

Exclusion tokens:

```text
smart-ignore
hidden-folders
hidden-files
dot-folders
dot-files
empty-folders
empty-files
extensionless-files
none
```

If at least one `--exclude` is present, the supplied values are the exact ordinary
exclusion set. `--exclude none` selects an empty set and cannot be combined with
another exclusion. If an option is absent, its value comes from the selected
profile.

Selected paths are relative to the project root. A file selects that file; a
directory selects its effective subtree. Parent/child overlaps are deduplicated.
An empty selected-path set means the complete effective tree. Absolute paths,
`..`, and link-based escapes are rejected.

## Terminal Workspace

```shell
devprojex
devprojex tui [PROJECT]
```

Options:

```text
--profile <standard|local|FILE>
--screen <auto|alternate|inline>
--mouse
--no-mouse
--color <auto|always|never>
--plain
--language <CODE>
```

The TUI uses the local project profile when one exists, otherwise the standard
profile. It provides recent local and Git workspaces, a lazy project tree,
readable and exact Raw Preview, Context Controls, a searchable Action Palette,
selection, search, roots, extensions, Git filtering, Exclusions, metrics,
profiles, and context/folder/ZIP export.
See [TerminalWorkspace.md](TerminalWorkspace.md).

## Open Desktop

```shell
devprojex open [PROJECT] [options]
```

Useful options:

```text
--last
--new-window
--wait
--preview
--view <tree|content|tree-content>
--tree-format <text|markdown|json|xml>
--filter <QUERY>
--search <QUERY>
```

`PROJECT` defaults to the current directory. `--last` cannot be combined with a
project argument. `--filter` and `--search` are mutually exclusive. `--view` and
`--search` imply `--preview`.

Without `--new-window`, DevProjex reuses a suitable desktop instance through
local per-user IPC. The default returns after the desktop accepts the request;
`--wait` waits until the requested project and state are applied.

Examples:

```shell
devprojex open .
devprojex open . --preview --view tree-content
devprojex open . --search "Program"
devprojex open --last
```

## Analyze

```shell
devprojex analyze [PROJECT] [options]
```

Defaults:

- project: current directory;
- format: `text`;
- output: stdout;
- profile: `standard`.

Specific options:

```text
--format <text|json>
-o, --output <PATH|->
--strict
--dry-run
--color <auto|always|never>
--progress <auto|always|never>
--verbosity <quiet|minimal|normal|detailed|diagnostic>
--plain
```

Text is a human-readable project summary. JSON is a stable machine document with
`schemaVersion`. `--strict` still writes the report, then returns exit code `3`
when policy diagnostics exist.

Examples:

```shell
devprojex analyze .
devprojex analyze . --format json -o -
devprojex analyze ./app --format json -o report.json --strict
devprojex analyze . --git-mode tracked --exclude smart-ignore
```

## Export Context

```shell
devprojex export context [PROJECT] [options]
```

Defaults:

- view: `tree-content`;
- format: `markdown`;
- output: stdout;
- profile: `standard`.

Specific options:

```text
--view <tree|content|tree-content>
--format <text|markdown|json|xml>
-o, --output <PATH|->
--force
--dry-run
```

The format applies to the entire document. JSON and XML are parseable structured
documents; Markdown contains headings, a fenced tree, and fenced text-file
content. Binary bytes are never embedded in context output. Machine documents
mark binary entries with metadata.

When output is stdout, stdout contains only the context document. When output is a
file, stdout contains one absolute result path. Existing files are conflicts
unless `--force` is used; replacement is atomic.

Examples:

```shell
devprojex export context .
devprojex export context . --view tree --format json -o -
devprojex export context . --view content --format xml -o context.xml
devprojex export context . --format markdown -o context.md --force
```

## Export Project

```shell
devprojex export project [PROJECT] --as <folder|zip> -o <PATH> [options]
```

The destination is exact:

```shell
devprojex export project . --as folder -o ./submission
devprojex export project . --as zip -o ./submission.zip
```

The first command creates exactly `./submission`; it does not create an additional
project-name child or `(2)` suffix. The folder must not exist. A ZIP path must end
in `.zip` and must not exist unless `--force` is supplied. `--force` is not valid
for folder exports.

Folder and ZIP exports preserve selected binary bytes, timestamps, directory
structure, and included empty directories. Staging is cleaned after cancellation
or failure. Canonical destination checks reject destinations equal to or inside
the source, including paths reached through symlinks or junctions.

On success stdout contains exactly one absolute result path. Measured progress and
warnings use stderr.

## Profiles

```shell
devprojex profile show [PROJECT] [--profile standard|local|FILE] [--format text|json]
devprojex profile export [PROJECT] --profile local -o FILE [--force]
devprojex profile import FILE [PROJECT] [--apply]
devprojex profile validate FILE
devprojex profile reset [PROJECT]
```

Direct commands default to `standard`. Terminal Workspace uses `local` when
available, then `standard`. Explicit CLI selection options override profile
fields. See [CLI-Profiles.md](CLI-Profiles.md).

## Desktop Control

```shell
devprojex ui list [--format text|json]
devprojex ui status
devprojex ui activate
devprojex ui preview open [--view tree|content|tree-content]
devprojex ui preview close
devprojex ui preview set-view <tree|content|tree-content>
devprojex ui tree set-format <text|markdown|json|xml>
devprojex ui filter set <QUERY>
devprojex ui filter clear
devprojex ui search set <QUERY>
devprojex ui search next
devprojex ui search previous
devprojex ui search clear
```

Targetable actions accept:

```text
--instance <ID>
--project <PATH>
--timeout <DURATION>
```

IPC is local-only and per-user. It exposes semantic actions, not arbitrary method
invocation. See [Desktop-Control.md](Desktop-Control.md).

## Doctor

```shell
devprojex doctor
devprojex doctor --format json
```

Doctor inspects version/runtime, package type, terminal capabilities, launcher and
PATH resolution, Git, current directory, profile/data/cache/temp access, desktop
IPC registrations, and tracked-mode readiness. It reports fixes as hints but does
not change the system.

## Completion

Completion scripts are generated from the same command tree:

```shell
devprojex completion bash
devprojex completion zsh
devprojex completion fish
devprojex completion powershell
```

Evaluate or install the generated script according to the shell's normal
completion mechanism.

## Streams and Exit Codes

stdout is reserved for payloads, one result path, help/version, and completion.
Progress, warnings, diagnostics, migration guidance, and errors use stderr. JSON
and XML never contain ANSI, animation frames, or extra summary lines.

| Code | Meaning |
|---:|---|
| 0 | Success, help, or version |
| 1 | Runtime or I/O failure |
| 2 | Invalid syntax, option, value, or combination |
| 3 | Policy/check failure |
| 4 | Destination conflict |
| 5 | Desktop target unavailable or ambiguous |
| 130 | Canceled |

Human errors include a stable `DPX-*` code. Normal output does not expose raw
platform exception messages. See [CLI-Output-Contract.md](CLI-Output-Contract.md).

## Legacy Syntax

The experimental flat CLI is no longer executed. Recognized legacy action flags
return exit code `2` and print an exact replacement command to stderr. See
[CLI-Migration.md](CLI-Migration.md).

## More Detail

- [CLI-V1-Contract.md](CLI-V1-Contract.md): normative public contract
- [CLI-Architecture.md](CLI-Architecture.md): layers and one-EXE routing
- [CLI-Output-Contract.md](CLI-Output-Contract.md): streams and schemas
- [CLI-Profiles.md](CLI-Profiles.md): portable profiles and precedence
- [Desktop-Control.md](Desktop-Control.md): local IPC
- [TerminalWorkspace.md](TerminalWorkspace.md): interactive TUI
