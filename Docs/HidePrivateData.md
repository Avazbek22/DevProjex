# Hide Private Data

**Hide private data** is an opt-in content transformation for text selected in DevProjex.
It detects a deliberately bounded set of personal and machine-specific values and replaces
only the matched span:

```text
DEVPROJEX_REDACTED[email#1]
DEVPROJEX_REDACTED[ipv4#1]
DEVPROJEX_REDACTED[local-user#1]
```

The surrounding file, key, path structure, and formatting remain visible. Source files are
never modified.

## One resolved state

Preview, clipboard, context output, text/Markdown/JSON/XML exports, folder copies, and ZIP
copies consume one prepared redaction state. A finding highlighted in Preview therefore has
the same placeholder and index on every output surface. Clicking a finding can keep that
occurrence as-is for the current session. It does not disable the rule for other occurrences.

Detection and aggregation are deterministic. They depend only on selected file content,
relative paths, enabled transformations, and the pinned rule versions. DevProjex does not use
the current OS username, machine name, locale, network, or another machine-local signal to
guess private data.

## Phase-one rules

### Email (`email`)

Matches ASCII addresses with a conventional local part and a domain containing at least two
labels. The final label contains only letters and has at least two characters. Plus addressing
is supported.

The rule does not match:

* single-label hosts such as `user@localhost`;
* RFC 2606 documentation hosts: `example.com`, `example.net`, `example.org`, their
  subdomains, and domains under `.test`, `.example`, or `.invalid`;
* the service local parts `git`, `noreply`, and `no-reply`;
* non-ASCII local parts or IDN forms in this phase;
* reference and placeholder forms such as `${EMAIL}`, `$(EMAIL)`, `{{email}}`, `<email>`,
  and `%EMAIL%`.

### Global IP addresses (`ipv4`, `ipv6`)

Only globally routable addresses are redacted. Ports and IPv6 brackets remain visible; an
IPv6 zone identifier is accepted for classification but is not part of the replacement.
IPv4-mapped IPv6 addresses are classified by the embedded IPv4 address.

IPv4 exclusions:

* unspecified, loopback, link-local, private, shared-address, benchmarking, multicast, and
  reserved ranges;
* TEST-NET-1/2/3 and `233.252.0.0/24` documentation ranges;
* the public resolver addresses `8.8.8.8`, `8.8.4.4`, `1.1.1.1`, `1.0.0.1`, `9.9.9.9`,
  `149.112.112.112`, `208.67.222.222`, and `208.67.220.220`.

IPv6 exclusions:

* `::`, `::1`, `fe80::/10`, `fc00::/7`, and `ff00::/8`;
* the documentation prefixes `2001:db8::/32` and `3fff::/20`.

Malformed octets and address-like substrings inside longer identifiers or dotted sequences
are not treated as addresses.

### Local usernames in paths (`local-user`)

Only the username segment is redacted in these path forms:

* `C:\Users\name\...` and `C:/Users/name/...`;
* escaped Windows separators such as `C:\\Users\\name\\...`;
* `/home/name/...` and `/Users/name/...`.

Path structure and the rest of the path remain visible. The following generic, operating
system, and CI identities stay visible: `Public`, `Default`, `Default User`, `All Users`,
`user`, `username`, `example`, `demo`, `test`, `runner`, `runneradmin`,
`ContainerAdministrator`, `ContainerUser`, `vagrant`, `jenkins`, and `root`.
Environment references such as `%USERPROFILE%` and the `~/` shorthand are outside this rule.

### MAC addresses (`mac-address`)

Matches six hexadecimal pairs using one consistent `:` or `-` separator. The all-zero and
broadcast values stay visible. Cisco dotted notation is intentionally not detected, and the
boundaries reject substrings inside IPv6 addresses and UUIDs.

### International phone numbers (`phone-number`)

Matches a leading `+` immediately followed by a digit and 8 to 15 digits in total. Spaces,
hyphens, dots, and parentheses may separate groups; the complete span is limited to 20
characters.

Documented fictional ranges stay visible:

* NANP `+1-XXX-555-01XX`;
* UK drama ranges beginning `+44 7700 900` or `+44 20 7946 0`.

Short timezone offsets, diff hunk coordinates, `C++` tokens, and numbers longer than 15
digits do not match.

## Interaction with Hide Secrets

Hide private data and Hide Secrets share one file read, content fingerprint, cache, output
scope, placeholder allocator, and Preview decision model. Enabling both does not create a
second output pipeline.

When findings overlap, a Hide Secrets finding always wins. Keeping that secret occurrence
as-is does not reveal a private-data finding that was suppressed by the overlap decision.
This rule keeps output stable and prevents one byte range from receiving competing
placeholders.

Each option has its own detected and redacted counters. Zero means only that the enabled
rules found no match; it is not a privacy or safety guarantee.

## Intentionally visible ambiguous forms

The phase-one rules keep several source-code forms visible to avoid repeatedly redacting
common project metadata:

* IPv4 values whose last octet is zero stay visible because they usually represent product
  versions or network identifiers rather than hosts. An IPv4 candidate preceded by `version`
  on the same line within the bounded context window is also kept. A real host ending in zero,
  or a real address in such a version context, is an intentional false negative; an ambiguous
  `1.2.3.4` without that context remains redacted and can be kept as-is in Preview.
* Email-like tokens with a file-extension final label or a retina-style first domain label
  such as `icon@2x.png` stay visible. This intentionally accepts false negatives for colliding
  country-code domains such as `.md`, `.sh`, `.rs`, and `.py`, because file names using these
  extensions are substantially more common in project content.
* Placeholder local parts, every local part beginning with `your`, and organizational role
  mailboxes such as `admin`, `owner`, `support`, and `security` stay visible. Local-part
  segments equal to `test` or `tests`, URI authority userinfo, and malformed multi-`@` tokens
  are not treated as email. A real personal address using one of these names is therefore an
  intentional false negative.
* An IPv6 candidate must contain at least one ASCII digit. This rejects language and shell
  scope operators such as `Db::Add` and `[List]::Add`; a valid address composed only of `a-f`
  words is an intentional false negative.
* A local username segment is limited to Unicode letters, ASCII digits, `.`, `_`, and `-` and
  cannot be entirely numeric. Slash-form anchors preceded by a letter or digit are treated as
  URL routes rather than local paths. Usernames outside that character set, numeric usernames,
  paths embedded in ambiguous route text, and generic documentation identities such as `me`,
  `developer`, and `devuser` are intentional false negatives.

## Limits and fail-closed behavior

Only decoded text files are inspected. Binary files are not inspected. A selected text file
larger than the configured 16 MiB inspection limit cannot be silently included in a strict
redacted output. Read, decoding, detector-budget, and inspection failures stop strict output
instead of emitting uninspected text.

The detector uses a feature prescan and starts only rule scanners whose required characters
or path anchors occur in the file. Findings share the same per-file and per-output budgets as
Hide Secrets. Cache entries retain file identity, rule identity, spans, rule IDs, and value
fingerprints, not source strings or detected values.

## Intentional phase-one boundaries

This phase does not attempt to detect:

* binary metadata or image/document contents;
* non-ASCII email local parts and IDNs;
* local or private IP addresses;
* Cisco dotted MAC notation;
* national phone formats without `+`;
* names, street addresses, government identifiers, or arbitrary free-form personal text;
* values assembled dynamically, encrypted, encoded, or obfuscated.

Use `--hide-private-data` with `analyze`, `export context`, and `export project`. An explicit
`--hide-private-data false` overrides a saved portable profile. The option is not exposed in
the Terminal Workspace during this phase.
