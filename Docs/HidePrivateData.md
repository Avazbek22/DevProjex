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
* placeholder and organizational role local parts such as `git`, `noreply`, `admin`, `dev`,
  `qa`, `staging`, and `testing`;
* attribution and contact files such as `LICENSE`, `NOTICE`, `AUTHORS`, `CONTRIBUTORS`,
  `SECURITY`, `CODEOWNERS`, and `.mailmap`, and package manifests such as `package.json`,
  `composer.json`, `composer.lock`, and `pyproject.toml`, where published addresses are intentional
  metadata; publishing-named files and `.mdoc` manuals follow the same policy;
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
* canonical repeated or sequential examples such as `2.2.2.2` and `1.2.3.4`, version syntax,
  dependency-resolution arrows, leading-zero octets, ASN.1 OID prefixes, RCS revisions, and values
  in recognized dependency lock, version-named, or versioned release-note files.

IPv6 exclusions:

* `::`, `::1`, `fe80::/10`, `fc00::/7`, and `ff00::/8`;
* the documentation prefixes `2001:db8::/32` and `3fff::/20`, and the retired 6bone prefix
  `3ffe::/16`;
* the documented Google, Cloudflare, and Quad9 public resolver literals;
* IANA special-purpose NAT64, discard, Teredo, ORCHID, 6to4, SRv6, reserved, and deprecated
  site-local ranges.
* four-digit `1900::` through `2100::` forms without an address suffix, which are RST year targets.

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
Common CI, cloud, container, and documentation identities such as `gitlab-runner`, `ubuntu`,
`ec2-user`, `postgres`, `redis`, `nginx`, `laravel`, `alice`, and `bob` also stay visible.
Environment references such as `%USERPROFILE%` and the `~/` shorthand are outside this rule.
Relative documentation links, file-like path segments including `.zig` source names, numbered
placeholders such as `user1`, common example homes such as `sweet`, `build`, and `project`, and
paths inside `.docset` bundles are also kept.

### MAC addresses (`mac-address`)

Matches six hexadecimal pairs using one consistent `:` or `-` separator. The all-zero and
broadcast values stay visible. Canonical examples such as `00:11:22:33:44:55`,
`11:22:33:44:55:66`, and values beginning with `DE:AD:BE:EF` also stay visible. Cisco dotted
notation is intentionally not detected, and the boundaries reject substrings inside IPv6
addresses and UUIDs. Fixture values whose final five octets are zero are kept as well.

### International phone numbers (`phone-number`)

Matches a leading `+` immediately followed by a digit and 8 to 15 digits in total. Spaces,
hyphens, and parentheses may separate groups; the complete span is limited to 20 characters.
Dot-separated forms are intentionally not detected because they collide with decimal values
and added expressions in diffs.

Documented fictional ranges stay visible:

* NANP `+1-XXX-555-01XX`;
* UK drama ranges beginning `+44 7700 900` or `+44 20 7946 0`.
* common `555` fixtures and sequential NANP placeholders.
* numeric type boundaries (`2^n`, `2^n - 1`, and `10^n`) and published license contacts.

Short timezone offsets, diff hunk coordinates, `C++` tokens, and numbers longer than 15
digits do not match. Values with country code zero, date-shaped values, repeated-digit and
sequential placeholder numbers stay visible, as does a leading `+` at the start of a line in
`.patch` and `.diff` files.

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

* IPv4 values whose last octet is zero, whose octets repeat or increase sequentially, or whose
  octets contain leading zeroes stay visible because they usually represent product versions,
  canonical examples, or network identifiers rather than hosts. Adjacent package constraint
  operators and bounded same-line version/specification keywords also keep version-shaped values
  visible. Standard section headings inside comments are recognized when the first component is
  at most 43; this covers the practical C++/POSIX/JVMS section range but intentionally keeps some
  commented addresses from `1.0.0.0/8` through `43.255.255.255` visible. Changelog-family and
  dependency lock files, version-named files, and `v1.2.3.md`-style release notes disable IPv4
  matching because versions dominate there. A target following `->` stays visible as dependency
  resolution syntax; a real route written as `route -> 51.15.23.7` is therefore an intentional
  false negative. SVG asset files and SVG JSON catalogs disable IPv4 matching, while a nearby
  naked fraction is treated as SVG/path geometry; consequently a real address in an SVG asset or
  near text such as `.5` is an intentional false negative.
* Email-like tokens with a file-extension final label or a retina-style first domain label
  such as `icon@2x.png` stay visible. This intentionally accepts false negatives for colliding
  country-code domains such as `.md`, `.sh`, `.rs`, and `.py`, because file names using these
  extensions are substantially more common in project content. Two-letter country-code labels
  remain eligible; longer labels must be in the detector's bounded public/internal TLD policy.
* Placeholder local parts, every local part beginning with `your`, and organizational role
  mailboxes such as `admin`, `owner`, `support`, `security`, `sender`, `recipient`, `dev`, and `qa`
  stay visible. Single-character local parts are treated as test syntax.
  Attribution/contact files, package manifests and locks, publishing files, `.mdoc` mail macros,
  and same-line attribution contexts disable email matching because those addresses are
  intentionally published. Shell-variable and escaped local parts also stay visible. Bounded
  license banners require both copyright and a strong license phrase before suppressing published
  email and phone contacts.
  UUID Message-IDs, language
  binder/call syntax, URI authority userinfo, and malformed multi-`@` tokens are not treated as
  email. A real personal address using one of these forms is therefore an intentional false
  negative.
* An IPv6 candidate must contain at least one ASCII digit and either four non-empty textual
  groups or a group at least three characters long. This structural minimum rejects language and
  shell scope operators while preserving allocated global `2000::/3` addresses, whose first
  textual group has four hexadecimal digits. Valid shorthand outside that shape is an intentional
  false negative.
* A local username segment is limited to Unicode letters, ASCII digits, `.`, `_`, and `-` and
  cannot be entirely numeric. Slash-form anchors preceded by a letter or digit are treated as
  URL routes rather than local paths. Usernames outside that character set, numeric usernames,
  paths embedded in ambiguous route text, relative documentation links, file-like segments,
  domain-like home segments, `.docset` bundles, numbered placeholders, names beginning with `your`
  or `my`, and common CI, cloud, container, framework, or documentation identities are intentional
  false negatives.
* Canonical MAC examples, sequential hexadecimal fixtures, values with five trailing zero octets,
  and values beginning with `DEADBEEF` stay visible because they are overwhelmingly documentation
  fixtures; a real interface using one is an intentional false negative.
* Dot-separated international phone forms stay visible to avoid decimal-expression matches.
  Country-code-zero values, date shapes, repeated/sequential placeholders, common `555` fixtures,
  numeric type boundaries, license attribution, and diff-line markers also stay visible; real phone
  values using those forms are intentional false negatives.

## Limits and fail-closed behavior

Only decoded text files are inspected. Binary files are not inspected. A selected text file
larger than the configured 16 MiB inspection limit or using an unsupported encoding cannot be
silently included in a strict redacted output. Its content is withheld while analysis continues;
the UI and CLI report the count, path, and reason for every such file, and project copies record
the same omissions in their notice. Decoder and size limitations therefore cannot leak raw or
partially inspected text. Detector-budget and other inspection failures still stop strict output.

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
