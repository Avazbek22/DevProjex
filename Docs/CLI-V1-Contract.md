# DevProjex Terminal and CLI v1 Contract

This document is the normative public contract for DevProjex Terminal and CLI v1.
Code, generated help, completion, README examples, packaging documentation, and
release validation must agree with this document. Other terminal documents explain
the contract but do not override it.

## Product Surfaces

DevProjex follows one workflow across all presentation surfaces:

```text
inspect -> select -> verify -> export
```

- Desktop provides the most visual workflow.
- Terminal Workspace provides keyboard-first interactive work.
- Direct CLI commands provide deterministic stdout, scripts, and CI automation.
- The three surfaces follow the same product workflow; this contract does not
  claim that their presentation and export orchestration is one interchangeable
  implementation pipeline.
- Terminal Workspace and direct CLI share terminal planning and document
  services. Desktop keeps its established presentation and export orchestration.
- Opening, analyzing, or exporting from an existing source project never
  modifies that source tree. Explicit file, folder, ZIP, and portable-profile
  destinations are accepted only outside it.
- Application-owned settings, local profiles, clone/cache storage, and runtime
  state are intentional writes outside the source tree.
- "Source tree" means the physical files and directories rooted at the opened
  project. DevProjex does not follow a source-side symlink or junction to an
  external target; that external target does not become part of the source merely
  because the user created a reverse alias to it.
- DevProjex has no telemetry.

`DevProjex.Terminal` remains a class library inside the primary application. Every
RID publish contains exactly one primary DevProjex application executable.

## Supported Entry Points

- `devprojex` in an interactive terminal starts Terminal Workspace.
- `devprojex <command>` runs a direct command without initializing Avalonia.
- `devprojex open` opens or reuses Desktop.
- `devprojex ui` sends a semantic request to an existing Desktop instance.
- A graphical launch starts Desktop.

On Windows, `devprojex` means the installed App Execution Alias or the generated
`devprojex.cmd` launcher. The launcher waits for completion, forwards arguments
without changing their values, preserves stdout and stderr, and returns the exact
application exit code. Direct invocation of the physical WinExe path is an
advanced implementation detail and is not the supported shell entry point.

On Linux and macOS, `devprojex` is the installed executable or generated POSIX
launcher. The launcher uses `exec` and forwards the original argument vector.

The published application is a self-contained single-file bundle. Its .NET host
may extract native libraries before managed routing: by default under
`$HOME/.net` on Linux/macOS and `%TEMP%/.net` on Windows. DevProjex guarantees
direct-command startup when that default is writable or when a private writable
`DOTNET_BUNDLE_EXTRACT_BASE_DIR` is supplied. An unset or read-only Unix home, or
an unusable Windows temporary directory, therefore requires the explicit
extraction base. The v1 contract does not promise extraction-free startup.

## Public Command Tree

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
│   │   ├── open
│   │   ├── close
│   │   └── set-view
│   ├── tree
│   │   └── set-format
│   ├── filter
│   │   ├── set
│   │   └── clear
│   └── search
│       ├── set
│       ├── next
│       ├── previous
│       └── clear
├── doctor
└── completion
```

`dev` is a hidden maintainer namespace. It is not part of the stable public v1
contract and never appears in normal help or completion.

Command and option names are case-sensitive. Choice values are accepted
case-insensitively, converted once at the parser boundary, and represented in
help, completion, diagnostics, profiles, JSON, and XML by canonical lowercase
tokens. A supplied unknown choice is a usage error; it never selects a default.

Both `--name value` and `--name=value` are supported where System.CommandLine
supports them. An empty inline assignment such as `--name=` is a missing-value
usage error for a value-taking option and never consumes the following token as
its value. Adding `=` to a flag, for example `--plain=`, is invalid syntax.
Explicit empty argv values for public options and positional arguments are usage
errors; defaults apply only when a value is absent. Lexical integrity errors are
reported before other parser diagnostics. `--` terminates option, help, legacy
detection, and the inline-assignment check. Every following token is argument
data.

## Recursive Options

`--language <CODE>` is recursive and may occur before or after a subcommand. The
supported canonical tokens are:

```text
en ru de fr it es pt pt-pt kk tg uz
```

The default is the detected application language. It affects human help,
diagnostics, status, and TUI labels only. Machine identifiers remain English.

`--help`, `-h`, `-?`, `/?`, `/h`, `--version`, and `-v` use stdout and exit `0`.
Help targets only a valid command path. An unknown token before a help token
remains a usage error instead of silently selecting a later command.

## Shared Selection

`analyze`, `export context`, `export project`, and `open` use:

```text
--profile <standard|local|FILE>
--root <PATH>                         repeatable
--extension <EXT>                    repeatable
--select <RELATIVE_PATH>             repeatable
--git-mode <none|gitignore|tracked>
--exclude <NAME>                     repeatable
--hide-secrets [<true|false>]
--compress [<true|false>]
```

Exclusion tokens are:

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

`--exclude none` is an exact empty exclusion set. Repeating `none` is idempotent,
but combining it with another exclusion is a usage error.

The normative `standard` profile has Git mode `gitignore` and contains all eight
exclusion groups: `smart-ignore`, `hidden-folders`, `hidden-files`, `dot-folders`,
`dot-files`, `empty-folders`, `empty-files`, and `extensionless-files`. Its root,
extension, and selected-path collections are unresolved defaults, so the current
project inventory supplies their available values. An explicit CLI field replaces
only that profile field for the current invocation.

`--hide-secrets` is a separate, additive option and is off in the `standard`
profile. It transforms selected text content after path selection and does not
replace the Exclusions collection, remove a file, or change the effective tree.
Binary files are not inspected and remain byte-identical in physical copies.

When enabled, the same per-occurrence decisions apply to Preview, clipboard,
context documents, folder copies, and ZIP copies. Placeholders use the stable form
`DEVPROJEX_REDACTED[rule-id#index]`; identical values under one rule reuse an index
within a produced output. Preview keep-as-is overrides apply to every later output in the current
application session and deliberately do not persist in profiles.

Detection failure and regex timeout fail closed on every command, reporting
`DPX-SECRET-DETECTION-FAILED` and exit `1`; no complete artifact is published. A
selected text file above the 16 MiB scan limit is not inspected, and costs only itself:
`export context` omits that file's text and completes, exactly as it does for a file
that large without `--hide-secrets`, while `export project` leaves it out of the copy
and names it in `DEVPROJEX-NOTICE.txt`. `--dry-run` announces the same omission.
Uninspected text is never emitted, a file left out of a copy is always named, and no
result describes zero matches as safe or clean.

For v5 compatibility, `--exclude hide-secrets` remains parseable but is omitted
from current help and completion choices. Resolution migrates it to the separate
`selection.hideSecrets` Boolean and removes it from canonical
`selection.exclusions`. An explicit `--hide-secrets true|false` takes precedence
over the legacy token.

`--compress` is a separate, additive content transformation and is off in the
`standard` profile. It preserves declarations and replaces executable bodies with
syntax-valid placeholders in the curated C, C++, C#, Go, Java, JavaScript, Kotlin, PHP, Python,
Ruby, Rust, Scala, TSX, and TypeScript language set. Ruby removes complete method-body lines between
the declaration and `end`; mixed HTML outside PHP sections remains unchanged. A parse failure,
unsupported language, size
limit, structural-gate rejection, or non-shrinking result leaves that file complete.
Scala 3 significant-indentation bodies remain complete because the pinned grammar does not expose
a structurally stable replacement boundary for every following declaration.
Kotlin preserves properties, accessor implementations, primary-constructor state and free
lambdas, while block functions, `init`, secondary constructors and multiline expression bodies are
compressed to block-form declarations for `.kt` and `.kts` files. Kotlin output never uses the
lambda-valued `= { }` form; Scala uses the same text intentionally as a block expression.
Analysis content metrics and every context/folder/ZIP output observe the same
transformed bytes; source files are never modified.

`gitignore` mode reads regular `.gitignore` files reachable in the selected working
tree. When the selected path is below its owning repository/worktree root, the
ancestor rule chain from that root through the selected path is applied before
rules discovered below it. It does not read `.git/info/exclude`, global Git excludes, or symbolic links
named `.gitignore`; Git itself does not follow a symbolic link when accessing that
control file. If no regular `.gitignore` exists, the selected mode remains
`gitignore` with an empty pattern set; the administrative entry named exactly `.git`
is still excluded, while lookalikes such as `.github` and `.git-owned` are ordinary
paths. When a regular `.gitignore` cannot be read, its directory scope is
excluded fail-closed, other scopes continue deterministically, and the invocation
exposes the existing partial-access diagnostic (`DPX-PROJECT-PARTIAL-ACCESS`)
instead of silently including files without complete rule evaluation. Skipping a
`.gitignore` symbolic link is normal Git-compatible behavior and is not an access
diagnostic. The reader strips an initial UTF-8 BOM and otherwise decodes as UTF-8;
UTF-16/UTF-32 BOMs are not auto-detected and reinterpreted as valid rules.

Git pattern and tracked-index path comparison use the effective repository
`core.ignoreCase` value. On macOS, canonical Unicode comparison also follows
`core.precomposeUnicode`. The same resolved semantics apply to prebuilt rules,
dynamically discovered nested `.gitignore` scopes, and tracked-index membership;
refresh invalidates the semantics snapshot together with the ignore caches.
If an identified repository cannot expose its effective Git configuration, neither
pattern matching nor tracked-index membership guesses case or Unicode behavior. The
affected repository scope is unavailable and remains fail-closed. A non-repository folder containing a
standalone `.gitignore` continues to use the host filesystem comparison policy.

Tracked mode requires an available Git CLI because index ownership is obtained from
`git ls-files`; DevProjex does not parse `.git/index` independently. Its readiness
matrix is normative:

- at least one readable index, including a readable empty index: ready;
- no readable applicable index, a missing Git CLI, or a failed index command:
  `DPX-GIT-TRACKED-INDEX-UNAVAILABLE`, error severity, fail-closed tree, exit `3`
  for direct commands (`analyze` still writes its requested report before returning);
- at least one readable index plus unreadable nested indexes:
  `DPX-GIT-TRACKED-INDEX-PARTIAL`, warning severity, affected nested scopes excluded,
  exit `0` unless `analyze --strict` promotes policy diagnostics to exit `3`.

Selection precedence is:

1. Load the selected baseline profile.
2. Replace each field explicitly supplied by the command.
3. Validate paths and Git readiness against the source.
4. Build one canonical `ProjectContextPlan`.

An absent collection inherits the profile. An explicitly supplied collection
replaces that profile field. Selected paths are relative to the source root.
Selecting a directory selects its effective subtree. Parent traversal, absolute
selected paths, and link-based escapes are rejected.

A modern local profile stores complete checkbox maps for root folders, extensions,
and Exclusions. Known rows retain their saved checked or unchecked state. Rows that
appear after the profile was saved use the current product default, consistently in
Desktop, CLI, and TUI. Legacy selected-only local data is promoted at the storage
boundary: its selected values become checked state entries and other rows use current
defaults. Explicit CLI root, extension, Git-mode, or exclusion overrides are exact
invocation-only fields and do not rewrite the local profile. Root names are exact
nonblank filesystem names; DevProjex never trims legal leading or trailing whitespace.

Direct commands default to `standard`. TUI and `open` default to `local` when a
valid local profile exists, otherwise `standard`.

`open --last` resolves the last project itself. It cannot be combined with a
project argument or any selection override because silently discarding those
overrides is forbidden.

## Command Contracts

### `tui`

```text
devprojex tui [PROJECT]
  --profile <auto|standard|local|FILE>
                                      default: auto
  --screen <auto|alternate|inline>    default: stored setting, then auto
  --mouse
  --no-mouse
  --color <auto|always|never>         default: auto
  --plain
```

`PROJECT` defaults to the current directory. Any readable directory is valid.
Filesystem roots, broad home directories, and protected system locations are not
opened automatically from Welcome.

The default mouse policy is enabled when the active terminal driver supports it.
`--mouse` enables mouse input for this session, `--no-mouse` disables it, and the
combination is a usage error. Every required workflow remains keyboard-accessible.

An explicit `--screen` applies only to the current invocation. It never writes a
persistent setting. `auto` resolves from terminal capabilities and multiplexer
signals. Normal exit, cancellation, and failure restore cursor, colors, mouse,
input mode, and alternate-screen state.

TUI requires interactive input and output and refuses `TERM=dumb`.

Git clone work never prompts through the TUI terminal. Git standard streams are
isolated, Git/GCM/askpass prompting is disabled, and SSH transport uses OpenSSH
batch mode while retaining the user's standard SSH config, known-hosts files,
keys, and agent. Missing credentials or host trust therefore produce the normal
recoverable clone error instead of taking over `/dev/tty`.

### `open`

```text
devprojex open [PROJECT]
  --last
  --new-window
  --wait
  --preview
  --view <tree|content|tree-content>
  --tree-format <text|markdown|json|xml>
  --filter <QUERY>
  --search <QUERY>
  <shared selection options>
```

`PROJECT` defaults to the current directory. `--filter` and `--search` conflict.
`--view` and `--search` imply `--preview`. `--wait` means wait until the requested
project and state have been applied, not until Desktop closes.
`--profile auto` is accepted by `open` and resolves `local` when a valid local
profile exists, otherwise `standard`.

On success, stdout contains the accepted absolute project path when a project was
resolved. Operational and handoff diagnostics use stderr.

### `analyze`

```text
devprojex analyze [PROJECT]
  --format <text|json>                default: text
  -o, --output <PATH|->               default: -
  --strict
  <shared selection options>
  <shared output options>
```

Analysis is already read-only, so it has no `--dry-run` option. `--strict` writes
the requested report first and returns `3` when policy diagnostics exist.

For text output, one canonical serializer defines fields, ordering, values, and
exactly one platform-native final line separator. Redirected stdout, `--plain`,
and file output use that
canonical text. Interactive stdout without `--plain` may use a rich presentation,
but it contains the same logical fields and values.

An analysis file must be outside the source project and must not already exist.
There is no analysis `--force` option in v1.

### `export context`

```text
devprojex export context [PROJECT]
  --view <tree|content|tree-content>  default: tree-content
  --format <text|markdown|json|xml>   default: markdown
  -o, --output <PATH|->               default: -
  --force
  --dry-run
  <shared selection options>
  <shared output options>
```

`--force` is valid only for a file destination and performs atomic replacement.
It is a usage error with stdout.

### `export project`

```text
devprojex export project [PROJECT]
  --as <folder|zip>                   required
  -o, --output <PATH>                 required
  --force
  --dry-run
  <shared selection options>
  <shared output options>
```

Folder output creates exactly the requested, previously absent directory. It
never adds a project-name child or a numeric suffix. `--force` is invalid for
folder output. ZIP output requires a `.zip` path; `--force` permits atomic ZIP
replacement.

When `--hide-secrets` is selected, text findings are replaced. Such a copy is intentionally not byte-for-byte faithful
and may not build or run. Binary files remain unchanged. The normal confirmation
and dry-run plan state this before writing.

### Profiles

```text
devprojex profile show [PROJECT]
  --profile <standard|local|FILE>     default: standard
  --format <text|json>                default: text

devprojex profile export [PROJECT]
  --profile <standard|local|FILE>     default: local
  -o, --output <FILE>                 required
  --force

devprojex profile import <FILE> [PROJECT]
  --apply

devprojex profile validate <FILE>
devprojex profile reset [PROJECT]
```

Portable profile export uses the same canonical source-safety and existing-parent
policy as analysis and context file output. The destination must resolve outside
the source project, including filesystem aliases, and its parent must already
exist. Source-safety failures return `3`. After safety validation, an existing
file, directory, or dangling link is a destination conflict (`4`); `--force`
permits atomic replacement of an external file destination, but never replaces a
directory. On success stdout contains the absolute committed path. Import
validates without modifying local state unless `--apply` is present.

### Desktop control

```text
devprojex ui list
  --format <text|json>               default: text

devprojex ui status
devprojex ui activate
devprojex ui preview open
  --view <tree|content|tree-content>
devprojex ui preview close
devprojex ui preview set-view <VIEW>
devprojex ui tree set-format <FORMAT>
devprojex ui filter set <QUERY>
devprojex ui filter clear
devprojex ui search set <QUERY>
devprojex ui search next
devprojex ui search previous
devprojex ui search clear
```

`VIEW` is required and accepts `tree`, `content`, or `tree-content`. `FORMAT` is
required and accepts `text`, `markdown`, `json`, or `xml`. `QUERY` is one
required argument; use shell quoting when it contains spaces. `preview open`
without `--view` preserves the Desktop view while opening the preview.

Every action except `ui list` accepts:

```text
--instance <ID>
--project <PATH>
--timeout <DURATION>                  default: 10s
```

The successful stdout payload is the versioned Desktop protocol response
envelope (`protocolVersion`, `requestId`, `ok`, `state`, `error`), not an
unversioned state fragment. IPC is local, per-user, versioned, and does not expose
arbitrary methods or properties.

### `doctor`

```text
devprojex doctor --format <text|json>
```

The default format is `text`. Checks use `pass`, `warning`, `failure`, or `skip`.
Warnings do not fail the command. A real failure of a required runtime component
returns policy exit code `3`. Doctor is read-only and leaves no probe files.
Desktop IPC is `skip` before a Desktop session has initialized its registry;
an existing but unreadable or path-conflicted registry is a `failure`.

### `completion`

```text
devprojex completion <bash|zsh|fish|powershell>
```

The generated script queries completion from the same command tree and current
cursor context. It does not expose hidden commands. It scopes subcommands and
options, completes choice values and paths, and does not suggest a non-repeatable
option already present. Hidden completion transport details, including the
UTF-8 Base64 arguments used to preserve unfinished quoted input and the
completion working directory in Windows PowerShell 5.1, are internal and are
not part of the public v1 syntax.

## Shared Output Options

Direct analysis and export commands use:

```text
--color <auto|always|never>           default: auto
--progress <auto|always|never>        default: auto
--verbosity <quiet|minimal|normal|detailed|diagnostic>
                                      default: normal
--plain
```

Verbosity controls operational stderr only and never removes a requested stdout
payload:

- `quiet`: errors only;
- `minimal`: errors and warnings;
- `normal`: normal status, warnings, and safe errors;
- `detailed`: normal output plus safe operation context;
- `diagnostic`: detailed output plus exception type and safe stack/request context.

Adjacent levels may currently share messages where no additional safe context
exists. Normal output never contains raw platform exception messages, file
contents, credentials, or environment secrets.

## Public Option Semantics Matrix

This matrix is the option audit index. Detailed tokens and document shapes remain
defined in the command sections above; the matrix records the observable effect
that prevents an accepted option from becoming a no-op.

| Command scope | Option | Default | Explicit observable effect | Conflict or restriction | Stream and exit contract | Contract tests |
|---|---|---|---|---|---|---|
| all commands | `--language` | detected application language | changes localized human help, diagnostics, status, and TUI labels | machine identifiers never localize | requested payload remains on stdout; invalid value exits `2` | parser, help, completion, localization |
| `tui` | `--profile` | `auto` | selects `standard`, `local`, or a profile file before building the workspace plan | `auto` is TUI/open-only | interactive screen; invalid/unresolved value exits `2` | parser, workspace, help, completion |
| `tui` | `--screen` | stored setting, then `auto` | overrides alternate/inline behavior for this invocation only | never persists the override | terminal screen; invalid value exits `2`; state restores on every exit path | parser, settings, PTY lifecycle |
| `tui` | `--mouse`, `--no-mouse` | capability-based auto policy | enables or disables input handling for this session | mutually exclusive | terminal screen; conflict exits `2`; tracking restores on exit | parser, workspace, PTY lifecycle |
| `tui` | `--color`, `--plain` | `auto`, off | selects color policy or strict plain presentation/export rendering | `--plain --color always` exits `2` | terminal screen; no payload stream repurposing | parser, presentation, plain PTY |
| `open` | `--last` | off | resolves the most recent workspace | conflicts with `PROJECT` and every selection override | accepted absolute path on stdout; conflict exits `2` | parser, handler, Desktop IPC |
| `open` | `--new-window` | off | requests a new Desktop instance | none | accepted path on stdout; handoff failures use stderr and `5` | handler, Desktop IPC |
| `open` | `--wait` | off | guarantees that the requested project/state is applied before return | does not wait for Desktop termination | accepted path on stdout; timeout uses stderr and `5` | handler, Desktop IPC |
| `open` | `--preview`, `--view`, `--tree-format` | closed; no explicit view/format | opens preview and applies its typed view/tree format | `--view` implies preview | accepted path on stdout; invalid value exits `2` | parser, handler, Desktop IPC |
| `open` | `--filter`, `--search` | absent | applies the requested Desktop filter or preview search | mutually exclusive; search implies preview | accepted path on stdout; conflict exits `2` | parser, handler, Desktop IPC |
| analyze/context/project/open | `--profile` | `standard`; `open`: `auto` | resolves `standard`, `local`, or a portable profile before explicit overrides | `auto` is accepted only by `open`; conflicts with `open --last` | requested payload/path stays on stdout; unresolved profile exits `2` | parser, resolver, handler, process |
| analyze/context/project/open | `--root` | profile roots | replaces the profile root set with each repeated top-level relative path | repeatable; conflicts with `open --last`; invalid/out-of-source path exits `2` | requested payload/path stays on stdout | parser, resolver, handler, process |
| analyze/context/project/open | `--extension` | profile extensions | replaces the profile extension set with each repeated normalized extension | repeatable; conflicts with `open --last` | requested payload/path stays on stdout | parser, resolver, handler, process |
| analyze/context/project/open | `--select` | profile selected paths | replaces the profile explicit path set with each repeated source-relative path | repeatable; conflicts with `open --last`; invalid/out-of-source path exits `2` | requested payload/path stays on stdout | parser, resolver, handler, process |
| analyze/context/project/open | `--git-mode` | profile Git mode | replaces the profile mode with `none`, `gitignore`, or `tracked` | conflicts with `open --last`; `tracked` requires Git CLI and at least one readable applicable index | on unavailable index, `analyze` preserves its requested report; context/project/open create no artifact and emit no success payload; diagnostic uses stderr and exit `3` | parser, resolver, handler, process |
| analyze/context/project/open | `--exclude` | profile exclusions | replaces the path-exclusion set with repeated typed values | repeatable; `none` conflicts with every other value; conflicts with `open --last` | requested payload/path stays on stdout; invalid value exits `2` | parser, resolver, handler, process |
| analyze/context/project/open | `--hide-secrets` | profile content-transformation state | independently enables or disables detected-value redaction without changing path filters | optional explicit Boolean; conflicts with `open --last` | requested payload/path stays on stdout; inspection failure exits `1` without a complete artifact | parser, resolver, handler, process |
| analyze/context/project/open | `--compress` | profile content-transformation state | independently enables or disables syntax-aware body compression without changing path filters | optional explicit Boolean; conflicts with `open --last` | requested payload/path stays on stdout; unsupported or rejected files remain complete | parser, resolver, handler, process |
| `analyze` | `--format` | `text` | selects the canonical text serializer or analysis JSON | none | document on stdout or in the selected file; invalid value exits `2` | parser, serializer, handler, process |
| `analyze` | `-o`, `--output` | `-` | selects stdout or an exact new report file | existing/unsafe file is rejected; no force or dry-run | document or real absolute path on stdout; conflict exits `4` | handler, destination, process |
| `analyze` | `--strict` | off | writes the report, then treats policy diagnostics as failure | none | requested report remains intact; policy result exits `3` | handler, process |
| analyze/context/project | `--color` | `auto` | selects ANSI color policy independently for the relevant human stream | `always` conflicts with `--plain`; `never` does not force ASCII | requested payload stays on stdout; conflict exits `2` | parser, rendering, process |
| analyze/context/project | `--progress` | `auto` | selects automatic, forced, or disabled operational progress on stderr | quiet/minimal suppress optional progress; plain forced progress is static ASCII | requested payload stays on stdout | parser, rendering, process |
| analyze/context/project | `--verbosity` | `normal` | controls optional operational stderr from quiet through safe diagnostic context | never removes requested stdout or suppresses errors | requested payload stays on stdout; invalid value exits `2` | parser, rendering, process |
| analyze/context/project | `--plain` | off | selects line-oriented ASCII human output and disables ANSI, markup, decoration, and animation | conflicts with `--color always` | machine schema and requested payload stay unchanged; conflict exits `2` | parser, rendering, process |
| `export context` | `--view`, `--format` | `tree-content`, `markdown` | selects typed document sections and serializer | none | document on stdout/file; invalid value exits `2` | parser, serializer, process |
| `export context` | `-o`, `--output` | `-` | selects streaming stdout or an exact context file | destination must be outside source | document or real absolute path on stdout | destination, streaming, process |
| `export context` | `--force` | off | atomically replaces an existing context file | invalid with stdout | success path on stdout; conflict exits `4`, invalid combination `2` | parser, destination, handler |
| `export context` | `--dry-run` | off | runs plan and destination preflight without document generation | creates no parent, staging, or output | stdout empty; one readiness plan on stderr | handler, filesystem-effects, process |
| `export project` | `--as` | required | selects exact folder or ZIP export | missing/invalid value exits `2` | real absolute created destination on stdout | parser, handler, process |
| `export project` | `-o`, `--output` | required | selects the exact destination | folder must be absent; ZIP path ends in `.zip`; destination outside source | real absolute created destination on stdout; conflict exits `4` | parser, destination, integration |
| `export project` | `--force` | off | atomically replaces an existing ZIP | invalid for folder output | success path on stdout; invalid combination exits `2` | parser, destination, integration |
| `export project` | `--dry-run` | off | validates plan, kind, and exact destination without copying | creates no folder, ZIP, parent, or staging | stdout empty; one readiness plan on stderr | handler, filesystem-effects, process |
| `profile show` | `--profile`, `--format` | `standard`, `text` | resolves a profile and selects text or profile JSON | `auto` is not accepted | document on stdout; invalid/unresolved value exits `2` | parser, profile handler |
| `profile export` | `--profile`, `-o`/`--output`, `--force` | `local`, required, off | resolves and atomically writes an exact portable profile outside the source; parent must exist | source safety precedes conflict; existing file needs force; directories always conflict | real committed absolute path on stdout; runtime write failure exits `1`, safety exits `3`, conflict exits `4` | parser, profile handler, filesystem effects |
| `profile import` | `--apply` | off | persists the validated imported profile as local state | absent means validation-only | status/path contract on stdout; invalid profile exits `2`; local-store write failure exits `1` | profile handler, persistence |
| `ui list` | `--format` | `text` | selects text or versioned instance JSON | none | requested list on stdout; invalid value exits `2` | parser, schema, IPC |
| targetable `ui` actions | `--instance`, `--project` | automatic single target | selects a Desktop target by stable id or project path | ambiguous/unavailable target fails rather than guessing | requested action payload on stdout; target failure exits `5` | parser, completion, IPC |
| targetable `ui` actions | `--timeout` | `10s` | bounds local IPC connection/request time | finite positive duration within parser limits | timeout diagnostic on stderr and exit `5`; invalid duration exits `2` | parser, completion, IPC |
| `doctor` | `--format` | `text` | selects localized text or versioned doctor JSON | none | report on stdout; required failure exits `3` | handler, schema, process |

Commands whose public contract contains only required/optional arguments
(`profile validate`, `profile reset`, nested semantic `ui` actions, and
`completion`) have no additional command-local options beyond this matrix and
their command definitions above. Help and context-aware completion are generated
from the same command model, and contract tests fail when an option is added
without metadata or observable coverage.

## stdout and stderr

stdout is the payload channel:

- analysis and context documents;
- one real absolute result path after a file, folder, or ZIP was created;
- an accepted project path from `open`;
- requested Desktop control payloads;
- help, version, and completion scripts.

stderr is the operational channel:

- progress and status;
- warnings and diagnostics;
- migration guidance;
- errors and hints;
- dry-run plans.

Direct commands never prompt. JSON and XML stdout never contain ANSI, progress,
spinner frames, tables, warning lines, result paths, or summaries.

`--dry-run` performs source planning plus destination safety and conflict
validation, creates no file, directory, ZIP, staging path, or parent directory,
and leaves stdout empty. A localized readiness summary uses stderr. For stdout
destinations, dry-run validates readiness but does not generate the document.
The dry-run readiness line is the explicitly requested preflight result, so
`--verbosity quiet` and `minimal` keep that one stderr line while suppressing
incidental status and progress.

An expected downstream pipe closure is not reported as an application crash. It
produces no stack trace or DPX error and exits successfully so bounded consumers
such as `head` remain usable.

## Color, Plain, Unicode, and Progress

`--color never` disables ANSI color but does not change document Unicode.

`--plain` is the strictest human-output mode. For direct commands it guarantees:

- no ANSI or markup;
- no animations;
- no emoji or decorative symbols;
- no Unicode box-drawing;
- ASCII, line-oriented human output.

An interactive TUI necessarily uses terminal cursor, input-mode, and screen-buffer
control sequences. In TUI `--plain` disables color styling, motion, decorated
frames, Unicode box-drawing, emoji, and ornamental glyphs; the visible layout is
monochrome and uses ASCII structure. Localized text, project content, and file
names remain Unicode and are never transliterated or corrupted merely to satisfy
plain mode. PTY restoration tests validate cursor visibility, alternate-screen,
bracketed-paste, and mouse-mode restoration before a live parent shell accepts
fresh input.

JSON and XML schemas do not change in plain mode. Text and Markdown tree sections
use ASCII tree glyphs when plain mode is requested.

Color precedence is:

1. `--plain`;
2. an explicit `--color` value;
3. a non-empty `NO_COLOR` value;
4. automatic terminal detection.

`NO_COLOR=` is treated as unset. `--plain --color always` is a usage error.

`TERM=dumb` disables ANSI, interactive progress, and TUI startup. Direct commands
remain usable. stdout and stderr capabilities are evaluated independently.
Automatic progress animates only when stderr is interactive and CI is not active.
`--plain --progress always` emits bounded static ASCII progress lines on stderr;
plain `auto` remains suppressed. Explicit `--progress always` also falls back to
bounded static lines when stderr is redirected or `TERM=dumb`, even if
`--color always` was requested. `quiet` and `minimal` suppress optional progress
even when `always` is requested.

## Destination and Atomicity

Analysis reports, context files, portable profile files, project folders, and
project ZIP files use one canonical destination-safety policy:

- destination equal to or inside the canonical source is rejected;
- a symlink or junction resolving into the source is rejected;
- case-only aliases resolving into the source on a case-insensitive volume are
  rejected without imposing case-insensitive semantics on case-sensitive volumes;
- filesystem aliases such as a substituted drive, bind mount, or macOS firmlink
  are compared in their physical filesystem namespace; on Linux, mounted
  filesystems visible below the source root are protected boundaries as well;
- an existing destination conflicts with exit code `4` unless replacement is
  explicitly allowed; an existing dangling symbolic link is also a conflict;
- the destination parent must already exist;
- staging is adjacent to the destination;
- file outputs resolve and revalidate the destination parent before staging and
  before the final move;
- a successful replacement is atomic where the platform filesystem supports it;
- cancellation or failure performs bounded staging cleanup; if the operating
  system continues to deny deletion, the command returns a runtime failure that
  identifies cleanup as incomplete instead of reporting clean cancellation;
- the source tree is unchanged.

Stable aliases, hard links, case aliases, and observed destination retargets are
validated fail-closed. v1 does not claim atomic protection against a privileged or
hostile process that mutates the already validated filesystem namespace in the
remaining interval before a path-based platform commit.

After a successful commit, DevProjex reports the absolute destination spelling
requested by the user while it still resolves to the committed physical entry.
If an alias was retargeted during the operation, the validated physical path is
reported instead. Physical paths remain an internal safety boundary and do not
replace stable user-visible paths merely because the operating system exposes an
equivalent alias such as macOS `/var` → `/private/var`.

Context, analysis, and portable-profile file outputs never create a missing
parent directory. Dry-run creates nothing.

Folder export creates exactly the requested directory and requires it to be
absent. ZIP, context, and portable-profile replacement require `--force`.

## Streaming Context Documents

Complete context documents are written incrementally to a writable destination.
stdout may be non-seekable. File output streams into an adjacent temporary file
and then moves or replaces it atomically. The CLI handler never materializes a
complete project context as one byte buffer or one managed string.

Without `--hide-secrets`, exact document generation validates and decodes each
included text file in bounded chunks; it does not retain either the complete
document or a complete currently processed file as a managed string. With
`--hide-secrets`, each selected text file is classified and inspected once, bounded
to 16 MiB, then written to a per-operation snapshot consumed by the serializer.
The complete document is never retained in either mode. Binary bytes are never
embedded in text, Markdown, JSON, or XML context documents.

## Cancellation and Signals

- The first interrupt cancels active direct work or the active TUI operation.
- Direct cancellation returns `130`, writes one concise localized diagnostic to
  stderr, and removes file-output staging. A streamed stdout destination cannot
  be rolled back and may contain the document prefix already accepted by the
  downstream consumer; cancellation never appends error text to that payload.
- TUI cancellation returns to a usable workspace when an operation is active.
- SIGINT, SIGTERM, and applicable SIGHUP requests use the same cancellation path.
- A repeated interrupt may terminate the process after cleanup had an opportunity
  to start.
- Child Git processes receive cancellation and are not intentionally orphaned.

Terminal restoration is protected by `finally` paths on normal exit, cancellation,
and unhandled failure. DevProjex restores the terminal presentation modes it
changes, including cursor visibility, colors and styles, mouse tracking,
bracketed paste, and the alternate screen. A normal TUI exit must return to a
usable parent shell with echo, canonical line input, and interrupt handling.

Input delivered before the TUI process has completed belongs to the active TUI
session; it may be consumed and is not guaranteed to be preserved or replayed
to the parent shell. DevProjex does not explicitly flush unread operating-system
input during teardown. Native macOS release gates cover published startup,
keyboard navigation, resize, normal exit, and observable parent-shell usability.
Extended `Ctrl+Z` and job-control lifecycle behavior is not part of the certified
v1 beta contract.

## Legacy Migration

The flat CLI was experimental and is not a stable public contract. DevProjex does
not execute it.

Only explicitly recognized and unambiguous legacy action/value shapes before
`--` trigger migration. Both `--option value` and `--option=value` are
recognized. Missing values, unsupported values, duplicated non-repeatable
options, unrelated tokens, similar names, and all data after `--` never produce
a guessed migration.

Migration writes a structured replacement argument vector to stderr and exits `2`;
stdout remains empty. The presentation does not claim that one quoting algorithm
is portable across PowerShell, CMD, Bash, Zsh, and Fish.

## Exit Codes

| Code | Meaning |
|---:|---|
| 0 | Success, help, version, or expected downstream pipe closure |
| 1 | Runtime or I/O failure |
| 2 | Invalid command, option, value, or combination |
| 3 | Policy, readiness, or required doctor check failure |
| 4 | Destination conflict |
| 5 | Desktop target unavailable or ambiguous |
| 130 | Canceled |

Human errors contain a stable `DPX-*` code. Diagnostic verbosity may add safe
details but never changes the exit code.

## Machine Documents

New public JSON documents use:

- integer `schemaVersion`;
- stable `kind`;
- stable English property names;
- canonical lowercase enum tokens;
- `/` separators in machine paths;
- UTF-8 without BOM;
- no ANSI or localized identifiers.

The v1 kinds are:

```text
devprojex-analysis
devprojex-context
devprojex-doctor
devprojex-profile
devprojex-ui-instances
```

Newly written portable profiles include `kind: "devprojex-profile"`. Readers
continue to accept schema-v1 profiles created before the kind discriminator was
added, but reject any explicit conflicting kind.

Context XML uses `devprojexContext`, numeric text `schemaVersion="1"`, and
`kind="devprojex-context"`. Its XML declaration reports UTF-8. Generated JSON and
XML must parse with standard parsers.

Analysis v1 contains inventory, effective selection, metrics, diagnostics, and
fingerprint. Timings are not part of the stable v1 analysis schema.

## Stable After v1

The following are stable after v1:

- public command hierarchy;
- command and option names;
- canonical choice tokens;
- option meaning, defaults, conflicts, and repeatability;
- selection and profile precedence;
- stream ownership;
- exit-code meanings;
- destination and dry-run semantics;
- machine schema versions, kinds, and existing required properties;
- supported terminal entry-point contract.

Localized wording, rich interactive layout, additive machine properties, and
hidden maintainer commands may evolve without redefining the public syntax.
Breaking changes require an explicit major-version contract and migration path.
