# Bounded declaration bodies

Measured on Windows x64 with the Release TerminalHost, against base
`af88c2bf47f9481858eda5d435c764183d4d1a1a`. No model calls were made.

For each repository, source files with 40–1,200 lines were sorted by path. Up to
30 evenly spaced files were selected before querying. Each file was searched for
`return|public|private|def |function|const |class ` with `context_lines=0` and
`max_results=200`. Inclusive declaration ranges came from the returned declaration
list; their original source lines were joined with LF to measure characters. The
sample includes fields, properties and enclosing types, not just methods. Only
declarations actually named by the bounded response enter the distribution.

| Repository commit | Declarations | Median characters | At most 1,800 | At most 3,000 |
| --- | ---: | ---: | ---: | ---: |
| serilog `1b461379f4e218a939d5c94897df2a1dbbf90573` | 172 | 201 | 151 (87.8%) | 159 (92.4%) |
| httpx `26d48e0634e6ee9cdc0533996db289ce4b430177` | 257 | 321 | 232 (90.3%) | 243 (94.6%) |
| hono `eebdf7be39abf0a872671835ccce0c4f03ea497a` | 172 | 273 | 159 (92.4%) | 167 (97.1%) |
| Total | 601 | — | 542 (90.2%) | 569 (94.7%) |

These are coverage of a deterministic bounded sample, not a repository-wide
census. They are higher than a method-only sample because many declarations are
short properties and fields. A 3,000-character body covers 27 more declarations
without increasing the 16,000-character search-content budget.

With an unrestricted root and `max_results=200, context_lines=0`, exact equality
changes the selected body as follows; hit and declaration order are unchanged:

| Repository commit | Pattern | Before | After |
| --- | --- | --- | --- |
| cobra `adbc8813901bba65827259daa8e22ff94ec1f30e` | `EnablePrefixMatching` | `command_test.go:TestEnablePrefixMatching` | Same |
| sinatra `cb22afd7902b566b6eaba6c4ea89739494a65d12` | `IndifferentHash` | `test/indifferent_hash_test.rb:TestIndifferentHash#test_slice` | `lib/sinatra/indifferent_hash.rb:Sinatra::IndifferentHash` |
| flask `d73fa1cdcbd8b1465c151db8924ba58b1dd14e35` | `SECRET_KEY` | `tests/test_basic.py:test_session_secret_key_fallbacks` | Same |

The remaining two cases do not expose a declaration with the requested variable
name: Go package variables and Python assignments are absent from the current
navigation projection. Equality cannot select an entity missing from that
projection. This change does not expand extraction or dependency facts.

## Startup body-limit comparison

Recorded on `1515c4a3b27dd92196943b241fcb6b3938267cca` using one binary,
four tasks and three repeats per task in each of three startup configurations:
36 sessions in total. This recorded comparison determined the new default of
1,800 characters. No new model sessions were run for the default change.

| `--search-body-chars` | Model turns | Cost (USD) | Complete answers | Required paths not named | Incorrect answers |
| --- | ---: | ---: | ---: | ---: | ---: |
| `off` | 173 | 1.27 | 2 / 12 | 22 / 54 | Not reported |
| `1800` | 161 | 1.21 | 5 / 12 | 14 / 54 | Not reported |
| `3000` | 179 | 1.38 | 2 / 12 | 30 / 54 | 1 |
