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
- one accepted local path or safe repository URL after `open`.

stderr is the operational channel:

- progress and status;
- warnings and diagnostics;
- human errors and hints;
- legacy-syntax migration guidance.

Direct commands never prompt. Redirected stdout never contains ANSI, progress,
spinners, tables around a machine payload, or additional summary lines.

Human-readable file names, paths, and one-line success-path payloads escape
carriage return, line feed, tab, other control characters, U+2028, and U+2029 as
visible `\\r`, `\\n`, `\\t`, or `\\uXXXX` sequences. This keeps every reported
path on one physical line and prevents terminal control injection. JSON and XML
retain exact machine values and use their format-native escaping instead.

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
Git progress for URL project sources is always confined to stderr. With interactive
stderr, outside CI, and without `--plain`, the latest Git message replaces one line
using carriage return and spaces only; no ANSI cursor controls are used. Every frame
is sanitized and truncated to `terminal width - 1` display columns, with the width
read again for every update, and the line is cleared before ordinary output resumes.
Interactive measured project-export progress uses the same single-line behavior when
color is disabled through `NO_COLOR` or `--color never`.

Redirected stderr, CI, `TERM=dumb`, and `--plain` use bounded milestone output instead:
one localized clone-start line, at most three Git percentage milestones, and one
completion line (never more than six physical lines for one operation). Both `auto`
and `always` use that fallback. `--progress never`, `--verbosity quiet`,
`--verbosity minimal`, and `-q` emit zero progress bytes. Progress never enters stdout,
so machine payloads and streamed context remain byte-for-byte unchanged.

## Analysis JSON

Analysis JSON is a deterministic document with `schemaVersion`. It contains
project inventory, effective selection, metrics, diagnostics, and the
deterministic context fingerprint available to the current engine. v1 does not
publish a timings field.

The base shape is:

```json
{
  "schemaVersion": 1,
  "kind": "devprojex-analysis",
  "project": {
    "root": "/workspace/app",
    "name": "app",
    "source": null
  },
  "selection": {
    "gitMode": "gitignore",
    "exclusions": [],
    "hideSecrets": false,
    "hidePrivateData": false,
    "compressCode": false,
    "stripComments": false,
    "stripBlankLines": false,
    "roots": [],
    "extensions": [],
    "selectedPaths": []
  },
  "inventory": {
    "files": 0,
    "folders": 0
  },
  "metrics": {
    "bytes": 0,
    "tree": {
      "lines": 0,
      "chars": 0,
      "tokens": 0
    },
    "content": {
      "lines": 0,
      "chars": 0,
      "tokens": 0
    }
  },
  "diagnostics": [],
  "fingerprint": "..."
}
```

`selection.roots` is the validated structural root scope applied to the
analysis. With no explicit CLI root override it contains the effective profile
roots; an explicit `--root` replaces it with the validated requested subset.
Available roots discovered before that restriction are not exposed in analysis
JSON. `inventory` contains only the projected `files` and `folders` counts.

For a local source, `project.source` is null. For a cached Git source it is an
object containing `type: "git"`, the safe `repositoryUrl`, and nullable `branch`
and `commit` properties. Diagnostic entries contain `code`, `severity`, `message`,
and nullable `path`; machine paths use `/` separators.

When Hide Secrets is enabled, analysis adds a top-level `redaction` object with
`matchedCount`, `redactedCount`, and a non-safety `notice`. Zero means the pinned
rules matched nothing; it never means that the project is safe.

When Hide Private Data is enabled, analysis adds a top-level `privacy` object
with the same complete shape: `matchedCount`, `redactedCount`, and a non-privacy-
guarantee `notice`. Zero means that the current rules matched nothing; it never
guarantees that the project contains no private data.
Private-data redaction also covers generated human-readable content, including
tree text and content headings. Machine metadata remains directly addressable:
`project.root` is the absolute project path and is not a redacted display value.

```json
{
  "privacy": {
    "matchedCount": 0,
    "redactedCount": 0,
    "notice": "..."
  }
}
```

When enabled content inspection withholds one or more files because they are too
large, unreadable, non-regular filesystem entries, or use an unsupported encoding,
analysis adds:

```json
{
  "contentInspection": {
    "unscannableCount": 1,
    "unscannableFiles": [
      {
        "path": "relative/path.txt",
        "reason": "too-large"
      }
    ]
  }
}
```

`unscannableCount` equals the array length. Each entry contains a source-relative
`path` with `/` separators and a `reason` token of exactly `too-large`,
`unreadable`, or `unsupported-encoding`. The object is omitted when no files are
withheld.

With `--findings`, analysis adds an ordered top-level `findings` array. Each
effective finding contains exactly `ruleId`, `category` (`secret` or
`private-data`), `relativePath`, and one-based `lineNumber`. The line number refers
to the original decoded source file before code compression, comment removal, or
blank-line removal and recognizes LF, CRLF, and lone CR boundaries. The array
count equals the combined effective matched counters from that output session.
Secret values, source fragments, assignment context, fingerprints, and raw
detector exceptions are never serialized. Text findings escape path control
characters so each descriptor occupies one physical output line. Text analysis
renders findings after the main field/value table in a separate three-column table
with localized `category`, `rule`, and `file:line` headings. Plain and redirected
output use display-cell-aware space padding; tab characters are never emitted.

When code compression, comment removal, or blank-line removal is enabled, analysis content metrics are
calculated from the transformed text and the document adds a top-level `compression`
object with `compressedFiles`, `unchangedFiles`, `bodyTransformedFiles`,
`commentTransformedFiles`, `blankLineTransformedFiles`, `sourceCharacters`, and
`transformedCharacters`.
`compressedFiles` remains the total number of files changed by any syntax
transformation. Inventory and source byte size still describe the selected project
files, not a materialized export container. Machine selection output exposes the
independent `compressCode`, `stripComments`, and `stripBlankLines` Booleans.

`--strict` writes the requested document before returning policy exit code `3`
when diagnostics are present.
`--fail-on-findings` likewise writes the requested document before returning
policy exit code `3` when effective findings exist; the two gates are independent.

## Recent and Cache JSON

`recent --format json` emits schema version 1 with kind `devprojex-recent` and a
newest-first `items` array. Entries use stable `kind`, nullable `path`/`url`,
`name`, `parent`, and `lastOpened` properties.

`cache list --format json` emits schema version 1 with kind
`devprojex-repository-cache`. Entries expose `url`, `state`, nullable `branch`
and `commit`, `localPath`, `approximateSizeBytes`, and `lastUsed`. Both ready and
damaged indexed entries are visible; `state` is `ready` or `damaged`. A partial
listing caused by a busy index lock or future-schema root additionally emits
`"incomplete": true`, writes a localized warning to stderr, and returns policy
exit code `3`. Complete output omits the additive field.

The text forms of `recent` and `cache list` use display-cell-aware, space-padded
columns and never tabs. Timestamps are rendered in the local time zone as
`yyyy-MM-dd HH:mm`. Cache sizes use binary human-readable units such as `68.2 MiB`.
Their JSON documents are unchanged: timestamps remain full UTC ISO-8601 values and
cache sizes remain byte counts.

`cache remove` and `cache clear` calculate `removed`, `retained`, and `failed`
inside the index-locked operation. A busy index lock, unsupported future schema,
or failed index update cannot be reported as empty success and produces policy
exit code `3`. `cache clear` also counts unindexed cache containers.

## Doctor JSON

`doctor --format json` emits the stable schema:

```json
{
  "schemaVersion": 1,
  "kind": "devprojex-doctor",
  "version": "5.1",
  "os": "Microsoft Windows 10.0.26100",
  "architecture": "x64",
  "packageType": "portable",
  "singleFile": true,
  "checks": [
    {
      "name": "terminal-launcher",
      "code": "DPX-DOCTOR-TERMINAL-LAUNCHER",
      "status": "pass",
      "severity": "info",
      "detail": "...",
      "hint": null,
      "path": null
    }
  ]
}
```

`architecture` is the lowercase .NET runtime architecture token. `packageType`
is `store` or `portable`. Check `name` and `code` are stable English identifiers;
`detail` and nullable `hint` are human-readable and may be localized. `status` is
exactly `pass`, `warning`, `failure`, or `skip`; the severity mapping is
`pass` -> `info`, `warning` -> `warning`, `failure` -> `error`, and `skip` ->
`info`. Nullable `path` uses `/` separators. A document containing a `failure`
check is still written in full and returns policy exit code `3`; warnings and
skipped checks alone do not change the success code.

For `open`, a local source reports its accepted absolute path. A repository URL
source reports only its safe URL; the generated physical cache path is never a
success payload.

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
    "bytes": 0,
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
complete well-formed UTF-8 document with escaped values. Characters forbidden by
XML 1.0 are replaced with `U+FFFD` so file content and metadata cannot invalidate
the document.

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
Any effective code-transformation option (`--compress-code`, `--strip-comments`, or
`--strip-blank-lines`) also announces that a transformed copy carries
`DEVPROJEX-NOTICE.txt`. A real folder or ZIP export fails with
`DPX-EXPORT-RESERVED-NAME` if that reserved root file already exists in the source.

With code compression enabled, context, folder, and ZIP exports all consume the
same validated transformed snapshot. Unsupported or rejected source files remain
complete. Named block bodies become minimal syntax-valid placeholders (`{ }`, or
`...` for Python); JavaScript-family block functions with stable assignment or
export bindings follow the same rule, including function-valued object properties
and inline functions wrapped one or two calls deep under a stable binding. Data
properties and bare callbacks remain unchanged.
An expression body whose expression fits on one source line remains byte-for-byte
complete as signature-level context; a multiline expression is implementation and
is compressed like a block body. Free lambdas or closures, fields, and language-level
properties remain complete, including nested callbacks in initializers and
property accessors. Python leading function docstrings and class `__init__` and
`__post_init__` methods also remain complete. Ruby `initialize` and PHP `__construct`
remain complete because they declare instance state; Ruby DSL/free blocks and PHP
anonymous/arrow functions are not treated as named bodies. Mixed HTML around PHP sections
remains byte-for-byte complete. Scala braced named `def` bodies and multiline ordinary
expression bodies are compressed; state declarations, `given` values, class-level constructor
statements, and Scala 3 significant-indentation bodies remain complete. Project source files are
never modified. Kotlin properties and primary-constructor state remain complete, including lambda
initializers and custom accessors; named block functions, `init` blocks, secondary constructors,
and multiline expression bodies are compressed to block-form declarations. Kotlin output never
uses `= { }`, which would denote a lambda rather than a function body. Scala deliberately retains
`= { }` because braces denote a block expression there. Top-level Kotlin DSL calls and free lambdas
are not captured.

With `--strip-comments`, syntax-tree comments are removed in 20 language packs: the 14
body-compression languages plus comments-only HTML, CSS, TOML, Bash, XML, and YAML packs.
The six additional packs remain on the unsupported fast path when only `--compress-code` is enabled.
XML-family project markup preserves CDATA, declarations, processing instructions, DOCTYPE
content, and attributes; YAML preserves scalar content, anchors, tags, and document markers.
Python leading module, class, and function docstrings are documentation for this
mode and are removed too; a suite that would otherwise become empty retains `...`.
The initial shebang remains, while directive comments such as `// eslint-disable`,
`// @ts-ignore`, and `# type: ignore` are deliberately removed. Comment-like text in
strings, interpolations, heredocs, attributes, annotations, and preprocessor directives
remains unchanged. PHP text outside `<?php ... ?>` sections is never classified as a PHP
comment. HTML comments are removed, including conditional comments, while content in HTML
`script` and `style` raw-text nodes is not recursively parsed and remains byte-for-byte complete.
Blank and whitespace-only lines adjacent to removed full-line comments are collapsed to at most
one between retained content blocks and to none at document boundaries. Blank lines outside an
affected comment site remain byte-for-byte unchanged; this option is not a general formatter.

With `--strip-blank-lines`, every whitespace-only source line outside a protected multiline
syntax-tree leaf is deleted. Protected leaves include multiline strings, raw and template
literals, heredocs, YAML block scalars, and multiline comments when comments remain enabled.
The rule is grammar-driven and identical for all 20 packs. XML and HTML whitespace inside
text nodes is character data and remains unchanged. Leading and trailing blank runs are removed,
while the final newline of the last content line remains. The option does not affect document
separators or path headers added after per-file transformation.

The complete three-flag mode matrix is deterministic: each of the eight combinations applies
only the selected edit families. Body, comment, and blank-line edits are merged into one plan,
applied once, and validated once; outer edits absorb contained edits. Hide Secrets, when
enabled, runs afterward over those exact transformed bytes. Unsupported files remain
byte-for-byte complete, and no source file is modified.

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
