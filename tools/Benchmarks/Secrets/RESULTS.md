# Secrets and content-pipeline results

Measured on 2026-09-09 on Windows 11, .NET SDK 10.0.401, on an Intel Core
i9-13900HX. The baseline is `fe210408c2cc038cd2c1b0adf61a9130bab52b13`;
the candidate includes the lazy rule-path correction and the measured rollback
of vectorized line indexing. BenchmarkDotNet used its default warm-up and
measurement policy with `MemoryDiagnoser`.

## Detector and line index

| Benchmark | Baseline mean (standard deviation) | Candidate mean (standard deviation) | Change | Baseline / candidate allocation |
|---|---:|---:|---:|---:|
| clean source | 467.9 us (26.0 us) | 458.9 us (14.8 us) | -1.9% | 344 B / 416 B |
| rejected-candidate noise | 1,122.4 us (27.7 us) | 1,032.5 us (16.3 us) | -8.0% | 432 B / 504 B |
| accepted findings | 4,327.0 us (236.8 us) | 3,077.5 us (80.7 us) | -28.9% | 161,880 B / 131,936 B |

The fixed 72-byte difference on the no-finding cases is retained in the table;
the accepted-finding corpus removes 29,944 bytes per detection. The confidence
intervals for clean source overlap slightly; no speedup is claimed there, only
the absence of the previously observed regression.

The rule-path change was also measured directly against the otherwise identical
pre-fix tree. Clean source moved from 465.6 us (23.5 us standard deviation) to
458.9 us (14.8 us); accepted findings from 3,087.5 us (140.5 us) to 3,077.5 us
(80.7 us); rejected noise from 1,018.2 us (14.3 us) to 1,032.5 us (16.3 us).
Those differences are within the observed distributions. The deterministic
counter is the deciding evidence: a clean file performs zero rule-path allowlist
evaluations, while matched rules evaluate each allowlist path only once.

The earlier line-index experiment measured LF -0.9%, CRLF -1.1%, and mixed
+2.5%, all within run spread. It was reverted to the simpler scalar traversal.

Stop-word lookup used the actual pinned lists (2 and 1,446 entries):

| Stop words / result | Linear `Contains` | `SearchValues` | Allocation |
|---|---:|---:|---:|
| 2 / absent | 8.612 ns | 4.752 ns | 0 B / 0 B |
| 2 / present | 10.143 ns | 13.901 ns | 0 B / 0 B |
| 1,446 / absent | 5,476.239 ns | 104.359 ns | 0 B / 0 B |
| 1,446 / present | 1,420.147 ns | 16.514 ns | 0 B / 0 B |

The detector retains `SearchValues`: the dominant absent case wins for both
real list sizes, and the large list wins in both branches.

The six detector changes were accepted independently as follows:

| Change | Measurement | Decision |
|---|---|---|
| defer match and capture materialization | accepted findings allocate 161,880 B at baseline and 131,936 B in the candidate | retained |
| defer line lookup until an allowlist needs it | rejected-noise builds zero line contexts and runs 1,122.4 -> 1,032.5 us for the complete candidate | retained |
| evaluate rule path allowlists on first match | zero evaluations on clean input; isolated timings overlap as recorded above | retained for eliminated work with no measured regression |
| use immutable stop-word search | absent 2-word case is 1.8x faster and absent 1,446-word case is 52.5x faster | retained |
| reuse group-zero matches | provider-shaped rules run zero secondary regex matches; rules with captures retain the reviewed path | retained |
| vectorize line-start discovery | -1.1% to +2.5%, entirely inside noise | reverted |

Operation-local diagnostics for the optimized detector were:

| Corpus | Findings | Secondary regex runs | Rejected line-context builds | Line-index builds |
|---|---:|---:|---:|---:|
| clean | 0 | 0 | 0 | 0 |
| rejected-candidate noise | 0 | 0 | 0 | 0 |
| accepted findings | 100 | 100 | 0 | 0 |

The safe whole-match shortcut is deliberately limited to rules whose compiled
regex exposes no capture group other than group zero. Other rules retain the
bounded second match.

## Real-process operations

One unmeasured warm-up preceded five alternating baseline/candidate runs against
the DevProjex tree. Peak working set was sampled while each process was alive.

| Operation | Baseline time, median (range) | Candidate time, median (range) | Baseline / candidate peak RSS | Interpretation |
|---|---:|---:|---:|---|
| `analyze --format json` | 5,473.91 ms (4,843.66-7,286.29) | 5,483.98 ms (4,704.49-6,575.66) | 521.07 / 519.55 MiB | +0.2%; distributions overlap |
| `export context --format markdown` | 5,596.14 ms (5,403.49-7,273.35) | 5,677.66 ms (5,056.86-6,654.00) | 521.84 / 523.96 MiB | +1.5%; distributions overlap |
| `export context --dry-run` | 5,062.91 ms (4,953.70-6,875.95) | 5,466.14 ms (5,009.98-6,854.42) | 520.38 / 521.86 MiB | +8.0%; distributions overlap |

The dry-run response was 302 bytes in both builds. Analyze and export responses
differed by one byte because the compared revisions format a progress/localized
line differently; content equivalence is covered separately by the redaction
contract tests rather than inferred from response size.

## Desktop metrics pipeline

The controlled provider measurement ran five times per build. The provider
counts source-version opens, repeated content reads, time waiting for the metrics
lock, and publication latency. Values are medians with min-max ranges.

| Files / changed | Baseline publication | Optimized publication | Opens (both) | Content rereads (both) | Lock wait (both) |
|---|---:|---:|---:|---:|---:|
| 100 / 0 | 15.580 ms (14.958-18.119) | 15.136 ms (14.555-18.956) | 101 | 0 | 0.001-0.034 ms |
| 100 / 1 | 15.308 ms (13.479-22.296) | 13.760 ms (13.448-15.584) | 204 | 1 | 0.001-0.002 ms |
| 20,000 / 0 | 45.273 ms (39.056-50.425) | 34.862 ms (30.601-46.599) | 20,001 | 0 | 0.001-0.003 ms |
| 20,000 / 1 | 46.140 ms (39.332-54.776) | 46.023 ms (40.099-52.416) | 40,004 | 1 | 0.002-0.003 ms |

The unchanged 20,000-file projection shows a 23.0% lower median, while the
changed case is effectively flat. The smaller cases overlap their ranges. A
compact prewarm fact accounts for 256 retained bytes per eligible file instead
of retaining its source string, still under the existing 64 MiB cap. Focused
tests pin latest-value progress coalescing, mandatory completion/cancellation,
generation checks, and release of retained facts.

## Correctness fingerprint

The baseline and optimized binaries produced identical ordered finding records
(`rule id`, offsets, length, and value):

| Corpus | Findings | Baseline SHA-256 | Optimized SHA-256 |
|---|---:|---|---|
| clean | 0 | `E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855` | same |
| rejected-candidate noise | 0 | `E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855` | same |
| accepted findings | 100 | `95C0611DA179F6CE95CE759D9C105C2BBDB6D1258080F7D2EA0B0A4EB673B8A4` | same |

The targeted contract matrix additionally covers identical placeholders and
redacted output, BOM and NUL handling, LF/CRLF boundaries, empty content,
surrogate pairs, bounded concurrency, cancellation, transient failures, and
lease cleanup.
