# Smart Ignore and Project Filtering

Smart Ignore removes generated output, dependency stores, caches, and machine-specific files from the effective project tree. It is a local, deterministic filter: it does not use an AI model, upload project data, execute project code, or modify the opened source tree.

This document describes the filtering behavior shared by the Desktop UI, Terminal Workspace, and direct CLI commands. [`CLI-V1-Contract.md`](CLI-V1-Contract.md) remains normative for command syntax, profiles, diagnostics, and exit codes.

## Filtering model

DevProjex combines four independent controls:

1. **Git filtering** chooses one mode: no Git filtering, `.gitignore`, or tracked files only.
2. **Exclusions** enable Smart Ignore and the hidden, dot-name, empty, and extensionless filters.
3. **Parameters** select file extensions. The current Terminal/CLI compatibility contract can additionally constrain root folders.
4. **Tree selection** includes or excludes individual files and subtrees.

Conceptually, all four narrow the same project inventory before preview, copy, context export, or project export:

```text
project inventory
  -> Git filtering
  -> Smart Ignore and ordinary exclusions
  -> extension selection (and optional Terminal/CLI root scope)
  -> checked tree items
  -> preview / copy / export
```

The implementation may fuse these stages to avoid redundant scans, but the effective selection must remain the same across GUI, TUI, and CLI.

## How Smart Ignore decides

Smart Ignore does not hide a collision-prone directory solely because of its name. A directory named `build`, `bin`, `out`, `vendor`, `packages`, `cache`, `pkg`, `Library`, or `target` can contain source code, so its name is only a candidate signal.

The decision uses two complementary rule layers.

### Stack-scoped rules

Project markers identify which technology owns a directory scope. Rules for that stack apply inside the nearest matching scope, not across the entire opened workspace.

Examples include:

| Stack | Representative markers | Representative candidates |
|---|---|---|
| .NET | `.sln`, `.csproj`, `.fsproj`, `.vbproj` | `bin`, `obj` |
| Frontend / Node.js | `package.json`, lock files, workspace files | `node_modules`, `dist`, `build`, framework caches |
| Python | `pyproject.toml`, requirements files, `Pipfile`, Poetry files | virtual environments and tool caches |
| JVM / Gradle / Maven / sbt | `pom.xml`, Gradle build and settings files, `build.sbt` | `target`, `.gradle`, `build`, `out` |
| Go | `go.mod`, `go.work` | `vendor`, `bin` |
| Rust | `Cargo.toml` | `target` |
| PHP | `composer.json` | `vendor` |
| Ruby | `Gemfile`, `Gemfile.lock` | `.bundle`, `vendor`, `log`, `tmp` |
| Swift / Apple platforms | `Package.swift`, `Podfile`, `Cartfile` | `.build`, `DerivedData`, `Pods`, `Carthage` |
| Dart / Flutter | `pubspec.yaml`, `pubspec.lock` | `.dart_tool`, `build` |

The table is illustrative rather than an exhaustive public blacklist. The maintained stack catalog lives in [`Infrastructure/SmartIgnore`](../Infrastructure/SmartIgnore/).

### Signature-backed artifact rules

Some generated directories are found by their own structure. Smart Ignore first performs a cheap candidate-name check and then inspects a bounded set of top-level entries for strong evidence such as:

- compiler or package-manager metadata;
- known cache layout directories;
- generated manifests and lock data;
- compiled binary headers;
- repeated package-store layouts;
- build-system-specific files.

This layer covers common dependency stores, build systems, framework caches, coverage output, IDE state, and generated directories used by additional ecosystems. Its maintained rules live in [`SmartArtifactIgnoreMatcher.cs`](../Kernel/Models/SmartArtifactIgnoreMatcher.cs).

The evidence probe is deliberately bounded and does not follow symlinks, junctions, or other reparse points. If a broad candidate cannot be proven to be generated output, Smart Ignore leaves it visible. Normal filesystem safety rules can still exclude an inaccessible path and report partial access independently.

## Scope and monorepo behavior

The nearest marked project owns its descendants. This prevents a rule from one project from leaking into an unrelated sibling or nested project.

For example:

- a frontend project can hide its own `node_modules` without affecting a same-named folder in an unrelated sibling;
- a .NET project can hide a signature-confirmed `bin` directory without hiding a source directory named `bin`;
- a nested project marker protects that nested scope from rules inherited from a different parent stack;
- monorepos can apply different rule families to frontend, backend, tooling, and documentation projects in one scan.

Markers and artifact evidence are revalidated during structural refreshes. Cached facts are an optimization, not permission to keep stale filtering results after the project topology changes.

## Git filtering is separate

Smart Ignore and Git filtering solve different problems. Smart Ignore recognizes generated artifacts; Git filtering follows repository policy or index membership.

| Mode | Behavior |
|---|---|
| `none` | Does not apply Git-based filtering. |
| `gitignore` | Applies reachable hierarchical `.gitignore` rules. |
| `tracked` | Includes only paths returned by applicable Git indexes. |

Only one Git mode can be active at a time. Ordinary exclusions, including Smart Ignore, remain independent and can be combined with any Git mode. Smart Secrets is a separate downstream content transformation: it never changes Smart Ignore or the effective tree. See [Hide Secrets](HideSecrets.md).

Desktop keeps the two Git modes as checkboxes because they are settings alongside
the other exclusions. Enabling one Git checkbox clears the other; clearing the
active checkbox leaves both off, which is the valid no-Git-filtering state. These
actions never change Smart Ignore or an ordinary exclusion. The section-wide
**All** checkbox still controls the whole section and never enables both Git modes.

### `.gitignore` mode

The `.gitignore` implementation supports nested rule scopes, negations, and ancestor rules from the owning repository or worktree root to the selected folder. An ignored directory is still traversed when a later negation may expose a descendant.

The following boundaries are intentional:

- an absent `.gitignore` is an active empty rule set, not a fallback to `none`;
- the administrative entry named exactly `.git` remains excluded while Git filtering is active;
- names such as `.github` and `.git-owned` are ordinary paths;
- `.git/info/exclude` and global Git excludes are not read;
- a symbolic link named `.gitignore` is not followed;
- an unreadable `.gitignore` scope fails closed and produces the existing partial-access diagnostic instead of silently including uncertain paths;
- an initial UTF-8 BOM is supported; UTF-16 and UTF-32 files are not reinterpreted as valid rule sources.

Repository-backed pattern comparison follows the effective Git `core.ignoreCase` value. On macOS, canonical Unicode comparison also follows `core.precomposeUnicode`. A standalone `.gitignore` outside a repository uses the host filesystem comparison policy.

### Tracked-files-only mode

Tracked mode obtains index membership through the installed Git CLI. DevProjex does not parse `.git/index` itself.

- A readable empty index is valid and produces an empty tracked view.
- If no applicable index can be read, the mode fails closed; it never falls back to `.gitignore` or an unfiltered tree.
- If a nested index fails while another applicable index succeeds, only the unavailable nested scope is excluded and reported.
- Direct-command exit behavior and diagnostic codes are defined in [`CLI-V1-Contract.md`](CLI-V1-Contract.md).

## Ordinary exclusions

These checkboxes are independent of Smart Ignore and Git filtering:

| Exclusion | Meaning |
|---|---|
| Hidden folders | Folders marked hidden by the filesystem. |
| Hidden files | Files marked hidden by the filesystem. |
| Dot folders | Folder names beginning with `.`. |
| Dot files | File names beginning with `.`. |
| Empty folders | Folders with no effective included descendants. |
| Empty files | Files whose length is zero. |
| Extensionless files | Files without an extension, including names ending in `.`, but not ordinary dot-files such as `.env`. |

Dot-name ownership is kept separate from the platform hidden attribute. This matters on Unix-like systems, where dot names conventionally appear hidden, and on Windows, where the Hidden attribute is independent from the name.

The interface may omit an option that cannot affect the current workspace. This is presentation behavior only; it must not silently change another selected filter or reinterpret a saved profile.

## Defaults and profiles

The built-in `standard` profile selects `.gitignore` mode and all eight ordinary exclusion groups, including Smart Ignore. A selected Git mode remains selected even when its current rule set is empty.

Desktop local-project settings persist file-type and exclusion choices. Known checkbox states are restored exactly, while a newly discovered extension or exclusion row uses the current product default. Terminal/CLI root-scope compatibility remains a separate contract until the public `--root` workflow is revised. Explicit CLI overrides affect only that invocation and do not rewrite the local settings.

For the complete profile precedence rules, see [`CLI-Profiles.md`](CLI-Profiles.md) and [`CLI-V1-Contract.md`](CLI-V1-Contract.md).

## CLI examples

Use Smart Ignore with `.gitignore` rules:

```bash
devprojex analyze . --git-mode gitignore --exclude smart-ignore
```

Use only Git-tracked files and keep the ordinary hidden, dot-name, empty, and extensionless exclusions enabled explicitly:

```bash
devprojex export context . --git-mode tracked \
  --exclude smart-ignore \
  --exclude hidden-folders \
  --exclude hidden-files \
  --exclude dot-folders \
  --exclude dot-files \
  --exclude empty-folders \
  --exclude empty-files \
  --exclude extensionless-files \
  --format markdown -o ../devprojex-context.md
```

Disable Git filtering and all ordinary exclusions:

```bash
devprojex analyze . --git-mode none --exclude none
```

When any `--exclude` option is supplied, the supplied values replace the profile's exclusion set for that invocation. `--exclude none` cannot be combined with another exclusion token.

## Read-only and output guarantees

Filtering changes only the effective DevProjex view and the generated result. It does not delete, move, rename, edit, stage, commit, or otherwise mutate files in the opened source project.

The same effective selection is used for:

- the project tree;
- preview and metrics;
- clipboard output;
- context export;
- folder and ZIP project export;
- direct CLI analysis and export.

Project copies and generated documents are written only to an explicit safe destination outside the source project.

## Troubleshooting an unexpected result

If a path is missing, check the controls independently:

1. Confirm the active Git mode.
2. Temporarily disable Smart Ignore.
3. Check hidden, dot-name, empty, and extensionless exclusions.
4. Check selected extensions. For Terminal/CLI invocations, also check an explicit root scope.
5. Check the individual tree selection.

If disabling Smart Ignore alone restores a source directory, report the directory name, relevant project markers, and a minimal redacted top-level layout. Do not attach private source contents, credentials, or repository URLs.

## Maintainer contract

Changes to filtering must preserve these invariants:

- collision-prone directory names are not sufficient evidence by themselves;
- stack-specific rules do not leak across project-scope boundaries;
- Git modes never silently downgrade to a less restrictive mode;
- missing control files do not erase explicit user intent;
- link-based artifact evidence is not followed;
- GUI, TUI, CLI, preview, metrics, and exports resolve the same effective selection;
- accepted options always have an observable, documented effect.

Relevant contract coverage includes the Smart Ignore golden matrices, nested-scope and monorepo tests, hierarchical `.gitignore` tests, tracked-index tests, cross-surface selection tests, profile-evolution tests, filesystem mutation tests, and reparse-point tests under [`Tests`](../Tests/).
