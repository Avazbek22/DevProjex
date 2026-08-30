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
├── mcp
├── open
├── analyze
├── tree
├── export
│   ├── context, ctx
│   └── project, proj
├── profile
│   ├── show
│   ├── export
│   ├── import
│   ├── validate
│   ├── reset
│   └── save
├── recent
├── cache
│   ├── path
│   ├── list
│   ├── remove
│   ├── clear
│   └── update
├── ui
│   ├── list
│   ├── status
│   ├── activate
│   ├── preview
│   ├── tree
│   ├── filter
│   └── search
├── doctor
├── help
├── completion
└── dev
    ├── benchmark
    │   ├── analysis
    │   └── ui
    └── session
```

`dev` is a hidden maintainer namespace. See `CONTRIBUTING.md` for its supported
diagnostic workflows.

`devprojex mcp [--root PATH ...] [--hide-private-data] [--allow-remote]` starts the local
read-only MCP stdio server. Secret redaction is mandatory; private-data
redaction is enabled only by the server startup flag and cannot be controlled by
tools. Remote Git URL project arguments are disabled by default; `--allow-remote`
enables RepoCache-backed clone/acquire for MCP project tools without changing
the local roots returned by `list_projects`.
Explicit MCP roots take precedence over `DEVPROJEX_ROOT`, then
`CLAUDE_PROJECT_DIR`, then the current directory. See
[McpServer.md](McpServer.md) for its security model, tools, and client
configuration.

Commands, option names, enum tokens, JSON properties, and XML element names are
stable English identifiers. `--language CODE` localizes human-readable help,
status, diagnostics, and Terminal Workspace labels.

## Version

```shell
devprojex --version
devprojex -v
```

`--version` and `-v` are equivalent. They write the user-facing DevProjex version
to stdout and exit with code `0` without opening Desktop or Terminal Workspace.

## Common Selection Options

Six commands accept the same typed path-selection options, through `--exclude`
in the list below: `analyze`, `tree`, `export context`, `export project`, `open`,
and `profile save`. All except `tree` also accept the five
content-transformation options that follow. `open` additionally accepts the
`auto` profile:

```text
--profile <standard|local|FILE>
--root <PATH>                 repeatable
--extension <EXT>            repeatable
--select <RELATIVE_PATH>     repeatable
--select-from <FILE|->
--git-mode <MODE>
--exclude <NAME>             repeatable
--max-file-bytes <SIZE>      analyze, tree, and export context only
--hide-secrets [<true|false|on|off>]
--hide-private-data [<true|false|on|off>]
--compress-code [<true|false|on|off>]
--strip-comments [<true|false|on|off>]
--strip-blank-lines [<true|false|on|off>]
```

Boolean transformation values also accept `on|off`. Each has an explicit
negative form: `--no-hide-secrets`, `--no-hide-private-data`,
`--no-compress-code`, `--no-strip-comments`, and
`--no-strip-blank-lines`. Short aliases are `-p` profile, `-r` root,
`-e` extension, `-s` select, `-x` exclude, and `-b` branch where they do not
conflict with an existing command option.

`--max-file-bytes SIZE` is an invocation-only narrowing filter for `analyze`,
`tree`, and `export context`. Files strictly larger than SIZE are removed after
all profile, ignore, Git, and explicit path filters; a file exactly SIZE bytes is
kept. SIZE may be a byte count or use a case-insensitive binary suffix:
`k|kb|kib`, `m|mb|mib`, or `g|gb|gib`, all with a 1024 multiplier. The filter is
not stored by `profile save` and does not change the profile format.

For `open`, the first line is `--profile <auto|standard|local|FILE>` and its
default is `auto`. Direct selection commands (`analyze`, `tree`, both exports,
and `profile save`) default to `standard`.

Git filtering is independent from ordinary Exclusions.

Git modes:

| Token | Behavior |
|---|---|
| `none` | No Git-based filtering |
| `gitignore` | Respect applicable hierarchical `.gitignore` rules |
| `tracked` | Include only paths returned from applicable indexes by the installed Git CLI; no readable index fails closed with exit `3` |
| `staged` | Include files with staged changes |
| `changes` | Include staged, unstaged, and untracked files; ignored untracked files remain excluded |
| `diff:<REF>..<REF>` | Include files changed between two Git references |

`off` is accepted as an input alias for `none`. Help, completions, and machine
documents continue to use the canonical `none` token.

The three state scopes are invocation-only and cannot be saved in local or
portable profiles. `open` supports `staged` and `changes`; diff scopes are
available in direct CLI commands, Terminal Workspace, and MCP, but not Desktop.
The Git scope narrows the effective profile selection before file-size limits.
Smart Ignore, Exclusions, explicit selected paths, extensions, and globs still
apply. Selected content always comes from the current working tree, including
when a staged file has newer unstaged edits.

Deleted paths and rename sources do not enter the package because they no longer
exist in the working tree. DevProjex reports their count with
`DPX-GIT-STATE-DELETED`. A non-repository project, unavailable Git executable,
or invalid diff reference fails closed with `DPX-GIT-STATE-UNAVAILABLE` and exit
`3`.

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

`--hide-private-data` is another independent, opt-in transformation. It detects the
phase-one email, global IP, local-user path, MAC-address, and international-phone
rules in selected text files. An explicit `false` overrides a portable profile.
For `analyze` and context output, it also covers generated human-readable content,
including tree text and content headings. Machine metadata such as `project.root`
remains the absolute project address so automated consumers can identify the source.
No findings is not a privacy guarantee. See
[HidePrivateData.md](HidePrivateData.md).

`--compress-code` is also independent from path selection and is off in the
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

`--strip-comments` is the third independent content transformation and is off in the
`standard` profile. It removes syntax-tree comment nodes from supported code files; Python
module, class, and function docstrings are removed as documentation as well. A shebang at
the beginning of a script remains intact. Comment-like text inside strings, interpolation,
heredocs, compiler directives, attributes, and annotations is not treated as a comment.
Directive comments such as `// eslint-disable`, `// @ts-ignore`, and `# type: ignore` are
removed because this output is intended for reading rather than compilation.
Comment removal supports the 14 body-compression languages plus HTML (`.html`, `.htm`), CSS
(`.css`), TOML (`.toml`), Bash (`.sh`, `.bash`), XML-family project files, and YAML (`.yml`,
`.yaml`), for 20 language packs in total. The six additional packs are comments-only: enabling
`--compress-code` alone keeps them on the unsupported fast path, while `--strip-comments` enables
their syntax-aware processing. XML-family coverage includes `.xml`, `.xaml`, `.axaml`,
`.csproj`, `.props`, `.targets`, `.vbproj`, `.fsproj`, `.nuspec`, `.config`, and `.resx`;
SVG assets are deliberately excluded. XML CDATA, declarations, processing instructions, and
DOCTYPE content remain intact. YAML block scalars, strings, anchors, tags, and document markers
remain intact. HTML comments are removed, but JavaScript and CSS comment text inside HTML
`script` and `style` raw-text nodes remains intact.
Blank and whitespace-only lines immediately adjacent to removed full-line comments collapse to
at most one between retained content blocks and to none at file boundaries. Unrelated blank lines
remain byte-for-byte unchanged; `--strip-comments` does not reformat the rest of the file.

`--strip-blank-lines` is the fourth independent content transformation and is off in the
`standard` profile. It removes every whitespace-only source line, including leading and
trailing runs, while preserving the final newline of the last content line. A blank line
inside a multiline syntax-tree leaf remains byte-for-byte complete. This protects multiline
strings, raw and template literals, heredocs, YAML block scalars, and multiline comments when
comment removal is disabled. The same grammar-only rule applies to all 20 packs. In XML and
HTML, whitespace inside text nodes is character data and therefore remains; blank separators
between separate markup nodes may still be removed. No language-specific text heuristics are
used.

Compression, comment removal, and blank-line removal are independent and share one syntax
analysis and one validated edit plan:

| Enabled transformations | Result |
|---|---|
| None | Original file bytes |
| `--compress-code` | Signatures and declarative state; comments and docstrings remain |
| `--strip-comments` | Complete implementation code without comments or docstrings |
| `--strip-blank-lines` | Complete code and comments without unprotected blank lines |
| `--compress-code --strip-comments` | A bare declaration skeleton without comments or docstrings |
| `--compress-code --strip-blank-lines` | Declaration skeleton with comments and no unprotected blank lines |
| `--strip-comments --strip-blank-lines` | Complete implementation without comments or unprotected blank lines |
| All three | Bare declaration skeleton without comments, docstrings, or unprotected blank lines |

When either redaction option is enabled, syntax edits are applied first and detection runs
over that exact transformed text. When secret and private-data spans overlap, the secret
finding wins. Unsupported languages such as Markdown remain
byte-for-byte complete. Source files are never modified by any combination.

Modern local profiles retain checked and unchecked states across roots, extensions,
and Exclusions. Newly discovered rows use current defaults in Desktop, CLI, and TUI;
explicit CLI collections remain exact and invocation-only.

Selected paths are relative to the project root. A file selects that file; a
directory selects its effective subtree. Parent/child overlaps are deduplicated.
An empty selected-path set means the complete effective tree. Absolute paths,
`..`, and link-based escapes are rejected.

`--select-from FILE` reads strict UTF-8 source-relative paths, with an optional
UTF-8 BOM, one per line; UTF-16 and UTF-32 inputs are rejected and empty lines
are ignored. The 16 MiB limit is enforced while the stream is read, including if
the file grows after opening. `--select-from -` reads redirected stdin and fails
immediately when stdin is an interactive terminal. Its entries are combined with repeatable
`--select`, deduplicated with the project filesystem path semantics, and replace
profile path selection as one explicit override. Input is limited to 100,000
non-empty entries and 16 MiB.

## Repository URL Sources

`tui`, `open`, `analyze`, `tree`, `export context`, and `export project` accept either a
local project directory or a Git repository URL as `PROJECT`. URL sources use the
same managed clone cache and operation leases as Desktop. `--branch NAME` selects
a validated branch for a URL source and is rejected for local paths and with
`open --last`. An existing local directory takes precedence over the SCP-like URL
heuristic, so legal Unix and macOS names containing `:` remain local; an explicit
`scheme://` source is always treated as a URL. The lease is
held for the complete operation and released afterward; generated cache paths are
never reported. Terminal Workspace repository details show the safe repository URL
and cache metadata without exposing the physical checkout path.

```text
devprojex export context https://github.com/owner/repo -o -
```

A successful first clone is added to repository history. Later invocations reuse
the complete cached checkout and can work offline. Clone progress follows
`--progress`, `--verbosity`, and `--plain`: an interactive stderr reuses one line,
while redirected, CI, dumb-terminal, and plain output is limited to start, three
percentage milestones, and completion. It never enters stdout. Cancellation cleans
staging through the cache lifecycle. Profile-management commands remain local-path-only.

Common aliases are part of the public contract: `export ctx` equals
`export context`, `export proj` equals `export project`, and `-f` equals
`--format`. `-n` equals `--dry-run` where that option is available. `-q` and
`--quiet` select `quiet` verbosity and cannot be combined with an explicit
`--verbosity`.

## Terminal Workspace

```shell
devprojex
devprojex tui [PROJECT] [--branch NAME]
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
devprojex open [PROJECT|URL] [options]
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
project argument, `--branch`, or selection/profile overrides. `--filter` and `--search` are
mutually exclusive. `--view` and `--search` imply `--preview`.
Desktop accepts `none`, `gitignore`, `tracked`, `staged`, and `changes`; an
`open --git-mode diff:...` request is a usage error because diff is not a GUI
mode.

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
devprojex analyze [PROJECT|URL] [options]
```

Defaults:

- project: current directory;
- format: `text`;
- output: stdout;
- profile: `standard`.

Specific options:

```text
-f, --format <text|json>
-o, --output <PATH|->
--force
--strict
--findings
--fail-on-findings
--top-files <N>
--max-file-bytes <SIZE>
--color <auto|always|never>
--progress <auto|always|never>
--verbosity <quiet|minimal|normal|detailed|diagnostic>
--plain
```

Text is a human-readable project summary. JSON is a stable machine document with
`schemaVersion`. `--strict` still writes the report, then returns exit code `3`
when policy diagnostics exist. Analysis is already read-only and therefore has
no `--dry-run` option. A file destination must be outside the source project and
must not already exist unless `--force` is used for atomic replacement. Its
parent directory must already exist.

`--findings` adds sanitized effective findings to text and JSON: only `ruleId`,
`category` (`secret` or `private-data`), `relativePath`, and one-based
`lineNumber`. Values, source fragments, assignment context, fingerprints, and raw
detector errors are never emitted. The number of descriptors equals the combined
effective matched counts from the same inspection session. `--fail-on-findings`
writes the requested report and returns policy exit code `3` when any effective
finding exists; unlike `--strict`, it does not gate ordinary diagnostics.
Either findings option runs count-only secret detection when it was otherwise
disabled, but never changes `--hide-secrets` or the emitted content. JSON adds
`findingCount`; `findings` remains conditional on `--findings`. Text reports
redacted values only when redaction was explicitly enabled. Private-data
detection remains explicit.

`--top-files N` accepts `1..1000` and appends a localized ranking of the N
largest selected text files by estimated tokens. JSON adds `topFiles` only when
the option is present; omitting it preserves the existing text and JSON output.
The ranking reflects transformed content when compression or stripping is active.
`--max-file-bytes` narrows both the inventory and this ranking before content is
read. Text output reports the applied limit and excluded count; JSON inventory
and byte metrics reflect the narrowed selection without adding a new property.

Examples:

```shell
devprojex analyze .
devprojex analyze . --format json -o -
devprojex analyze ./app --format json -o report.json --strict
devprojex analyze . --git-mode tracked --exclude smart-ignore
devprojex analyze . --git-mode staged
devprojex analyze . --compress-code --format json
devprojex analyze . --hide-secrets --findings --fail-on-findings
devprojex analyze . --top-files 10
devprojex analyze . --max-file-bytes 1m
```

## Tree

```shell
devprojex tree [PROJECT|URL] [-f text|markdown|json|xml] [-o PATH|-] [options]
```

Output options:

```text
--color <auto|always|never>
--progress <auto|always|never>
--verbosity <quiet|minimal|normal|detailed|diagnostic>
-q, --quiet
--plain
--force
--branch NAME
```

`tree` writes the same tree payload as the shared Desktop export service and
defaults to text on stdout. It accepts profile and path-selection options,
including `--select-from`, but deliberately has no content-transformation flags.
Its `PROJECT` argument may be a local directory or repository URL. `-q` selects quiet verbosity and
cannot be combined with an explicit `--verbosity`; `--plain` conflicts with
`--color always`.

Text trees write the project path once and then start directly with its real
children. They do not repeat the project name as a synthetic top-level node.
`--plain` changes only the connectors to their ASCII equivalents.

For file output, the destination must be outside the source project, its parent
directory must already exist, and an existing destination requires `--force`.
Replacement is atomic. On success, stdout contains the tree document for `-o -`
or one absolute committed path for file output; operational output stays on stderr.

## Export Context

```shell
devprojex export context [PROJECT|URL] [options]
```

Defaults:

- view: `tree-content`;
- format: `markdown`;
- output: stdout;
- profile: `standard`.
- token budget: unlimited.

Specific options:

```text
--view <tree|content|tree-content>
-f, --format <text|markdown|json|xml>
-o, --output <PATH|->
--force
-n, --dry-run
--max-tokens <N>
```

The format applies to the entire document. JSON and XML are parseable structured
documents; Markdown contains headings, a fenced tree, and fenced text-file
content. Binary bytes are never embedded in context output. Machine documents
mark binary entries with metadata.

For `--view content`, human-readable text and Markdown write one `Root: ...`
line followed by project-relative file headings. A repository URL source uses its
safe URL for the Root line instead of the managed checkout path. `tree-content`
keeps relative content headings. JSON and XML keep their existing machine root
and file-path representation.

`--max-tokens N` limits included file content to an estimated token budget of at
least 1. The estimate is one token per four transformed characters, rounded up
per file. Files are considered in deterministic path order; a file that does not
fit is reported as skipped, while later files are still considered. An empty
content result is valid. Tree text, headings, and format markup are outside the
budget. Compression and stripping are applied before estimates are calculated,
so `--compress-code` can fit more supported source files into the same budget.
Whenever the option is present, stderr reports included and skipped counts and
estimated tokens, lists up to 25 largest skipped files, and suggests how to fit
more. JSON and XML also include an additive `tokenBudget` object; text and
Markdown payloads remain unchanged apart from omitted file sections.
In JSON and XML, the existing `metrics` object and tree continue to describe the
complete effective selection, while `files` and `tokenBudget` describe the
content admitted by the budget.

With `--hide-secrets` or `--hide-private-data`, detector and budget failures fail closed and
produce no complete output artifact. A text file above the supported scan limit or in an
unsupported encoding does not abort the remaining analysis: its content is withheld from context
and project-copy output, and stderr lists every affected relative path with the reason and total
count. `export project` also records the same omissions in `DEVPROJEX-NOTICE.txt`. This
continue-with-withholding behavior never emits undecoded or only partially inspected source text.

When output is stdout, stdout contains only the context document. When output is a
file, stdout contains one absolute result path. Existing files are conflicts
unless `--force` is used; replacement is atomic. The destination parent directory
must already exist.

`--force` is valid only for a file destination, never for stdout. `--dry-run`
performs planning and destination preflight but does not generate a document,
create an artifact, or print a result path. Its operational plan is written to
stderr.
With `--max-tokens`, dry-run performs the same transformed-content forecast and
writes the same budget report without generating the document.

Examples:

```shell
devprojex export context .
devprojex export context . --view tree --format json -o -
devprojex export context . --view content --format xml -o ../devprojex-context.xml
devprojex export context . --format markdown -o ../devprojex-context.md --force
devprojex export context . --hide-secrets --format markdown -o ../devprojex-redacted.md
devprojex export context . --hide-private-data --format markdown -o ../devprojex-private.md
devprojex export context . --compress-code --format markdown -o ../devprojex-compact.md
```

## Export Project

```shell
devprojex export project [PROJECT|URL] --as <folder|zip> -o <PATH|-> [options]
```

The destination is exact:

```shell
devprojex export project . --as folder -o ../devprojex-submission
devprojex export project . --as zip -o ../devprojex-submission.zip
$ devprojex export project . --as zip -o - > devprojex-submission.zip
devprojex export project . --compress-code --as zip -o ../devprojex-compact.zip
```

The first command creates exactly `../devprojex-submission`; it does not create an
additional project-name child or `(2)` suffix. The folder must not exist. A ZIP path must end
in `.zip` and must not exist unless `--force` is supplied. `--force` is not valid
for folder exports. In both cases the destination parent directory must already
exist.
For ZIP only, `-o -` streams raw ZIP bytes to stdout; folder output cannot use
standard output.

Folder and ZIP exports preserve selected binary bytes, timestamps, directory
structure, and included empty directories. Staging is cleaned after cancellation
or failure. Canonical destination checks reject destinations equal to or inside
the source, including paths reached through symlinks or junctions.

With `--hide-secrets` or `--hide-private-data`, detected values in text files are replaced. Binary files remain
unchanged. The result is intentionally not byte-for-byte faithful and may not
build or run. `--dry-run` states this before any destination or staging path is
created.

On success, file and folder destinations write exactly one absolute result path
to stdout. A ZIP destination of `-` writes only the raw archive bytes instead.
Measured progress and warnings use stderr.

## Recent Workspaces

```shell
devprojex recent [--kind all|folder|repository] [--limit N] [-f text|json]
```

`recent` is read-only and preserves the ordering used by the GUI, newest first.
Folders come from the 32-entry local-project history and repositories from the
16-entry clone-URL history. JSON uses `schemaVersion: 1`, kind
`devprojex-recent`, and stable `kind`, `path`/`url`, `name`, `parent`, and
`lastOpened` fields.

## Git Clone Cache

```shell
devprojex cache path
devprojex cache list [-f text|json]
devprojex cache remove URL [-y|--yes|--force] [-n|--dry-run] [-f|--format text|json]
devprojex cache clear [-y|--yes|--force] [-n|--dry-run] [-f|--format text|json]
devprojex cache update URL
```

`cache path` prints the same managed Git clone cache root reported by `doctor`.
`cache list` reports the repository URL, state, branch, commit, local path,
approximate size, and last-use timestamp. Its JSON document uses
`schemaVersion: 1` and kind `devprojex-repository-cache`. If an index root cannot
be read because its lock is busy or its schema is newer than this application,
the available entries are still emitted, a localized warning goes to stderr, and
the command returns policy exit code `3`. JSON adds `"incomplete": true` only in
that case; a complete result omits the field.

Actual removal is non-interactive and requires `--force` or `--yes`; dry-run
requires neither confirmation flag. The result reports
removed, retained, and failed entries. A live repository lease is retained; any
retained or failed entry returns policy exit code `3`, so scripts cannot mistake a
partial cleanup for complete success.
`cache remove` and `cache clear` also accept `-y`/`--yes`, `-n`/`--dry-run`,
and `--format json`. Dry-run reports the same counters and bytes without deleting.
The JSON result has `schemaVersion: 1`, kind `devprojex-cache-removal`, `dryRun`,
`removed`, `retained`, `failed`, and `bytes`. If `cache remove` cannot find the
requested URL, text mode writes the localized diagnostic to stderr; JSON mode
instead writes the same versioned envelope to stdout with `notFound: true` and
zero counters/bytes, then returns usage exit code `2`. A temporarily busy cache
index is not reported as missing: text reports the busy condition, JSON adds
`busy: true`, and the operation returns runtime exit code `1`. Future-schema or
otherwise unreadable roots remain policy failures (`3`).

`cache update` prints a localized completion status only. Its managed clone path
is an internal cache detail and is not part of stdout.

## Command Help

```shell
devprojex help [COMMAND...]
devprojex help export context
```

The command form renders the same structured help as `--help` on the resolved
node. Aliases are accepted while canonical command names are shown. An unknown
command path returns usage exit code `2`.

## Profiles

```shell
devprojex profile show [PROJECT] [--profile standard|local|FILE] [-f text|json]
devprojex profile export [PROJECT] [--profile standard|local|FILE] -o FILE [--force] [-n]
devprojex profile save [PROJECT] [selection options]
devprojex profile import FILE [PROJECT] [--apply]
devprojex profile validate FILE [-f text|json]
devprojex profile reset [PROJECT]
```

`profile validate --format json` emits `schemaVersion: 1`, kind
`devprojex-profile-validation`, Boolean `valid`, and an `errors` array.

`analyze`, `tree`, `export context`, `export project`, `profile show`, and
`profile save` default to `standard`. `profile export` is the exception and
defaults to `local`. Terminal Workspace and `open` use `local` when it is valid,
then `standard`. Explicit CLI selection options override profile fields. See
[CLI-Profiles.md](CLI-Profiles.md).

Portable profile output must resolve outside the source project, including
filesystem aliases, and its parent directory must already exist. Source safety is
validated before conflicts. An existing file requires `--force` for atomic
replacement; an existing directory is always a destination conflict. On success
stdout contains one absolute committed path. `-n`/`--dry-run` performs the same
source-safety, parent, and conflict preflight without creating or changing the
profile file; its localized plan is written to stderr. Errors and diagnostics use
stderr.

## Desktop Control

```shell
devprojex ui list [-f text|json] [--timeout DURATION]
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
devprojex doctor [-f text|json]
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
option values, repeatability, conflicts, and path arguments. All four scripts
pass the shell's current working directory through the hidden completion
transport so relative path candidates are resolved in the caller's context. It
does not execute Avalonia or require another DevProjex executable. Evaluate or
install it using the shell's normal completion mechanism.

## Streams and Exit Codes

`--color`, `--plain`, `--verbosity`, and `-q`/`--quiet` are recursive root
options and may be placed on every command. Commands without optional ANSI or
diagnostic output accept and ignore values that do not affect their payload.
`--progress` remains limited to `analyze`, `tree`, and exports.

Environment defaults sit below explicit flags and above capability detection:
`DEVPROJEX_COLOR`, `DEVPROJEX_PROGRESS`, `DEVPROJEX_VERBOSITY`, and
`DEVPROJEX_LANGUAGE` accept the same tokens as their options. For the MCP server
(`devprojex mcp`), `DEVPROJEX_ROOT` supplies the root after explicit `--root`
values and before `CLAUDE_PROJECT_DIR` or the current directory. It does not
replace the `PROJECT` argument of direct commands. `NO_COLOR` is considered
after `DEVPROJEX_COLOR`.

Interactive text tables add localized headers and fit long paths to the terminal
with a middle ellipsis. Pipes and redirects retain the headerless, untruncated
text shape for script compatibility; human labels remain localized, while JSON
remains the stable machine channel.
Context/project dry-run plans include files, folders, bytes, estimated tokens,
and effective profile.

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
