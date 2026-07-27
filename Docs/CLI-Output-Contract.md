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

Color and progress modes accept `auto`, `always`, or `never`. `--plain` disables
decorative rendering. Auto mode evaluates stream redirection, `NO_COLOR`,
`TERM=dumb`, and CI. Unicode symbols have ASCII fallbacks and no output depends on
emoji or Nerd Fonts.

## Analysis JSON

Analysis JSON is a deterministic document with `schemaVersion`. It contains
project inventory, effective selection, metrics, diagnostics, timings, and the
deterministic context fingerprint available to the current engine.

`--strict` writes the requested document before returning policy exit code `3`
when diagnostics are present.

## Context JSON

The top-level shape is:

```json
{
  "schemaVersion": "1",
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

## Context XML

XML uses the root element `devprojexContext` with `schemaVersion="1"` and
`kind="devprojex-context"`. It contains corresponding `project`, `selection`,
`metrics`, `tree`, `files`, `diagnostics`, and `fingerprint` elements. XML is a
complete well-formed UTF-8 document with escaped values.

## Markdown and Text

Markdown contains one project heading, an optional fenced tree block, and file
headings with safe variable-length fences. File names and content cannot break
the document structure.

Text preserves the existing readable ASCII-tree and file-section semantics.

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
