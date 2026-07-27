# DevProjex CLI v1 Contract

This document is the normative public contract for DevProjex Terminal and CLI v1.
The parser, generated help, process tests, and completion output are derived from
one `System.CommandLine` command tree.

## Product Surfaces

- `DevProjex.exe` launched without a console starts DevProjex Desktop.
- `devprojex` in an interactive terminal starts DevProjex Terminal.
- `devprojex <command>` runs a deterministic command without initializing Avalonia.
- `devprojex open` opens or reuses Desktop.
- `devprojex ui` sends semantic commands to an existing Desktop instance over local,
  per-user IPC.

The portable distribution contains one primary executable. Terminal and CLI code
is linked into that executable as a class library.

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

`dev` is a maintainer surface and is hidden from normal root help. There is no
root-level arbitrary project argument.

## Selection

Commands accept a typed selection:

- `--profile standard|local|FILE`
- repeatable `--root PATH`
- repeatable `--extension EXT`
- repeatable `--select RELATIVE_PATH`
- `--git-mode none|gitignore|tracked`
- repeatable `--exclude NAME`

Git filtering is independent from ordinary exclusions. `--exclude none` is an
exact empty exclusion set and cannot be combined with another exclusion. An absent
option inherits the selected profile. Direct commands default to `standard`; TUI
defaults to `local` when a local profile exists, otherwise `standard`.

Selected paths are relative to the source root. Empty selection means the full
effective tree. A directory means its effective subtree. Absolute paths, parent
traversal, and paths escaping through links are rejected.

## Streams

`stdout` is the payload channel:

- analysis or context documents;
- one absolute result path for file, folder, and ZIP output;
- help, version, and completion scripts.

`stderr` contains progress, status, warnings, diagnostics, migration guidance, and
errors. Redirected JSON and XML contain no ANSI or additional lines. Direct
commands never prompt.

## Exit Codes

| Code | Meaning |
|---:|---|
| 0 | Success, help, or version |
| 1 | Runtime or I/O failure |
| 2 | Invalid command, option, value, or combination |
| 3 | Policy or explicit readiness failure |
| 4 | Destination conflict |
| 5 | Desktop target unavailable or ambiguous |
| 130 | Canceled |

Human errors include a stable `DPX-*` machine code. Raw platform exception messages
are not written in normal verbosity.

## Output Documents

All new machine-readable documents contain `schemaVersion`. JSON property names,
XML element names, command names, option names, and enum tokens are stable English
identifiers. Human text is localized.

Context export treats text/Markdown/JSON/XML as whole-document formats. Binary file
bytes are never embedded in an AI text context; machine formats identify binary
entries. Folder and ZIP export preserve binary bytes.

## Destinations

CLI destinations are exact:

- folder export creates exactly the requested, previously absent directory;
- ZIP export requires a `.zip` path;
- existing destinations fail with exit code 4;
- `--force` is valid only for atomic ZIP or context-file replacement.

The source tree is never modified. Existing destination canonicalization, staging,
cancellation cleanup, and symlink/junction protection remain mandatory.

## Compatibility

The experimental flat CLI is not executed. Recognized legacy action flags produce
one exact migration example on `stderr` and exit with code 2. Internal desktop
relaunch and UI benchmark protocols are not public CLI options.

## Future Additions

Future Secret Guard, comments, signatures/ECO, token-budget, and MCP features must
consume the same project context plan. They may add options or a new command
namespace only when functional; v1 help contains no placeholders.
