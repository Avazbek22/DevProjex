# Hide Secrets

Hide Secrets replaces detected credential values in the output while keeping the
file, its path, and the surrounding code. It is an opt-in content transformation:
the option is off by default and adds no scanning cost until it is enabled.

Hide Secrets does not certify a project as safe. A rule match means that DevProjex
found a value it knows how to recognize. No matches means only that the current
rules matched nothing.

## One decided state

The same redaction decisions apply to every output built from the current
selection:

- Preview;
- clipboard payloads;
- text, Markdown, JSON, and XML context exports;
- folder copies;
- ZIP copies.

If Preview shows a placeholder, every later output contains that placeholder. If
the user keeps one finding as-is in Preview, only that occurrence returns to its
original value, and the decision is used by every output for the rest of the
application session.

If a finding moves because the source file changes, the old override is discarded
and the moved value is redacted again. This fail-closed rule prevents a decision
about one source occurrence from silently attaching to another.

Overrides deliberately do not persist in the project profile. Persisting hashes
or locations of credential-like values would create sensitive, stale profile
state after files change. Reopening DevProjex therefore starts with all current
findings redacted again.

## Placeholders and identity

Each removed value is replaced in place:

```text
DEVPROJEX_REDACTED[telegram-bot-api-token#1]
```

The rule id describes what matched. The index identifies the value within one
produced output. Repeated occurrences of the same value under the same rule reuse
the same index across files; different values receive different indexes. File
ordering, overlap resolution, and index assignment are deterministic.

Each output containing a redaction also carries a short format-native legend:

- text uses a plain header;
- Markdown uses an HTML comment;
- JSON uses a top-level `redaction` object;
- XML uses a top-level `redaction` element;
- folder and ZIP copies contain `DEVPROJEX_REDACTIONS.txt` at the copy root.

If that filename already exists in the selected project, DevProjex chooses the
first free deterministic suffix such as `DEVPROJEX_REDACTIONS-1.txt`. Source files
are never overwritten to add the legend.

## Detection rules

DevProjex ships a reviewed managed port of the default Gitleaks
[`v8.30.1`](https://github.com/gitleaks/gitleaks/tree/v8.30.1) configuration:

- 221 content rules can redact values in text;
- the one path-only PKCS#12 rule is intentionally outside this feature because a
  filename or opaque binary payload cannot be redacted in place;
- keyword prescreening avoids running every expression over every selected file;
- entropy thresholds and upstream allowlists reduce known false positives;
- `gitleaks:allow` suppresses findings on that line, matching the pinned rule
  source's convention;
- every expression uses the managed non-backtracking .NET engine with a bounded
  timeout.

The configuration is embedded and verified by SHA-256 before it is compiled.
DevProjex does not bundle or launch the Gitleaks executable, and it does not use
native secret-scanning dependencies. Attribution and license text are in
[`THIRD-PARTY-NOTICES.md`](../THIRD-PARTY-NOTICES.md).

## Text, binary files, and limits

Only selected text files can be redacted. Binary files are not scanned and pass
through unchanged in a folder or ZIP copy; context documents continue to represent
them as binary entries without embedding their bytes.

Selected text files larger than 16 MiB fail the Hide Secrets operation closed.
DevProjex does not silently skip them or continue in a weaker mode. Detection
errors and bounded-regex timeouts also stop the operation before a complete
artifact is published.

The redaction stage uses the existing classified content reader. Each selected
source text is read once into a per-operation snapshot, then Preview or export
consumes that decided snapshot. This prevents a file mutation between detection
and writing from reintroducing an unreviewed value. Cancellation removes staging
data and never modifies the source tree.

## Folder and ZIP copies

With Hide Secrets enabled, a project copy is intentionally not byte-for-byte
faithful to the source. Text containing findings is changed, the root legend is
added, and the result may not build or run. The Desktop and TUI confirmation, and
the CLI dry-run plan, state this before writing the copy.

Directory structure, included empty folders, timestamps, and binary bytes retain
the normal project-copy contract. The destination must still be outside the
source project. The source remains read-only.

## Counts and interaction

Hide Secrets is always available in Exclusions because its availability cannot be
known without reading content. While the option is off, no count is shown and no
detector runs. Once the current selected output has been scanned, Desktop and TUI
show the number of values that remain redacted. `analyze` reports both matched and
redacted counts without turning zero into a safety claim.

In Desktop Preview, click a highlighted occurrence to toggle keep-as-is. In
Terminal Workspace, `[` and `]` move between highlighted occurrences and
`Enter` or `Space` toggles the active occurrence. Mouse activation remains
available when TUI mouse support is enabled.

## CLI

Enable the transformation explicitly with the repeatable selection option:

```shell
devprojex export context . --exclude hide-secrets --format markdown -o ../context.md
devprojex export project . --exclude hide-secrets --as zip -o ../project-redacted.zip --dry-run
```

Supplying any `--exclude` value replaces the profile's exclusion set for that
invocation. Combine `hide-secrets` with the other required exclusions explicitly,
or use a local profile where the checkbox is saved. `--exclude none` still
conflicts with every other exclusion token.

## Updating the rule source

A rule update is a reviewed source change, not an automatic download:

1. pin a specific Gitleaks release;
2. replace the embedded `config/gitleaks.toml` source;
3. update the expected SHA-256 and reviewed rule counts;
4. regenerate the attributed corpus fixture;
5. run positive, negative, timeout, determinism, and cross-output contract tests;
6. review RE2-to-.NET differences and any newly path-only rule explicitly.

The application never downloads detection rules at runtime, so identical input
and application version produce identical decisions.
