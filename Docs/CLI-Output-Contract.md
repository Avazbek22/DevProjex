# CLI Output Contract

This document defines the stable stream, document, error, and exit-code contract
for direct DevProjex commands.

## Streams

stdout is the machine/payload channel:

- help and version;
- completion scripts;
- text, JSON, or XML analysis;
- text, Markdown, JSON, or XML context;
- one absolute result path after file, folder, or ZIP output.

stderr is the operational channel:

- progress and status;
- warnings and diagnostics;
- human errors and hints;
- legacy-syntax migration guidance.

Direct commands never prompt. Redirected stdout never contains ANSI, progress,
spinners, tables around a machine payload, or additional summary lines.

## Terminal Modes

Color and progress modes accept `auto`, `always`, or `never`. Their precedence is
`--plain`, an explicit command-line color value, non-empty `NO_COLOR`, then
terminal capability detection. `NO_COLOR=` is unset. `--plain --color always`
is a usage error.

`--color never` disables ANSI color but does not imply ASCII. `--plain` disables
ANSI, markup, box-drawing characters, emoji, and animations and uses stable ASCII
lines. `TERM=dumb` selects the same conservative terminal-capability fallback and
never starts the TUI. stdout and stderr TTY state are evaluated independently;
machine payloads remain undecorated in every mode.
Explicit `--plain --progress always` uses bounded static ASCII stderr lines rather
than a spinner. Plain `auto`, `quiet`, and `minimal` do not emit optional progress.

## Analysis JSON

Analysis JSON is a deterministic document with `schemaVersion`. It contains
project inventory, effective selection, metrics, diagnostics, and the
deterministic context fingerprint available to the current engine. v1 does not
publish a timings field.

`selection.selectedRootFolders` is the validated structural root scope applied
to that analysis. When no explicit CLI root override is supplied, it contains
all effective project roots. An explicit `--root` override restricts it to the
validated requested subset. `inventory.availableRootFolders` remains the set
discovered before that explicit restriction. The removed Desktop root selector
does not restrict analysis in the current model.

When Hide Secrets is enabled, analysis adds a top-level `redaction` object with
`matchedCount`, `redactedCount`, and a non-safety `notice`. Zero means the pinned
rules matched nothing; it never means that the project is safe.

`--strict` writes the requested document before returning policy exit code `3`
when diagnostics are present.

Plain or redirected text and text written to a file use one canonical field
model, ordering, and final-newline policy. An interactive rich presentation may
change layout but not the represented fields or values.

## Context JSON

The top-level shape is:

```json
{
  "schemaVersion": 1,
  "kind": "devprojex-context",
  "project": {
    "root": "/workspace/app",
    "name": "app"
  },
  "selection": {
    "gitMode": "gitignore",
    "exclusions": [],
    "roots": [],
    "extensions": [],
    "selectedPaths": []
  },
  "metrics": {
    "files": 0,
    "folders": 0,
    "characters": 0,
    "estimatedTokens": 0
  },
  "tree": null,
  "files": [],
  "diagnostics": [],
  "fingerprint": "..."
}
```

Property order is deterministic where contract tests require it. Paths use `/`
inside machine documents. A binary entry has `isBinary: true` and null content;
binary bytes are never inserted into AI context output.

For a cached Git clone, `project.source` is an additive object containing the
source type, safe repository URL, and optional branch/commit metadata. Human
identity never uses the generated cache-directory suffix. Local projects retain
the existing root/name representation and omit `source`.

## Context XML

XML uses the root element `devprojexContext` with `schemaVersion="1"` and
`kind="devprojex-context"`. It contains corresponding `project`, `selection`,
`metrics`, `tree`, `files`, `diagnostics`, and `fingerprint` elements. XML is a
complete well-formed UTF-8 document with escaped values.

## Markdown and Text

Markdown contains one project heading, an optional fenced tree block, and file
headings with safe variable-length fences. File names and content cannot break
the document structure.

Text preserves the existing readable tree and file-section semantics. With
`--plain`, tree connectors use strict ASCII.

Context generation streams UTF-8 to stdout or an adjacent temporary file. File
output is flushed and moved atomically; cancellation or write failure removes
staging. A complete project context is never materialized as one managed string,
and exact export validates then decodes each included text file through bounded
chunks rather than retaining a full per-file string.

File output is rollback-safe because only the adjacent staging file is visible
until completion. stdout is inherently not rollback-capable: cancellation can
leave an already-written document prefix on stdout, but never writes the
cancellation diagnostic or other error text into that payload.

## Dry run

Context and project `--dry-run` perform source planning plus destination safety
and conflict checks. They create no file, folder, ZIP, or stdout payload. stdout
is empty and the operational plan is written to stderr. Success means that
preflight is ready; it is not a result path.
The readiness line is the requested dry-run result and remains visible at
`quiet` and `minimal`; incidental status and progress remain suppressed.

A project-copy dry run with Hide Secrets enabled also states that detected text
will be changed, binary files will remain unchanged, and the result may not build
or run. This warning does not create or scan an output artifact.

## Errors

Human errors follow:

```text
error[DPX-EXPORT-DESTINATION-EXISTS]:
Destination already exists: /exports/app.zip

hint:
Choose another path or use --force for ZIP replacement.
```

The `DPX-*` code is stable and language-independent. Normal verbosity never
prints raw `Exception.Message`, an inner exception, or a platform-localized I/O
message. Diagnostic verbosity may report an exception type, safe path context,
stack trace, and request identifier, but never file content or secrets.

Secret inspection never emits uninspected text. A selected text file above the supported
16 MiB limit fails no command: `export context` omits its text, and `export project`
leaves it out of the copy and names it in `DEVPROJEX-NOTICE.txt`.
`DPX-SECRET-DETECTION-FAILED` identifies rule loading, matching, timeout, or
classified-read failure on any command; it is a runtime failure (exit `1`) and never
falls back to an unredacted artifact.

## Exit Codes

| Code | Meaning |
|---:|---|
| 0 | success, help, or version |
| 1 | runtime or I/O failure |
| 2 | invalid command, option, value, or combination |
| 3 | policy/check failure |
| 4 | destination conflict |
| 5 | desktop target unavailable or ambiguous |
| 130 | canceled |

These codes are part of CLI v1 and are suitable for shell and CI decisions.
An expected downstream pipe close is a quiet success and is not rendered as
`DPX-CLI-UNEXPECTED`.
