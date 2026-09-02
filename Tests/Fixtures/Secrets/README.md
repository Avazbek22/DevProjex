# Secret detection corpus

`gitleaks-v8.30.1-corpus.jsonl` is a compact test fixture derived from the
generated positive and negative rule cases in the pinned Gitleaks v8.30.1 source.
It exists to detect semantic drift between the reviewed RE2 rules and the managed
.NET port used by DevProjex.

- Upstream source: https://github.com/gitleaks/gitleaks/tree/v8.30.1/cmd/generate/config/rules
- Configuration: https://github.com/gitleaks/gitleaks/blob/v8.30.1/config/gitleaks.toml
- License: MIT; see [`THIRD-PARTY-NOTICES.md`](../../../THIRD-PARTY-NOTICES.md)

The values are upstream-generated test data, not production credentials. Payloads
are stored as UTF-8 Base64 in `contentBase64` and decoded only by the corpus test.
When push protection recognizes an encoded negative fixture as a credential, its
Base64 is split into `contentBase64Parts` and joined only in memory by the test.
This preserves the exact upstream cases without making a DevProjex source checkout
look like it contains more than one hundred secrets. A rule update must regenerate
this fixture from the newly pinned source and review every changed expectation. Do
not make the corpus pass by deleting a case or by changing an expected result
without documenting the corresponding upstream rule change.

The encoded fixture remains covered by GitHub Secret Scanning. Keep synthetic
credentials encoded at rest and out of ordinary test source files so the whole
repository remains protected.
