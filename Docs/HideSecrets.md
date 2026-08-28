# Smart Secrets and Hide Secrets

Hide Secrets and [Hide private data](HidePrivateData.md) share one redaction pipeline and
one set of Preview decisions. Overlapping findings are rendered as non-overlapping segments with
an ordered candidate stack. Keeping a secret finding changes that whole occurrence across all of
its fragments, while text still covered by non-kept private-data findings remains redacted. The
original segment appears only after every candidate in its stack is kept. The full overlap
contract is described in [HidePrivateData.md](HidePrivateData.md).

**Smart Secrets** is DevProjex's local, deterministic credential-detection engine.
**Hide Secrets** is the opt-in switch that applies its decisions to produced output.
It is off by default and adds no scan cost until enabled.

Detected values are replaced in place. The file, path, key name, and surrounding
code remain available as context. DevProjex never describes an output as safe or
clean: a finding means that a rule matched, while no findings means only that the
current rules matched nothing.

Smart Secrets runs locally, uploads no code, uses no model, and never modifies the
source tree.

## One decided state

The same redaction decisions apply to every output built from the current
selection:

- Preview;
- clipboard payloads;
- text, Markdown, JSON, and XML context exports;
- folder copies;
- ZIP copies.

If Preview shows a placeholder, every later output contains that placeholder. A
finding kept as-is in Preview returns only that occurrence to its original value;
the decision then applies to every output for the rest of the application session.
The Preview context menu can apply the same decision in bulk to every occurrence
of the rule or to every detected occurrence in the same file, and can hide those
occurrences again as one action.

Individual and bulk keep-as-is decisions are intentionally session-only. They are not written to
project profiles, so profiles never retain secret fingerprints or source
locations. If a finding moves after a file change, it is treated as a new
occurrence and redacted again.

## Manual mark classes

A manual mark belongs to exactly one redaction class: Secret or Private data. The
corresponding switch controls the whole class, including detector findings and manual marks;
turning Hide Secrets off reveals Secret marks without changing Private-data marks. Creating a
Secret mark in Preview enables Hide Secrets automatically. Persistent marks store only a keyed
value identity and their class, never the original value. Store schema v4 migrates every mark
created by schema v3 to the Secret class, preserving the behavior it had before classes existed.

## Placeholders and identity

Each removed value is replaced with a deterministic placeholder:

```text
DEVPROJEX_REDACTED[telegram-bot-api-token#1]
```

The rule id describes what matched. The index identifies a value within one
produced output. Repeated occurrences of the same value under the same rule reuse
the same index across files; different values receive different indexes.

## Two detection tiers

The engine combines two complementary tiers.

### Provider rules

DevProjex ships a reviewed managed port of the default Gitleaks
[`v8.30.1`](https://github.com/gitleaks/gitleaks/tree/v8.30.1) configuration:

- 221 content rules recognize provider-shaped credentials in selected text;
- the one path-only PKCS#12 rule is intentionally excluded because a filename or
  opaque binary payload cannot be redacted in place;
- keyword prescreening limits which bounded regular expressions inspect a file;
- entropy thresholds and upstream allowlists preserve the pinned rule semantics;
- `gitleaks:allow` suppresses findings on that line;
- expressions use the managed non-backtracking .NET engine with a timeout.

### Scope-aware configuration rules

Gitleaks is deliberately conservative with short and low-entropy values. Smart
Secrets therefore adds a structured tier for places where a key name and file
shape are stronger evidence:

- credential URIs for PostgreSQL, MySQL, MongoDB, Redis, AMQP, and HTTP(S);
- ADO.NET and JDBC-style connection strings;
- `.env*` and `.npmrc` assignments;
- `ENV` and `ARG KEY=value` assignments in Dockerfile and Containerfile
  variants; the legacy `ENV KEY value` form is also supported. A space-delimited
  `ARG KEY value` is not treated as an assignment because Docker's ARG grammar
  defines only `ARG KEY` and `ARG KEY=value`;
- `Authorization` and `Proxy-Authorization` credentials in `.http` and
  `.rest` request files, with the authentication scheme left visible;
- every `Cookie` pair value and the initial `Set-Cookie` pair value in `.http`
  and `.rest` request files; cookie names remain visible, and `Set-Cookie`
  attributes such as `Path`, `HttpOnly`, and `Max-Age` stay visible;
- password fields in `.pgpass`, `pgpass.conf`, `.netrc`, and `_netrc`;
- `appsettings*.json`, `*.config`, `application*.yml`,
  `application*.yaml`, `*.tfvars`, `docker-compose*.yml`, and
  `docker-compose*.yaml` values;
- quoted assignments in `settings.py`.

Credential URIs and connection strings redact only the password segment. Host,
user, database, scheme, and other surrounding values remain visible. Credential
URIs on RFC 2606 documentation hosts (`example.com`, `example.net`,
`example.org`, their subdomains, and the `.test`, `.example`, and `.invalid`
TLDs) are not redacted. `localhost` is intentionally still inspected because
development credentials can be real secrets.

The structured tier reuses Smart Ignore's project-scope resolver and root facts.
The nearest marked project owns its descendants, so stack-specific vocabulary
does not leak between sibling or nested projects in a monorepo. Ordinary source
assignments such as `var password = "test123"` are outside this tier; provider-
shaped credentials in source remain covered by Gitleaks rules.

References and placeholders such as `${DB_PASSWORD}`, `$(DbPassword)`,
`%DB_PASSWORD%`, `{{ secret }}`, `<password>`, and empty values are not redacted.
Common template values such as `changeme`, `your-password-here`, `replace_me`,
`placeholder`, `null`, `none`, `your-api-key-here`-style templates, and repeated
non-numeric characters are also ignored. These checks match whole values or
interpolation syntax, never a substring inside an otherwise credential-shaped
value. Weak literal values such as `password`, `admin`, `0000`, and `123456`
still match in recognized configuration shapes.

The provider tier intentionally preserves Gitleaks' upstream substring-based
stopwords, including their false-negative trade-off. The scope-aware tier does
not copy that behaviour: its placeholder allowlist is whole-value only.

Smart Ignore and Smart Secrets share an engine shape but have opposite failure
biases. Smart Ignore leaves a directory visible when artifact evidence is missing,
because hiding source is the expensive error. In a recognized configuration
shape, Smart Secrets redacts a sensitive literal when further evidence is
missing, because a missed credential can leave the user's control and a false
positive can be reversed per occurrence in Preview.

The pinned TOML is embedded and verified by SHA-256 before compilation. DevProjex
does not bundle or launch Gitleaks and has no native scanning dependency.
Attribution is recorded in
[`THIRD-PARTY-NOTICES.md`](../THIRD-PARTY-NOTICES.md).

## Text, binary files, and limits

Only selected text files are inspected. Binary files are not scanned and pass
through unchanged in folder and ZIP copies; context documents continue to mark
them as binary without embedding their bytes.

A selected text file above 16 MiB is not scanned, and what happens next depends on what
the surface produces. Documents — preview, clipboard, and every context format — omit
its text and mark the entry, which is what they already do for a file that large with
Hide Secrets off. A folder or ZIP project copy reproduces bytes rather than rendering
them, so it leaves the file out of the copy entirely and names it in
`DEVPROJEX-NOTICE.txt` under the copy's root; the rest of the project is copied
normally. One unreadable file therefore costs that file, never the whole operation.
Detection errors and regex timeouts still stop the operation on every surface. Text that
was never inspected is never emitted, an uninspected file is never passed off as
inspected, and a file left out of a copy is always named.

The count scan stores compact spans, rule ids, file fingerprints, and hashed value
identities in a bounded LRU cache. It does not retain complete source or redacted
strings. Changed files are rescanned individually; unchanged files reuse their
findings. Full transformed content is produced lazily for Preview or export, and
temporary data is removed after completion or cancellation.

## Folder and ZIP copies

With Hide Secrets enabled, a project copy is intentionally not byte-for-byte
faithful to the source. Matching text changes, and the copy may not build or run.
Desktop and TUI confirm this, and CLI `--dry-run`
reports it before writing.

Directory structure, included empty folders, timestamps, and binary bytes retain
the normal project-copy contract. The destination remains outside the source
project, and the source stays read-only.

## Counts and interaction

Hide Secrets has its own content-transformation section; it is not a path filter
and never changes the tree, Smart Ignore, Git mode, roots, or extensions.

No count is shown before a completed scan. During inspection the existing status
surface reports scanning. After completion, the label shows the number of
detected values; when keep-as-is decisions leave fewer values hidden, it shows
the detected and still-hidden counts side by side, and the row's status
indicator reports both numbers in text. A zero-result label explicitly
says that no values were detected; it does not claim that the selection is safe.
`analyze` reports matched and redacted counts under the same contract.

In Desktop Preview, click a highlighted occurrence to toggle keep-as-is, or move
between findings with `Alt`+`↓` / `Alt`+`↑` (`⌥` on macOS) and toggle the active
one with `Enter`. The Preview scrollbar marks every line with a finding and
scrolls to the line when a marker is clicked; a marker does not disappear when
its finding is kept as-is. In Terminal Workspace,
`[` and `]` navigate findings and `Enter` or `Space` toggles the active
occurrence.

## CLI

Use the dedicated additive option:

```shell
devprojex export context . --hide-secrets --format markdown -o ../context.md
devprojex export project . --hide-secrets --as zip -o ../project-redacted.zip --dry-run
devprojex analyze . --hide-secrets --findings --fail-on-findings
```

`analyze --findings` lists the effective findings as rule id, category, relative
path, and one-based source line — never the detected value — and
`--fail-on-findings` returns a policy exit code when any effective finding
exists, so a pipeline can gate on redaction before exporting.

`--hide-secrets` does not replace the `--exclude` collection. The v5 token
`--exclude hide-secrets` remains accepted for compatibility, but is hidden from
new help and completion output. An explicit `--hide-secrets false` disables a
value inherited through a profile or the legacy token for that invocation.

## MCP

In MCP server mode (`devprojex mcp`) secret redaction is not a switch: it is
always enabled for returned file content, stored context packs, and search —
matching runs against the redacted text, not the original. There is no server
flag to disable it, and tool schemas intentionally expose no redaction controls,
so neither a configuration mistake nor the connected agent can turn it off.
Private-data redaction remains a separate, opt-in server flag; see
[McpServer.md](McpServer.md) for the full security model.

## Updating the rule source

A provider-rule update is a reviewed source change, not a runtime download:

1. pin a Gitleaks release;
2. replace the embedded `config/gitleaks.toml` source;
3. update the expected SHA-256 and reviewed rule counts;
4. regenerate the attributed corpus fixture;
5. run positive, negative, timeout, determinism, scope, and cross-output tests;
6. review RE2-to-.NET differences and every newly path-only rule.

Identical input, selection, overrides, and application version produce identical
decisions.
