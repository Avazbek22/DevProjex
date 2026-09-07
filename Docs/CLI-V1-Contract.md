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
- Destination safety is relative to the exact `PROJECT` root opened by the
  command; a destination elsewhere in a containing repository is external to a
  nested project root and is therefore allowed.
- Application-owned settings, local profiles, clone/cache storage, and runtime
  state are intentional writes outside the source tree.
- "Source tree" means the physical files and directories rooted at the opened
  project. DevProjex does not follow a source-side symlink or junction to an
  external target; that external target does not become part of the source merely
  because the user created a reverse alias to it.
- DevProjex has no telemetry.

`DevProjex.Terminal` remains a class library inside the primary application. Every
RID publish contains exactly one primary DevProjex application executable.

### v5.2 headless distribution extension

Starting with v5.2, the same `DevProjex.Terminal` application is hosted by the
desktop executable and by the `devprojex` headless executable distributed through
RID-specific NuGet tool packages and npm platform packages. For identical arguments,
environment, and inputs, the CLI, TUI, and MCP byte contracts are identical; the
headless host adds no alternate parser or command implementation.

The only additive host-capability difference is an attempted desktop launch. When
`open` cannot reuse a running desktop and the TUI action **Open desktop** is invoked,
the headless host reports `DPX-DESKTOP-NOT-INCLUDED`, explains that this distribution
has no desktop app, links to `Docs/Installation.md`, and returns exit code `5` for a
direct command. `ui ...` continues to control an already running compatible Desktop
instance over IPC. Existing `DPX-*` codes, exit meanings, output schemas, and command
grammar are unchanged.

### v5.2 dependency-facts extension

v5.2 additively introduces `devprojex related` and the MCP `related_files` tool.
Both consume the same read-only dependency-facts engine and the effective file
selection; neither can widen a profile, Git scope, exclusion set, explicit path
selection, glob selection, or file-size limit. The engine exposes evidence and
`Resolved`, `Ambiguous`, `External`, or `Unresolved` status instead of guessing a
target. Existing commands, MCP tools, error codes, and output schemas are unchanged.
The new CLI JSON document has `schemaVersion: 1` and kind
`devprojex-related-files`; see [Dependencies.md](Dependencies.md) and
[CLI-Output-Contract.md](CLI-Output-Contract.md).

## Supported Entry Points

- `devprojex` in an interactive terminal starts Terminal Workspace.
- `devprojex <command>` runs a direct command without initializing Avalonia.
- `devprojex open` opens or reuses Desktop.
- `devprojex ui` sends a semantic request to an existing Desktop instance.
- `devprojex mcp` starts the local read-only MCP stdio server without initializing Desktop or Terminal Workspace.
- `devprojex mcp --hide-private-data` enables private-data redaction for the server process. Secret redaction remains mandatory, and MCP tool schemas expose no redaction controls.
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
When DevProjex resolves a path for launching or registering itself, an existing
file named by `APPIMAGE` takes precedence over the temporary mounted process path.

## Public Command Tree

```text
devprojex
├── tui
├── mcp
├── open
├── analyze
├── related
├── tree
├── export
│   ├── context, ctx
│   └── project, proj
├── profile
│   ├── show
│   ├── export
│   ├── import
│   ├── validate
│   └── reset
├── recent
├── cache
│   ├── path
│   ├── list
│   ├── remove
│   └── clear
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
├── help
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
en ru de fr it es pt pt-pt kk tg uz zh-cn zh-tw ja ko tr uk pl vi id
```

The default is the detected application language. It affects human help,
diagnostics, status, and TUI labels only. Machine identifiers remain English.

`--help`, `-h`, `-?`, `/?`, `/h`, `--version`, and `-v` use stdout and exit `0`.
Help targets only a valid command path. An unknown token before a help token
remains a usage error instead of silently selecting a later command.

## Shared Selection

`analyze`, `related`, `tree`, `export context`, `export project`, `open`, and
`profile save` share the path-selection options through `--exclude`. `tree` and
`related` omit the content-transformation options below:

```text
--profile <standard|local|FILE>
--root <PATH>                         repeatable
--extension <EXT>                    repeatable
--select <RELATIVE_PATH>             repeatable
--select-from <FILE|->
--git-mode <MODE>
--exclude <NAME>                     repeatable
--max-file-bytes <SIZE>              analyze/related/tree/export-context only
--hide-secrets [<true|false|on|off>]
--no-hide-secrets
--hide-private-data [<true|false|on|off>]
--no-hide-private-data
--compress-code [<true|false|on|off>]
--no-compress-code
--strip-comments [<true|false|on|off>]
--no-strip-comments
--strip-blank-lines [<true|false|on|off>]
--no-strip-blank-lines
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

For `analyze` and generated context content, `--hide-private-data` covers both
selected file text and generated human-readable text such as trees and content
headings. Stable machine metadata remains addressable: `project.root` is the
absolute project path rather than a redacted display value.

`--select` and `--select-from` form one explicit selected-path override. The
latter reads UTF-8 source-relative entries, one per line, from a file or
redirected stdin (`-`), ignores empty lines, and rejects interactive stdin.
Inputs are capped at 100,000 entries and 16 MiB. Entries from both options are
combined and deduplicated with project path semantics.
Names discovered in the project tree retain exact ordinal identity, including
case-distinct siblings. On Windows, a differently cased input remains compatible
only when it resolves to one unambiguous discovered entry.
An entry that does not exist on disk is a usage error (`2`) and no output is
created. An entry that exists but is absent from the effective tree because of
Git or exclusion filtering remains a warning and the command succeeds; this
preserves intentional selection against changing filter profiles.

`--max-file-bytes` is a transient narrowing filter on `analyze`, `tree`, and
`export context`. Files strictly larger than the parsed byte limit are excluded
after all ordinary selection rules; files exactly at the limit remain selected.
SIZE is either a positive byte count or a case-insensitive binary suffix form
using `k|kb|kib`, `m|mb|mib`, or `g|gb|gib`, each with a 1024 multiplier. It is
not part of `ProjectSelectionSpec`, is never stored by `profile save`, and does
not alter portable-profile JSON.

`tree` accepts the path-selection subset (`--profile`, `--root`, `--extension`,
`--select`, `--select-from`, `--git-mode`, `--exclude`, and
`--max-file-bytes`) and intentionally omits all content-transformation options.

The documented aliases are stable syntax: `export ctx`, `export proj`, `-f` for
every public `--format`, `-n` for each public `--dry-run`, and `-q`/`--quiet`
for quiet verbosity. Aliases appear in help and completion; none are implicit
or hidden.

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

On Linux and macOS, non-regular source entries such as FIFOs, sockets, and devices
are never opened for content inspection and are reported with the `unreadable`
classification. Redacted project copies leave them out and name them in the notice;
an untransformed project copy rejects such an entry as an unavailable source instead
of attempting a potentially blocking read.

For v5 compatibility, `--exclude hide-secrets` remains parseable but is omitted
from current help and completion choices. Resolution migrates it to the separate
`selection.hideSecrets` Boolean and removes it from canonical
`selection.exclusions`. An explicit `--hide-secrets true|false` takes precedence
over the legacy token.

`--compress-code` is a separate, additive content transformation and is off in the
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
If the grammar delivery source is empty or unreadable, or a required native grammar
cannot be found or loaded, the affected source stays complete and direct analysis
or context export emits warning `DPX-COMPRESSION-UNAVAILABLE` with the delivery path
or grammar name. The warning is additive, does not change the command exit code, and
is not promoted by `analyze --strict`. Unsupported languages and parse or structural
safety failures remain separate unchanged-file outcomes.

`--strip-comments` is an independent, additive content transformation and is off in the
`standard` profile. It removes syntax-tree comments in the 14 body-compression languages plus
comments-only HTML, CSS, TOML, Bash, XML, and YAML packs, for 20 comment-capable languages in
total. The six comments-only packs remain unsupported when only `--compress-code` is enabled. It also
removes leading Python module/class/function docstrings. A shebang at byte offset zero is
preserved. Strings, heredocs, attributes, annotations, and compiler directives are not
comments; pragma comments intended for compilers or linters are removed. Compression-only
output keeps documentation, comments-only output keeps complete implementation code, and
enabling both produces the declaration skeleton. Syntax edits share one parse, plan,
application, reverse parse, and structural gate; Hide Secrets runs afterward. Unsupported
files remain complete and source files are never modified.
At each removed full-line comment site, adjacent blank and whitespace-only lines collapse to at
most one between retained content blocks and to none at file boundaries. Blank lines unrelated
to a removed comment remain byte-for-byte unchanged.

`--strip-blank-lines` is an independent, additive content transformation and is off in the
`standard` profile. It removes whitespace-only source lines in all 20 language packs. Lines
inside multiline leaf tokens remain byte-for-byte complete, including multiline strings,
raw/template literals, heredocs, YAML block scalars, and multiline comments when comments are
not removed. XML and HTML whitespace inside text nodes is character data and remains. The three
syntax transformations support all eight flag combinations and share one parse, merged plan,
application, reverse parse, and structural gate.

Every project copy that actually changes or omits content carries the reserved root file
`DEVPROJEX-NOTICE.txt`. If the selected source root already contains that file, folder and ZIP
exports fail with `DPX-EXPORT-RESERVED-NAME` rather than overwrite or duplicate it. Project
`--dry-run` announces the notice for any effective `--compress-code`, `--strip-comments`, or
`--strip-blank-lines` option, matching the real export contract.

`gitignore` mode reads regular `.gitignore` files reachable in the selected working
tree. When the selected path is below its owning repository/worktree root, the
ancestor rule chain from that root through the selected path is applied before
rules discovered below it. Repository-local `info/exclude` is read with lower
precedence than the root `.gitignore`, resolving `gitdir:` and `commondir` for
worktrees and submodules. It does not read global Git excludes or symbolic links
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

In v5.2 the existing Git modes gain an intentional behavior change: `gitignore`
reads `info/exclude`, and both `gitignore` and `tracked` treat undeclared embedded
repositories as opaque directories. Declared initialized submodules own their
rules and nested declarations recursively; parent rules do not leak inside.
Without a repository above the scan root, the first repository in each subtree
becomes an independent owner. A gitlink without a declaration remains embedded;
a declaration without a boundary remains an ordinary directory. The complete
source-resolution and ownership specification is in [SmartIgnore.md](SmartIgnore.md).
To recover the previous embedded-repository visibility, use `--git-mode none`
(or MCP `--unrestricted` to also disable ordinary exclusions). No new flag,
checkbox, token, diagnostic code, localization key, or MCP schema is introduced.

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

Git mode also accepts three momentary, invocation-only scopes: `staged`,
`changes`, and `diff:<REF>..<REF>`. `staged` selects paths with staged changes;
`changes` selects the union of staged, unstaged, and non-ignored untracked paths;
`diff` selects paths changed between the two non-empty Git references. These
scopes use an unfiltered scanner underlay for staged/diff and a `gitignore`
underlay for changes, then intersect the resulting plan with the Git state. An
explicit persistent baseline remains a ceiling and can only narrow that result.
Every other profile, Exclusion, Smart Ignore, extension, explicit path, glob, and
file-size restriction remains active. Content is always read from the current
working tree, not the index or a commit object.

CLI `--git-mode off` is an input alias for `none`, and Terminal Workspace accepts
both `set git off` and `set git none`. Help, completion, profiles, and machine
documents expose only the canonical token for their respective surface.

Deleted paths and rename sources are omitted because they have no working-tree
file and produce warning `DPX-GIT-STATE-DELETED` with their count. A non-Git
project, unavailable Git executable, failed Git state command, or invalid diff
reference produces error `DPX-GIT-STATE-UNAVAILABLE` and direct-command exit `3`.
Momentary modes are never persisted: `profile save` and portable profile writes
reject them with usage error `DPX-CLI-PROFILE-INVALID`. Desktop exposes `staged`
and `changes`, but not the payload-bearing diff scope.

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
invocation-only fields and do not rewrite the local profile.

Portable-profile root arrays are normalized at the storage boundary. Empty values
are discarded, while non-empty names retain significant whitespace because it can
be part of a valid path on Unix. Duplicates are removed before values are sorted
with effective host path semantics (case-insensitive on Windows, ordinal on Linux
and macOS), producing one deterministic root set without changing path names.

`analyze`, `tree`, `export context`, `export project`, `profile show`, and
`profile save` default to `standard`. `profile export` is the exception and
defaults to `local`. TUI and `open` default to `local` when a valid local profile
exists, otherwise `standard`.

`open --last` resolves the last project itself. It cannot be combined with a
project argument, `--branch`, or any selection override because silently
discarding those overrides is forbidden.

## Project Sources

`tui`, `open`, `analyze`, `tree`, `export context`, and `export project` accept a local
directory or a Git repository URL as `PROJECT`. URL sources are resolved through
the shared managed clone cache; no second clone mechanism exists. One cache
session lease spans the complete operation. `--branch NAME` is valid only for a
URL source, conflicts with `open --last`, and branch names are validated before
Git starts. An existing local directory wins over SCP-like source detection, so
legal Unix and macOS directory names containing `:` remain local. An explicit
`scheme://` source is always interpreted as a URL.

Cached repositories are reusable offline. A successful first clone records the
safe source in recent-repository history only for network clone sources:
`https://`, `http://`, `ssh://`, `git://`, and SCP syntax. Local paths and
`file://` sources remain valid clone sources and use the managed cache, but are
never written to recent-repository history. Cancellation removes clone staging;
network and clone failures return runtime exit `1` without opening or exporting
partial content. The generated cache path is internal and is never reported by
direct URL-source commands or Terminal Workspace repository details. In particular,
successful `open URL` writes the safe repository URL rather than the physical cache
checkout path. Every profile command, including `profile save`, accepts local
directories only.

## Command Contracts

### `tui`

```text
devprojex tui [PROJECT|URL]
  --profile <auto|standard|local|FILE>
                                      default: auto
  --screen <auto|alternate|inline>    default: stored setting, then auto
  --mouse
  --no-mouse
  --color <auto|always|never>         default: auto
  --plain
  --branch <NAME>                     URL source only
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
devprojex open [PROJECT|URL]
  --last
  --new-window
  --wait
  --preview
  --view <tree|content|tree-content>
  --tree-format <text|markdown|json|xml>
  --filter <QUERY>
  --search <QUERY>
  --branch <NAME>                     URL source only
  <shared selection options>
```

`PROJECT` defaults to the current directory. `--filter` and `--search` conflict.
`--view` and `--search` imply `--preview`. `--wait` means wait until the requested
project and state have been applied, not until Desktop closes.
`--profile auto` is accepted by `open` and resolves `local` when a valid local
profile exists, otherwise `standard`.

On success, stdout contains the accepted absolute project path for a local source
or the safe repository URL for a URL source. A generated cache path is never
reported. Operational and handoff diagnostics use stderr.

### `analyze`

```text
devprojex analyze [PROJECT|URL]
  -f, --format <text|json>            default: text
  -o, --output <PATH|->               default: -
  --strict
  --findings
  --fail-on-findings
  --top-files <N>                     range: 1..1000; absent by default
  --force                             file output only
  --branch <NAME>                     URL source only
  <shared selection options>
  <shared output options>
```

Analysis is already read-only, so it has no `--dry-run` option. `--strict` writes
the requested report first and returns `3` when policy diagnostics exist.
`--findings` adds only sanitized effective descriptors (`ruleId`, `category`,
`relativePath`, and one-based `lineNumber`). `lineNumber` identifies the line in
the original decoded source file before code compression, comment removal, or
blank-line removal; LF, CRLF, and lone CR are line boundaries. The list count
equals the combined effective matched counters from the same output session.
Descriptors are materialized only when `--findings` is requested. Values, source
text, assignment context, fingerprints, and raw detector exceptions are forbidden.
Text output places descriptors in a separate, localized three-column findings table
after the main analysis table. Plain output aligns the same columns with spaces and
never emits tab characters.
`--fail-on-findings` writes the report and returns `3` when that effective count
is nonzero; it is independent from `--strict`. Requesting `--findings` or
`--fail-on-findings` runs count-only secret detection when needed but never
changes the effective `HideSecrets` selection or redacts the emitted report.
JSON adds the optional `findingCount`; the text redacted-value row is present only
when redaction is actually enabled. Private-data detection remains opt-in.
`--force` atomically replaces an existing report file and is invalid with stdout.
`--top-files` adds a largest-text-file ranking by estimated tokens. It is absent
by default, so existing text and JSON bytes do not change unless requested. The
ranking uses effective transformed content and portable relative paths.

### `related`

```text
devprojex related <PATH>
  --project <PROJECT|URL>              default: current directory
  --direction <dependencies|dependents|both> default: both
  -f, --format <text|json>             default: text
  --branch <NAME>                      URL source only
  <path-selection options>
  <shared output options>
```

`PATH` is a project-relative seed inside the effective selection. The seed does not
narrow the allowed manifest: the command indexes files selected by the profile,
roots, extensions, explicit paths, Git mode, exclusions, and optional
`--max-file-bytes`, then gates every result against that manifest. It suppresses
self-file edges and emits dependencies, dependents, or both. Rows retain the
evidence reason, `resolved` or `ambiguous` status, candidate paths, token estimate,
and cross-scope marker. Text labels are localized; JSON is the stable document in
[CLI-Output-Contract.md](CLI-Output-Contract.md).

Unsupported seed languages return success with an empty result and
`warning[DPX-DEPENDENCY-UNSUPPORTED]` on stderr. Missing or filtered seeds use the
existing path/selection argument errors. The command is read-only, has no output-file
or content-transformation options, and never promotes an unresolved fact into a
process failure. The dependency evidence and resolution contract is specified in
[Dependencies.md](Dependencies.md).

### `tree`

```text
devprojex tree [PROJECT|URL]
  -f, --format <text|markdown|json|xml> default: text
  -o, --output <PATH|->                 default: -
  --force                               file output only
  --branch <NAME>                       URL source only
  <path-selection options>
  <shared output options>
```

Tree output uses the shared tree export serializer and is byte-identical to the
tree section exported by Desktop for the same projected tree and format. It has
no content-transformation flags. Local directories and repository URLs use the
same source/cache contract as `analyze`.

Human-readable text writes the root path once and starts directly with the real
top-level children. The project name is not repeated as a synthetic child. The
plain renderer uses ASCII connectors without changing this structure.

For text output, one canonical serializer defines fields, ordering, values, and
exactly one platform-native final line separator. Redirected stdout, `--plain`,
and file output use that
canonical text. Interactive stdout without `--plain` may use a rich presentation,
but it contains the same logical fields and values.

A tree output file must be outside the source project, its parent directory must
already exist, and an existing destination requires `--force`. Replacement is
atomic; `--force` is invalid with stdout.

### `export context`

```text
devprojex export context|ctx [PROJECT|URL]
  --view <tree|content|tree-content>  default: tree-content
  -f, --format <text|markdown|json|xml> default: markdown
  -o, --output <PATH|->               default: -
  --force
  -n, --dry-run
  --max-tokens <N>                    integer >= 1; default: unlimited
  --branch <NAME>                     URL source only
  <shared selection options>
  <shared output options>
```

`--force` is valid only for a file destination and performs atomic replacement.
It is a usage error with stdout.

For human-readable text and Markdown, `--view content` writes one `Root: ...`
line and project-relative file headings. Remote sources use the safe repository
URL as Root. `tree-content` retains relative content headings. Context JSON and
XML keep their existing machine root and file-path representation.

### `export project`

```text
devprojex export project|proj [PROJECT|URL]
  --as <folder|zip>                   required
  -o, --output <PATH|->               required
  --force
  -n, --dry-run
  --branch <NAME>                     URL source only
  <shared selection options>
  <shared output options>
```

Folder output creates exactly the requested, previously absent directory. It
never adds a project-name child or a numeric suffix. `--force` is invalid for
folder output. ZIP output requires a `.zip` path; `--force` permits atomic ZIP
replacement.

ZIP output also accepts `-o -` and streams raw ZIP bytes to stdout. Folder output
with `-o -` is a usage error.

When `--hide-secrets` is selected, text findings are replaced. Such a copy is intentionally not byte-for-byte faithful
and may not build or run. Binary files remain unchanged. The normal confirmation
and dry-run plan state this before writing.

### `recent`

```text
devprojex recent
  --kind <all|folder|repository>      default: all
  --limit <N>                        default: 48; range: 1..100000
  -f, --format <text|json>            default: text
```

This is a read-only projection of the 32-entry folder history and 16-entry
repository-URL history, newest first. JSON schema version 1 uses kind
`devprojex-recent` and stable `kind`, `path`/`url`, `name`, `parent`, and
`lastOpened` properties. Text output uses display-cell-aligned, space-separated
columns and renders the timestamp in local time as `yyyy-MM-dd HH:mm`; JSON retains
the full UTC ISO-8601 value. Folder/repository labels are localized in every text
mode; JSON alone retains the stable English `folder` and `repository` tokens.

### `cache`

```text
devprojex cache path
devprojex cache list [-f text|json]
devprojex cache remove <URL> [-y|--yes|--force] [-n|--dry-run] [-f|--format text|json]
devprojex cache clear [-y|--yes|--force] [-n|--dry-run] [-f|--format text|json]
devprojex cache update <URL>
```

The group manages only the shared Git clone cache. Actual destructive commands
never prompt and require `--force` or `--yes`; `--dry-run` requires neither.
Their result reports removed, retained, and failed
entries from the same index-locked operation. Leased repositories are retained;
an unavailable index lock, an unsupported future index schema, or a failed index
update is reported as a failure rather than an empty success. Unindexed cache
containers are included by `clear`. A retained or failed entry returns policy exit
`3`, never clean success. Cache listing includes both ready and damaged indexed
entries; damaged entries publish `state: "damaged"`. Cache-list JSON schema version
1 uses kind `devprojex-repository-cache` and publishes URL, state, branch, commit,
internal local path, approximate size, and last-use timestamp. Text output aligns
columns by display-cell width, renders binary sizes such as `68.2 MiB`, and uses
local `yyyy-MM-dd HH:mm` timestamps without tab characters. If any cache root
cannot be read because its index lock is unavailable or its schema is newer, the
command emits all available entries, writes a localized warning to stderr, and
returns policy exit `3`. JSON adds `"incomplete": true` only for this partial
result; complete schema-version-1 output omits that additive field.

### `help`

```text
devprojex help [COMMAND...]
```

The command resolves canonical command names or documented aliases and invokes
the same structured renderer as `--help` for that node. Unknown command paths
return usage exit `2`.

### Profiles

```text
devprojex profile show [PROJECT]
  --profile <standard|local|FILE>     default: standard
  -f, --format <text|json>            default: text

devprojex profile export [PROJECT]
  --profile <standard|local|FILE>     default: local
  -o, --output <FILE>                 required
  --force
  -n, --dry-run

devprojex profile save [PROJECT]
  <shared selection options>

devprojex profile import <FILE> [PROJECT]
  --apply

devprojex profile validate <FILE> [-f text|json]
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
  --timeout <DURATION>               default: 10s

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
completion working directory in bash, zsh, fish, and Windows PowerShell 5.1, are
internal and are not part of the public v1 syntax.

## Shared Output Options

The following options are recursive root options and are accepted by every
command (commands without ANSI or optional diagnostic output may ignore them):

```text
--color <auto|always|never>           default: auto
--verbosity <quiet|minimal|normal|detailed|diagnostic>
                                      default: normal
-q, --quiet                           alias for --verbosity quiet
--plain
```

`--progress <auto|always|never>` remains command-local to `analyze`, `related`, `tree`, and
the two export commands.

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
| all commands | `--color`, `--plain` | `auto`, off | selects ANSI color policy or strict plain presentation where a command has human rendering | `--plain --color always` exits `2`; commands without ANSI output accept and ignore the effective policy | requested payload stays byte-clean on stdout | parser, rendering, process |
| all commands | `--verbosity`, `-q`/`--quiet` | `normal` | controls optional operational stderr; either quiet alias selects `quiet` | a quiet alias conflicts with explicit `--verbosity`; errors are never suppressed | requested payload stays on stdout; invalid value or conflict exits `2` | parser, help, completion, process |
| `tui` | `--profile` | `auto` | selects `standard`, `local`, or a profile file before building the workspace plan | `auto` is TUI/open-only | interactive screen; invalid/unresolved value exits `2` | parser, workspace, help, completion |
| `tui` | `--screen` | stored setting, then `auto` | overrides alternate/inline behavior for this invocation only | never persists the override | terminal screen; invalid value exits `2`; state restores on every exit path | parser, settings, PTY lifecycle |
| `tui` | `--mouse`, `--no-mouse` | capability-based auto policy | enables or disables input handling for this session | mutually exclusive | terminal screen; conflict exits `2`; tracking restores on exit | parser, workspace, PTY lifecycle |
| `tui` | `--color`, `--plain` | `auto`, off | selects color policy or strict plain presentation/export rendering | `--plain --color always` exits `2` | terminal screen; no payload stream repurposing | parser, presentation, plain PTY |
| `open` | `--last` | off | resolves the most recent workspace | conflicts with `PROJECT` and every selection override | accepted absolute path on stdout; conflict exits `2` | parser, handler, Desktop IPC |
| `open` | `--new-window` | off | requests a new Desktop instance | none | accepted local path or safe source URL on stdout; handoff failures use stderr and `5` | handler, Desktop IPC |
| `open` | `--wait` | off | guarantees that the requested project/state is applied before return | does not wait for Desktop termination | accepted local path or safe source URL on stdout; timeout uses stderr and `5` | handler, Desktop IPC |
| `open` | `--preview`, `--view`, `--tree-format` | closed; no explicit view/format | opens preview and applies its typed view/tree format | `--view` implies preview | accepted local path or safe source URL on stdout; invalid value exits `2` | parser, handler, Desktop IPC |
| `open` | `--filter`, `--search` | absent | applies the requested Desktop filter or preview search | mutually exclusive; search implies preview | accepted local path or safe source URL on stdout; conflict exits `2` | parser, handler, Desktop IPC |
| analyze/related/tree/context/project/open/profile-save | `--profile` | `standard`; `open`: `auto` | resolves `standard`, `local`, or a portable profile before explicit overrides | `auto` is accepted only by `open`; conflicts with `open --last`; a misspelled simple token is rejected instead of being treated as a path | requested payload/path stays on stdout; unresolved profile exits `2` | parser, resolver, handler, process |
| analyze/related/tree/context/project/open/profile-save | `--root` | profile roots | replaces the profile root set with each repeated top-level relative path | repeatable; conflicts with `open --last`; invalid/out-of-source path exits `2` | requested payload/path stays on stdout | parser, resolver, handler, process |
| analyze/related/tree/context/project/open/profile-save | `--extension` | profile extensions | replaces the profile extension set with each repeated normalized extension | repeatable; conflicts with `open --last` | requested payload/path stays on stdout | parser, resolver, handler, process |
| analyze/related/tree/context/project/open/profile-save | `--select`, `--select-from` | profile selected paths | combines direct paths with strict UTF-8 file/redirected-stdin entries into one explicit path override | optional UTF-8 BOM is accepted; UTF-16/UTF-32, interactive stdin, oversized input, physically missing or invalid/out-of-source paths, and `open --last` fail with exit `2`; existing paths removed from the effective tree produce a warning and success; the byte limit is enforced during reading | requested payload/path stays on stdout | parser, reader, resolver, process |
| analyze/related/tree/context/project/open/profile-save | `--git-mode` | profile Git mode | replaces the profile mode with `none`, `gitignore`, `tracked`, `staged`, `changes`, or `diff:<REF>..<REF>`; accepts `off` as an input alias for `none` | conflicts with `open --last`; momentary modes require a Git repository and are not persistable; Desktop rejects `diff` | on unavailable Git state, `analyze` preserves its requested report; related/tree/context/project/open/profile-save create no artifact and emit no success payload; machine output uses canonical `none`; diagnostic uses stderr and exit `3` | parser, resolver, Git process, handler |
| analyze/related/tree/context/project/open/profile-save | `--exclude` | profile exclusions | replaces the path-exclusion set with repeated typed values | repeatable; `none` conflicts with every other value; conflicts with `open --last` | requested payload/path stays on stdout; invalid value exits `2` | parser, resolver, handler, process |
| analyze/related/tree/context | `--max-file-bytes` | absent | removes otherwise selected files strictly larger than SIZE | positive bytes or binary `k|kb|kib`, `m|mb|mib`, `g|gb|gib`; invocation-only and never persisted | inventories, trees, context, metrics, and dry-run counts reflect the narrowed selection; invalid value exits `2` | parser, application filter, handler, process |
| analyze/context/project/open/profile-save | `--hide-secrets` | profile content-transformation state | independently enables or disables detected-value redaction without changing path filters | bare form means on; values are `true`, `false`, `on`, `off`; conflicts with `--no-hide-secrets` and `open --last` | requested payload/path stays on stdout; inspection failure exits `1` without a complete artifact | parser, resolver, handler, process |
| analyze/context/project/open/profile-save | `--hide-private-data` | profile content-transformation state | independently enables or disables private-data redaction without changing path filters | bare form means on; values are `true`, `false`, `on`, `off`; conflicts with `--no-hide-private-data` and `open --last` | requested payload/path stays on stdout; inspection failure exits `1` without a complete artifact | parser, resolver, handler, process |
| `mcp` | `--hide-private-data` | off | enables private-data redaction for the entire server process | startup-only; tool schemas and profiles cannot alter it; secret redaction remains mandatory | stdout remains JSON-RPC-only; startup failure exits `2` | parser, MCP contract, process |
| `mcp` | `--allow-remote` | off | permits project tools to resolve Git URL sources through RepoCache | startup-only; local roots and `list_projects` remain unchanged; `branch` is URL-only | stdout remains JSON-RPC-only; tool failures use stable `DPX-MCP-*` results | parser, MCP schema, integration |
| `mcp` | `--git-mode` | standard-profile mode | selects the server baseline from `none`, `gitignore`, or `tracked` when no explicit profile is requested; accepts `off` as an input alias for `none` | startup-only; momentary modes are rejected; conflicts with `--unrestricted` | stdout remains JSON-RPC-only; startup failure exits `2` | parser, MCP contract, process |
| `mcp` | `--exclude` | MCP default set: `smart-ignore`, `empty-folders` | selects the server baseline path-exclusion set from the shared exclusion tokens when no explicit profile is requested; `none` starts with every toggle off; `default` expands to the MCP default set so a list can extend it | startup-only; repeatable; a list without `default` replaces the default set; `none` conflicts with other values; redaction toggles are rejected as unknown; conflicts with `--unrestricted` | stdout remains JSON-RPC-only; startup failure exits `2` | parser, MCP contract, process |
| `mcp` | `--unrestricted` | off | starts the widest baseline: every exclusion toggle off and the Git baseline `none`, equivalent to `--exclude none --git-mode none` | startup-only; conflicts with `--exclude` and `--git-mode`; secret redaction remains mandatory | stdout remains JSON-RPC-only; invalid combination exits `2` | parser, MCP contract, process |
| `mcp` | `--allow-agent-exclusions` | off | publishes an `exclusions` array parameter on the six selection tools (`get_tree`, `analyze`, `pack_context`, `search_project`, `related_files`, `get_file`) so the agent may set the exclusion toggles per call; the value outranks the server baseline and profile exclusions | startup-only; without the flag the parameter is absent from every schema and rejected as an unknown argument; tokens match case-insensitively, duplicates are rejected, redaction toggles never appear in the vocabulary | stdout remains JSON-RPC-only; invalid tokens are `DPX-MCP-INVALID-ARGUMENTS` | parser, MCP schema, integration |
| MCP `get_tree` | `format` | `markdown` | selects compact Markdown, drawing-character text, JSON, or XML tree output | values are `markdown`, `text`, `json`, `xml`; JSON/XML over 2,000 lines fail instead of returning a partial document | text payload remains spotlight-wrapped; invalid value is `DPX-MCP-INVALID-ARGUMENTS`, structured overflow is `DPX-MCP-PAYLOAD-TRUNCATED` | MCP schema, tree serializer, integration |
| MCP get_tree/analyze/pack_context/search_project/related_files | `git_scope` | absent | narrows the effective selection with `staged`, `changes`, or `diff:<REF>..<REF>` | cannot weaken the profile/server baseline; input is limited to 4,096 characters; non-Git projects and invalid refs fail | tool error is `DPX-MCP-PROJECT-UNAVAILABLE` for unavailable Git state or `DPX-MCP-INVALID-ARGUMENTS` for invalid input | MCP schema, integration |
| analyze/context/project/open/profile-save | `--compress-code` | profile content-transformation state | independently enables or disables syntax-aware body compression without changing path filters | `true|false|on|off`; conflicts with `--no-compress-code` and `open --last` | requested payload/path stays on stdout; unsupported or rejected files remain complete | parser, resolver, handler, process |
| analyze/context/project/open/profile-save | `--strip-comments` | profile content-transformation state | independently removes syntax-tree comments and Python docstrings without changing path filters | `true|false|on|off`; conflicts with `--no-strip-comments` and `open --last` | requested payload/path stays on stdout; unsupported or rejected files remain complete | parser, resolver, handler, process |
| analyze/context/project/open/profile-save | `--strip-blank-lines` | profile content-transformation state | independently removes unprotected whitespace-only source lines without changing path filters | `true|false|on|off`; conflicts with `--no-strip-blank-lines` and `open --last` | requested payload/path stays on stdout; unsupported or rejected files remain complete | parser, resolver, handler, process |
| `analyze` | `--format` | `text` | selects the canonical text serializer or analysis JSON | none | document on stdout or in the selected file; invalid value exits `2` | parser, serializer, handler, process |
| `analyze` | `-o`, `--output` | `-` | selects stdout or an exact report file | existing file requires `--force`; destination must remain outside the source; no dry-run | document or real absolute path on stdout; atomic conflict exits `4` | handler, destination, process |
| `analyze`, `tree` | `--force` | off | atomically replaces an existing report/tree file | invalid with stdout | success path on stdout; invalid combination exits `2` | parser, destination, process |
| `analyze` | `--strict` | off | writes the report, then treats policy diagnostics as failure | none | requested report remains intact; policy result exits `3` | handler, process |
| `analyze` | `--findings` | off | adds sanitized effective redaction descriptors | values, source fragments, fingerprints, and raw detector errors are forbidden | report stays on stdout/file | serializer, sanitation, process |
| `analyze` | `--fail-on-findings` | off | writes the report, then gates on effective findings | independent from `--strict` | requested report remains intact; a nonzero finding count exits `3` | handler, process |
| `analyze` | `--top-files` | absent | appends the N largest selected text files by estimated tokens | range `1..1000`; ranking reflects effective transformations | optional text section or `topFiles` JSON property; invalid value exits `2` | parser, observer metrics, schema, process |
| `related` | `--project` | current directory | selects the local directory or Git URL whose effective manifest is indexed | the positional `PATH` remains the seed; `--branch` is URL-only | related-files document on stdout; invalid source exits by the existing source rules | parser, source resolver, process |
| `related` | `--direction` | `both` | emits dependencies, dependents, or both without changing the indexed manifest | values are `dependencies`, `dependents`, `both` | text or JSON payload remains on stdout; invalid value exits `2` | parser, renderer, process |
| `related` | `-f`, `--format` | `text` | selects localized text or deterministic `devprojex-related-files` JSON | values are `text`, `json` | one complete payload on stdout; invalid value exits `2` | parser, serializer, process |
| URL-capable commands | `--branch` | remote default branch | selects a validated repository branch under an operation lease | rejected for local paths and with `open --last` | ordinary command payload remains on stdout; clone/branch failure exits `1` or invalid name exits `2` | parser, resolver, Git fixture |
| analyze/related/tree/context/project | `--progress` | `auto` | selects automatic, forced, or disabled operational progress on stderr | quiet/minimal suppress optional progress; URL-source Git operations use bounded milestones when rewriting is unavailable | requested payload stays byte-clean on stdout | parser, rendering, process |
| analyze/related/tree/context/project | `--verbosity`, `-q` | `normal` | controls optional operational stderr from quiet through safe diagnostic context; `-q` selects `quiet` | `-q` conflicts with an explicit `--verbosity`; neither removes requested stdout nor suppresses errors | requested payload stays on stdout; invalid value or conflict exits `2` | parser, rendering, process |
| analyze/related/tree/context/project | `--plain` | off | selects stable ASCII decorations and line structure while preserving Unicode user text, and disables ANSI, markup, and animation | conflicts with `--color always` | machine schema and requested payload stay unchanged; conflict exits `2` | parser, rendering, process |
| `export context` | `--view`, `--format` | `tree-content`, `markdown` | selects typed document sections and serializer | none | document on stdout/file; invalid value exits `2` | parser, serializer, process |
| `export context` | `-o`, `--output` | `-` | selects streaming stdout or an exact context file | destination must be outside source | document or real absolute path on stdout | destination, streaming, process |
| `export context` | `--force` | off | atomically replaces an existing context file | invalid with stdout | success path on stdout; conflict exits `4`, invalid combination `2` | parser, destination, handler |
| `export context` | `--dry-run` | off | runs plan and destination preflight without document generation | creates no parent, staging, or output | stdout empty; one readiness plan on stderr | handler, filesystem-effects, process |
| `export context` | `--max-tokens` | unlimited | greedily limits included transformed file content by estimated tokens while preserving deterministic path order | integer `>= 1`; skipped files do not stop consideration of later files; document structure is outside the budget | document stays on stdout/file; localized budget report is written to stderr; JSON/XML add `tokenBudget` | parser, serializer, handler, process |
| `export project` | `--as` | required | selects exact folder or ZIP export | missing/invalid value exits `2` | real absolute created destination on stdout | parser, handler, process |
| `export project` | `-o`, `--output` | required | selects the exact destination | folder must be absent; ZIP path ends in `.zip`; destination outside source; `-` is valid only with `--as zip` | folder/file success returns its real absolute path; ZIP stdout is the raw archive byte stream | parser, destination, integration |
| `export project` | `--force` | off | atomically replaces an existing ZIP file | invalid for folder output and ZIP stdout | success path on stdout; invalid combination exits `2` | parser, destination, integration |
| `export project` | `--dry-run` | off | validates plan, kind, and exact destination without copying | creates no folder, ZIP, parent, or staging | stdout empty; one readiness plan on stderr | handler, filesystem-effects, process |
| `recent` | `--kind`, `--limit`, `-f`/`--format` | `all`, `48`, `text` | filters and bounds newest-first workspace history and selects text or JSON | limit range is `1..100000` | requested list on stdout; invalid value exits `2` | parser, schema, process |
| `cache list` | `-f`, `--format` | `text` | selects text or versioned cache JSON | a busy index lock or future-schema root marks the result incomplete | future-schema roots warn and exit `3`; a busy index reports runtime exit `1`; JSON adds `incomplete: true` and adds `busy: true` only for lock contention | parser, schema, process |
| `cache remove`, `cache clear` | `--force`, `-y`/`--yes` | off | authorizes non-interactive destructive cleanup | one authorization form is required unless `--dry-run` is used | text or versioned JSON counters on stdout; retained/failed entries exit `3`; a busy index exits `1` and JSON adds `busy: true`; missing remove URL exits `2`, with a JSON not-found envelope when JSON was requested | parser, leases, schema, process |
| `cache remove`, `cache clear` | `-n`, `--dry-run` | off | reports entries, counters, and bytes without deleting cache data | does not require force/yes | plan on stdout; no cache/index mutation | handler, filesystem-effects, process |
| `cache update` | `URL` | required | fetches and refreshes an existing managed clone | URL must already have a cache entry | localized status, never the internal cache path, on stdout; missing URL exits `2`, a busy index exits `1` | parser, Git cache, process |
| `profile show` | `--profile`, `--format` | `standard`, `text` | resolves a profile and selects text or profile JSON | `auto` is not accepted | document on stdout; invalid/unresolved value exits `2` | parser, profile handler |
| `profile export` | `--profile`, `-o`/`--output`, `--force`, `-n`/`--dry-run` | `local`, required, off, off | resolves an exact portable-profile export; dry-run performs the same preflight without writing | source safety precedes conflict; existing file needs force; directories always conflict | real committed absolute path on stdout, or a localized plan for dry-run; runtime write failure exits `1`, safety exits `3`, conflict exits `4` | parser, profile handler, filesystem effects |
| `profile import` | `--apply` | off | persists the validated imported profile as local state | absent means validation-only | status/path contract on stdout; invalid profile exits `2`; local-store write failure exits `1` | profile handler, persistence |
| `profile validate` | `--format` | `text` | selects localized validation text or `{schemaVersion,valid,errors}` JSON | none | report on stdout; invalid profile exits `2` after emitting the report | parser, schema, process |
| `profile save` | selection options | standard profile | resolves the supplied project selection and persists it as that project's local profile | local project directory only | saved-profile status/path on stdout | parser, profile handler, persistence |
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
- an accepted local project path or safe repository URL from `open`;
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

Human-readable file names, paths, and one-line success-path payloads are display
values rather than reversible filesystem identifiers. Carriage return, line feed,
tab, other control characters, U+2028, and U+2029 are escaped as `\\r`, `\\n`,
`\\t`, or `\\uXXXX`, so one filesystem entry cannot inject terminal control or
forge another output line. Structured JSON and XML path/name values keep their
exact machine-readable values and rely on their serializers for escaping.

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
- stable ASCII line structure and tree connectors. Localized text, file names,
  paths, and project content remain Unicode and are never transliterated.

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
3. a valid non-empty `DEVPROJEX_COLOR` value;
4. a non-empty `NO_COLOR` value;
5. automatic terminal detection.

`NO_COLOR=` is treated as unset. `--plain --color always` is a usage error.

`TERM=dumb` disables ANSI, interactive progress, and TUI startup. Direct commands
remain usable. stdout and stderr capabilities are evaluated independently.
Automatic animated progress runs only when stderr is interactive and CI is not
active. URL-source Git operations use a single carriage-return-updated line in that
mode. Redirected stderr, CI, `TERM=dumb`, and `--plain` use at most six static
milestone lines for URL-source Git progress in both `auto` and `always` modes.
`--progress never`, `quiet`, and `minimal` suppress that progress completely.
Measured project-export progress also remains a single carriage-return-updated line
in interactive monochrome modes (`NO_COLOR` and `--color never`).
All progress remains on stderr and never changes the requested stdout payload.

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

## Terminal polish additions to CLI v1

These additive changes are part of CLI v1 and do not alter existing machine
schemas unless a schema is named below.

- `--color`, `--plain`, `--verbosity`, and `-q`/`--quiet` are recursive root
  options. `--progress` remains limited to commands that report progress.
- Selection aliases are `-p`/`--profile`, `-r`/`--root`,
  `-e`/`--extension`, `-s`/`--select`, `-x`/`--exclude`, and
  `-b`/`--branch`. Aliases are omitted only where an existing symbol in that
  command would conflict. `open --tree-format` additionally accepts `--format`.
- Content-transformation values accept `true|false|on|off`. The explicit
  negative forms are `--no-hide-secrets`, `--no-hide-private-data`,
  `--no-compress-code`, `--no-strip-comments`, and
  `--no-strip-blank-lines`; a positive and matching negative form conflict.
- A simple unknown `--profile` word is rejected with the choices
  `standard`, `local`, and `FILE`; path-like values continue to resolve as
  portable profiles.
- `analyze -o FILE --force` and `tree -o FILE --force` perform atomic
  replacement. Neither command has a dry-run mode.
- `cache remove` and `cache clear` accept `-y`/`--yes` as aliases for
  `--force`, plus `-n`/`--dry-run`. Their `--format json` result has
  `schemaVersion: 1`, kind `devprojex-cache-removal`, `dryRun`, `removed`,
  `retained`, `failed`, and `bytes`. A missing `cache remove` URL adds
  `notFound: true`, keeps all counters and bytes at zero, writes no text
  diagnostic, and exits `2`. Lock contention instead adds `busy: true`, reports
  that the index is temporarily busy, and exits `1`.
- `profile export -n` validates and reports its plan without writing.
  `profile validate --format json` returns `schemaVersion: 1`, kind
  `devprojex-profile-validation`, `valid`, and `errors`. `profile save
  [PROJECT]` stores the effective selection as the project's local profile.
  `cache update URL` refreshes an existing managed clone, never creates an
  unrelated cache entry, and prints a localized status rather than its internal
  cache path.
- Context/project dry-run output includes file/folder counts, bytes, estimated
  tokens, and the effective profile.
- `export context --max-tokens N` applies a per-file transformed-content estimate
  and greedily includes every file that fits the remaining budget. It reports
  included and skipped estimates on stderr; context JSON and XML add the optional
  `tokenBudget` sibling without changing `schemaVersion` or existing properties.
- `ui list --timeout` uses the same bounded IPC timeout as other UI actions.
- Root help owns the full exit-code table; command help shows only codes that
  the command can produce. Verbosity choices are ordered `quiet`, `minimal`,
  `normal`, `detailed`, `diagnostic`. Help metavariables use `--language CODE`
  and `--exclude NAME`.

Environment defaults are evaluated after an explicit option and before
`NO_COLOR` or capability autodetection: `DEVPROJEX_COLOR`,
`DEVPROJEX_PROGRESS`, `DEVPROJEX_VERBOSITY`, and `DEVPROJEX_LANGUAGE` use the
same tokens as their options. For the MCP server, `DEVPROJEX_ROOT` is the
general counterpart of `CLAUDE_PROJECT_DIR`; explicit MCP `--root` values still
win, and the variable does not replace direct commands' `PROJECT` arguments.
Empty or invalid environment values are ignored. The effective priority is
explicit flag, matching `DEVPROJEX_*`, `NO_COLOR` where applicable, then
terminal/system autodetection.

Interactive text tables for `recent`, `cache list`, and `ui list` use localized
headers and middle-ellipsis path truncation to fit the TTY. Redirected/piped text
keeps the legacy headerless, untruncated shape while human labels remain
localized. JSON keeps stable English tokens, full commit hashes, raw byte counts,
and booleans. `cache list` shortens commits
to 12 characters only in text and adds a TTY total; `ui list` emits an empty
stdout and the no-instances diagnostic on stderr. Analysis text uses IEC sizes,
and profile text uses localized yes/no values.

## MCP remote-source addition to CLI v1

`devprojex mcp --allow-remote` is an additive, opt-in startup capability. Without
the flag, MCP project tools retain the local-only, zero-network contract. With
the flag, `get_tree`, `analyze`, `pack_context`, `search_project`, and `get_file`
accept a Git URL in `project` plus an optional URL-only `branch`. RepoCache owns
clone publication and the server pins each resolved checkout until shutdown.
`list_projects` remains the stable list of configured local roots.
Remote network URLs use HTTP(S), SSH, Git protocol, or SCP syntax and cannot
contain query strings or fragments. A `file://` source must resolve inside an
already configured local root, so this opt-in never broadens local filesystem
access.

`analyze --top-files N` is an additive CLI-v1 option with range `1..1000`.
The MCP `analyze` tool exposes the matching optional `top_files` parameter with
default `10`; both surfaces share the same bounded, deterministic ranking.

`--max-file-bytes SIZE` is an additive, invocation-only option on `analyze`,
`tree`, and `export context`. The four MCP selection tools expose the equivalent
positive integer `max_file_bytes` parameter. Both surfaces use one Application
filter and exclude files strictly larger than the limit without changing profile
schemas. Existing machine documents add no property; their inventory, byte
metrics, trees, and content reflect the effective narrowed selection.

Git-axis v2 is an additive CLI-v1 extension. Direct selection commands accept
the momentary `staged`, `changes`, and `diff:<REF>..<REF>` tokens. MCP adds the
persistent server baseline `--git-mode` and the narrowing `git_scope` parameter
on its four selection tools. Profile schemas remain unchanged and reject
momentary values.

MCP exclusions are a v5.2 extension with one deliberate default change: a
server started without exclusion flags runs with `smart-ignore` and
`empty-folders` instead of the desktop standard set, so dot-files, dot-folders,
extensionless files, hidden entries, and empty files are visible to agents by
default. Startup lines that want the pre-v5.2 view spell it out with
`--exclude` and the full standard token list. `devprojex mcp --exclude`
selects the persistent server baseline from the shared exclusion tokens plus
the MCP-only `default` token (the default set, for extending it) and applies
only when a tool does not name an explicit profile.
`devprojex mcp --unrestricted` is the widest-baseline preset, equivalent to
`--exclude none --git-mode none` and in conflict with both spelled-out flags.
`devprojex mcp --allow-agent-exclusions` additionally publishes an `exclusions` array
parameter on `get_tree`, `analyze`, `pack_context`, `search_project`, and
`get_file`; the value is the full desired toggle set, an empty array disables
every toggle, and it outranks the server baseline and profile exclusions.
Without the flag the parameter does not exist in any schema. Redaction toggles
are not part of the vocabulary on either surface. `analyze` results echo the
effective set in an `exclusions` array and `list_projects` results carry a
`baseline` object (`git`, `exclusions`, `agentExclusions`); both are required
on every server — including servers started without the exclusion flags — so
consumers that pinned the pre-v5.2 output schemas must refresh their copies.
`get_tree` and `pack_context` responses end with a trusted
`[Effective filters]` line, every selection tool adds an `[Empty selection]`
line when nothing survived the filters, and `DPX-MCP-PATH-NOT-FOUND` names the
effective filters. Glob patterns gain `{a,b}` alternatives; `!` negation and
`[...]` classes, previously matched as literal characters, are rejected with
`DPX-MCP-INVALID-PATTERN`.

MCP diagnostics are also refined in v5.2. An unknown `profile` now returns
`DPX-MCP-INVALID-ARGUMENTS` rather than `DPX-MCP-PATH-NOT-FOUND`, and MCP
messages no longer expose internal `DPX-CLI-*` codes. Stored responses start
with `Pack stored as '<id>' (<N> characters, <M> lines)`. Continuation and
truncation trailers are trusted text outside the untrusted-data block.
`get_file.path` and the `paths` accepted by `analyze` and `pack_context`
recognize Markdown-escaped names copied from the default tree format;
`max_file_bytes` appears in effective-filter diagnostics when supplied; and
wrong-case path diagnostics are platform-independent and name the spelling
listed by `get_tree`.

MCP `get_tree.format` is an additive input with `markdown` as its compact default.
The existing `text`, `json`, and `xml` tree serializers are available explicitly;
structured output that cannot fit the 2,000-line response limit fails with an
actionable tool error rather than returning invalid syntax.

MCP agent ergonomics changes four v5.2 behaviors without adding inputs or error
codes. First, `get_file` and `read_pack` clamp an `end_line` past EOF and append
a trusted range notice; `start_line` past EOF and reversed ranges keep
`DPX-MCP-INVALID-RANGE`. A caller that requires a prevalidated end must first
learn the line count and send `end_line <= N`; there is no strict-range switch.
Second, when `max_depth` is omitted and a complete human-readable tree exceeds
2,000 lines, `get_tree` returns the deepest complete depth that fits. Passing an
explicit `max_depth` restores caller-selected depth and the prior line-truncation
behavior; JSON/XML still fail rather than return partial syntax and now suggest
a fitting depth. Third, uninspected entries in MCP `analyze.topFiles` gain the
optional `uninspected: true` field, and their estimates use the same character
base as aggregate `characters` and `tokens`. Fourth, `initialize.instructions`
now carries workflow, trust-boundary, redaction, limit, and glob guidance, while
all seven tool descriptions are self-contained for tool search. Cached MCP
schemas must be refreshed for the additive `topFiles` field and new field
descriptions.

Compression readiness is also explicit in v5.2. CLI analysis and context export
add warning `DPX-COMPRESSION-UNAVAILABLE` to stderr and machine diagnostics when an
empty delivery source or a missing, incompatible, or invalid grammar prevents a
requested transformation. Safe complete output and all exit codes are unchanged,
including `analyze --strict`. MCP `analyze`, `pack_context`, and `get_file` append
trusted `[Compression unavailable] ...` text outside project data when compression
is effective; `analyze` additionally exposes optional structured
`compressionUnavailable` with `reason` and affected `languages`. Consumers that
cache the `analyze` output schema must refresh it.

Before the v5.1 output freeze, human-readable content and text-tree presentation
was aligned across Desktop, Terminal Workspace, CLI, and MCP. Content-only text
and Markdown now declare the root once and use relative file headings; text trees
declare the root once and start at real children. This changes only human-readable
presentation. Context and tree JSON/XML roots and file paths are unchanged.

Before the same freeze, Markdown tree Root values, node names, context project
headings, and content-only Root lines were hardened as literal CommonMark text:
active Markdown, HTML, and entity syntax is escaped consistently in buffered and
streaming output. Text, JSON, and XML bytes are unchanged.

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

The machine `selection.gitMode` value is one of `none`, `gitignore`, `tracked`,
`staged`, `changes`, or the complete `diff:<REF>..<REF>` token. JSON and XML keep
the payload-bearing diff token intact so separate ranges have distinct,
reproducible selection identities.

The v1 kinds are:

```text
devprojex-analysis
devprojex-recent
devprojex-repository-cache
devprojex-cache-removal
devprojex-context
devprojex-doctor
devprojex-profile
devprojex-profile-validation
devprojex-ui-instances
```

Newly written portable profiles include `kind: "devprojex-profile"`. Readers
continue to accept schema-v1 profiles created before the kind discriminator was
added, but reject any explicit conflicting kind.

Context XML uses `devprojexContext`, numeric text `schemaVersion="1"`, and
`kind="devprojex-context"`. Its XML declaration reports UTF-8. Generated JSON and
XML must parse with standard parsers.

When `export context --max-tokens` is present, context JSON and XML add an
optional `tokenBudget` sibling after `files`. It contains
`maximumEstimatedTokens`, `includedFiles`, `skippedFiles`,
`includedEstimatedTokens`, `skippedEstimatedTokens`, `largestSkippedFiles`, and
`additionalSkippedFiles`. Each largest-skipped entry contains `path` and
`estimatedTokens`. The budget sums per-file estimates after enabled content
transformations and excludes tree text, headings, and serialization markup.
The existing `metrics` object and `tree` remain pre-budget descriptions of the
complete effective selection; `files` and `tokenBudget` describe the content
admitted by the budget.

Analysis v1 contains inventory, effective selection, metrics, diagnostics, and
fingerprint. Either findings option adds `findingCount`. With `--findings`, it
also adds an ordered `findings` array whose entries
contain only `ruleId`, `category`, `relativePath`, and `lineNumber`. The one-based
line number is measured in the original decoded source before content
transformations. Timings and secret values are not part of the stable v1 analysis
schema.
When `--top-files N` is present, analysis JSON adds `topFiles` after `metrics`.
Each ordered item contains a portable relative `path` and integer `tokens`.
The property is omitted when the option is absent, preserving existing v1 bytes.

Recent JSON contains `schemaVersion`, kind `devprojex-recent`, and `items` with
stable `kind`, nullable `path`/`url`, `name`, `parent`, and `lastOpened` fields.
Cache-list JSON contains `schemaVersion`, kind `devprojex-repository-cache`, and
`items` with `url`, `state`, `branch`, `commit`, `localPath`,
`approximateSizeBytes`, and `lastUsed` fields. `state` is `ready` or `damaged`;
damaged indexed entries remain visible for explicit cache management.

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
