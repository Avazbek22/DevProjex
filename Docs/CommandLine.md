# DevProjex Command Line

DevProjex Desktop, Terminal, and CLI follow the same
inspect -> select -> verify -> export product workflow. Terminal Workspace and
direct CLI share terminal planning and document services; Desktop keeps its
established presentation and export orchestration. They are not one
interchangeable implementation pipeline.

The portable distribution contains one primary executable per RID:
`DevProjex.exe` on Windows and `DevProjex` on Linux and macOS.

## Getting Started

After enabling **Help > Launch from terminal**:

```shell
devprojex --help
devprojex
devprojex analyze .
devprojex export context . -o ../devprojex-context.md
devprojex open . --preview
```

`devprojex` opens the interactive Terminal Workspace when stdin and stdout are
interactive. With redirected streams it prints plain root help and exits. A
desktop shortcut or direct launch without an attached terminal opens Avalonia.

The executable name is shown as `devprojex` throughout this document. Portable
users should install or generate the platform launcher and use that command.
Direct invocation of the physical Windows WinExe path is an advanced diagnostic
detail and is not the supported shell entry point.

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
selection. `open` additionally accepts `auto`:

```text
--profile <standard|local|FILE>
--root <PATH>                 repeatable
--extension <EXT>            repeatable
--select <RELATIVE_PATH>     repeatable
--git-mode <none|gitignore|tracked>
--exclude <NAME>             repeatable
--hide-secrets [<true|false>]
--compress [<true|false>]
```

For `open`, the first line is `--profile <auto|standard|local|FILE>` and its
default is `auto`. Direct analyze/export commands default to `standard`.

Git filtering is independent from ordinary Exclusions.

Git modes:

| Token | Behavior |
|---|---|
| `none` | No Git-based filtering |
| `gitignore` | Respect applicable hierarchical `.gitignore` rules |
| `tracked` | Include only paths returned from applicable indexes by the installed Git CLI; no readable index fails closed with exit `3` |

A readable empty index is a valid tracked view with zero files. If at least one index
loads but a nested index does not, that nested scope is excluded and reported with
`DPX-GIT-TRACKED-INDEX-PARTIAL`. If none load, commands report
`DPX-GIT-TRACKED-INDEX-UNAVAILABLE`; they never reinterpret `tracked` as `gitignore`.
An absent `.gitignore` is an active empty rule set, not a fallback to `none`.
The administrative path named exactly `.git` remains excluded; `.github` and other
`.git*` names are not treated as Git metadata.

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

`--hide-secrets` is a separate, additive content transformation. It does not
replace Exclusions or change the effective tree. It replaces detected values in
selected text files and keeps their surrounding context. It is off in the
`standard` profile. Binary
files are not inspected, and no findings is not a security guarantee. See
[HideSecrets.md](HideSecrets.md).

`--compress` is also independent from path selection and is off in the
`standard` profile. It replaces block bodies of named methods, functions, and
constructors with minimal syntax-valid placeholders (`{ }`, `...` for Python, or an empty Ruby
method body between its declaration and `end`) in supported C, C++, C#, Go, Java, JavaScript,
Kotlin, PHP, Python, Ruby, Rust, Scala, TSX, and TypeScript
files. JavaScript-family block
functions stored in object properties, assigned or exported under a stable binding,
or wrapped one or two calls deep under that binding are compressed as well. The
property or binding name and function parameters remain visible; bare callbacks
without a binding remain complete.
An expression body whose expression fits on one source line remains byte-for-byte
complete as signature-level context; a multiline expression is implementation and
is compressed like a block body. Free lambdas or closures remain complete because
removing an unbound body would leave no useful name. Fields and language-level
properties are kept byte-for-byte complete, including their initializers and
property accessors, because they describe project state and public behavior.
Python also keeps a leading function docstring and the
complete class `__init__` and `__post_init__` methods, where instance state is
declared. Ruby likewise keeps class `initialize` methods, while named `method` and
`singleton_method` bodies are removed without collapsing class/module or DSL blocks.
PHP keeps properties, constants, enum cases, and `__construct`; named functions and methods
are compressed in both PHP-only and mixed HTML/PHP files, while anonymous functions and arrow
functions remain complete. Scala compresses braced named `def` bodies and multiline ordinary
expression bodies while preserving `val`, `var`, `given`, case-class parameters, and class-level
constructor statements. Scala 3 significant-indentation bodies remain complete with the pinned
grammar because their replacement boundary is not structurally stable. Kotlin preserves
properties, custom accessors, primary-constructor state, data classes, enum entries, annotations,
and free lambdas. Named block functions, `init` blocks, secondary constructors, and multiline
expression bodies are compressed to block-form declarations in both `.kt` and `.kts`; one-line
expression functions remain complete. Kotlin never emits `= { }`, because that form is a lambda;
Scala intentionally uses it as a block expression. Unsupported or
conservatively rejected files remain complete. The same
transformed content is used by analysis metrics, context documents, folder exports,
and ZIP exports.

Modern local profiles retain checked and unchecked states across roots, extensions,
and Exclusions. Newly discovered rows use current defaults in Desktop, CLI, and TUI;
explicit CLI collections remain exact and invocation-only.

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
--profile <auto|standard|local|FILE>
--screen <auto|alternate|inline>
--mouse
--no-mouse
--color <auto|always|never>
--plain
--language <CODE>
```

The TUI default `auto` profile uses the local project profile when one exists,
otherwise the standard profile. It provides recent local and Git workspaces, a lazy project tree,
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
project argument or selection/profile overrides. `--filter` and `--search` are
mutually exclusive. `--view` and `--search` imply `--preview`.

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
--color <auto|always|never>
--progress <auto|always|never>
--verbosity <quiet|minimal|normal|detailed|diagnostic>
--plain
```

Text is a human-readable project summary. JSON is a stable machine document with
`schemaVersion`. `--strict` still writes the report, then returns exit code `3`
when policy diagnostics exist. Analysis is already read-only and therefore has
no `--dry-run` option. A file destination must be outside the source project and
must not already exist. Its parent directory must already exist.

Examples:

```shell
devprojex analyze .
devprojex analyze . --format json -o -
devprojex analyze ./app --format json -o report.json --strict
devprojex analyze . --git-mode tracked --exclude smart-ignore
devprojex analyze . --compress --format json
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

With `--hide-secrets`, detection failure fails closed and produces no complete output
artifact. A selected text file above the supported scan limit is omitted from the
context document, the same as without `--hide-secrets`; `export project` leaves it out
of the copy and names it in `DEVPROJEX-NOTICE.txt`.

When output is stdout, stdout contains only the context document. When output is a
file, stdout contains one absolute result path. Existing files are conflicts
unless `--force` is used; replacement is atomic. The destination parent directory
must already exist.

`--force` is valid only for a file destination, never for stdout. `--dry-run`
performs planning and destination preflight but does not generate a document,
create an artifact, or print a result path. Its operational plan is written to
stderr.

Examples:

```shell
devprojex export context .
devprojex export context . --view tree --format json -o -
devprojex export context . --view content --format xml -o ../devprojex-context.xml
devprojex export context . --format markdown -o ../devprojex-context.md --force
devprojex export context . --hide-secrets --format markdown -o ../devprojex-redacted.md
devprojex export context . --compress --format markdown -o ../devprojex-compact.md
```

## Export Project

```shell
devprojex export project [PROJECT] --as <folder|zip> -o <PATH> [options]
```

The destination is exact:

```shell
devprojex export project . --as folder -o ../devprojex-submission
devprojex export project . --as zip -o ../devprojex-submission.zip
devprojex export project . --compress --as zip -o ../devprojex-compact.zip
```

The first command creates exactly `../devprojex-submission`; it does not create an
additional project-name child or `(2)` suffix. The folder must not exist. A ZIP path must end
in `.zip` and must not exist unless `--force` is supplied. `--force` is not valid
for folder exports. In both cases the destination parent directory must already
exist.

Folder and ZIP exports preserve selected binary bytes, timestamps, directory
structure, and included empty directories. Staging is cleaned after cancellation
or failure. Canonical destination checks reject destinations equal to or inside
the source, including paths reached through symlinks or junctions.

With `--hide-secrets`, detected values in text files are replaced. Binary files remain
unchanged. The result is intentionally not byte-for-byte faithful and may not
build or run. `--dry-run` states this before any destination or staging path is
created.

On success stdout contains exactly one absolute result path. Measured progress and
warnings use stderr.

## Profiles

```shell
devprojex profile show [PROJECT] [--profile standard|local|FILE] [--format text|json]
devprojex profile export [PROJECT] [--profile standard|local|FILE] -o FILE [--force]
devprojex profile import FILE [PROJECT] [--apply]
devprojex profile validate FILE
devprojex profile reset [PROJECT]
```

Direct commands default to `standard`. Terminal Workspace uses `local` when
available, then `standard`. Explicit CLI selection options override profile
fields. See [CLI-Profiles.md](CLI-Profiles.md).

Portable profile output must resolve outside the source project, including
filesystem aliases, and its parent directory must already exist. Source safety is
validated before conflicts. An existing file requires `--force` for atomic
replacement; an existing directory is always a destination conflict. On success
stdout contains one absolute committed path. Errors and diagnostics use stderr.

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
not change the system. Desktop IPC is reported as skipped until a Desktop session
initializes it; an existing inaccessible or path-conflicted registry is a failure.

## Completion

Completion scripts are generated from the same command tree:

```shell
devprojex completion bash
devprojex completion zsh
devprojex completion fish
devprojex completion powershell
```

The generated script queries the production command tree using the current
command line and cursor position. Suggestions are scoped to the active command,
option values, repeatability, conflicts, and path arguments. It does not execute
Avalonia or require another DevProjex executable. Evaluate or install it using
the shell's normal completion mechanism.

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

The experimental flat CLI is no longer executed. A small set of unambiguous
legacy action/value shapes returns exit code `2` and prints an exact replacement
argument vector to stderr. Malformed, incomplete, duplicated, or unsupported
legacy shapes do not receive a speculative replacement. See
[CLI-Migration.md](CLI-Migration.md).

## More Detail

- [CLI-V1-Contract.md](CLI-V1-Contract.md): normative public contract
- [CLI-Architecture.md](CLI-Architecture.md): layers and one-EXE routing
- [CLI-Output-Contract.md](CLI-Output-Contract.md): streams and schemas
- [HideSecrets.md](HideSecrets.md): redaction, overrides, limits, and rule provenance
- [CLI-Profiles.md](CLI-Profiles.md): portable profiles and precedence
- [Desktop-Control.md](Desktop-Control.md): local IPC
- [TerminalWorkspace.md](TerminalWorkspace.md): interactive TUI
