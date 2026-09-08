# Secrets and content-pipeline results

Measured on 2026-09-09 on Windows 11, .NET SDK 10.0.401, on an Intel Core
i9-13900HX. The baseline is `fe210408c2cc038cd2c1b0adf61a9130bab52b13`;
the optimized product tree is `906da078`. BenchmarkDotNet used three warm-up
iterations and five measured iterations. Values below are medians; the range
or standard deviation is included so differences inside host noise stay visible.

## Detector and line index

| Benchmark | Baseline median | Optimized median | Change | Baseline / optimized allocation |
|---|---:|---:|---:|---:|
| clean source | 529.6 us | 628.5 us | +18.7% | 416 B / 416 B |
| rejected-candidate noise | 1,567.2 us | 1,317.5 us | -15.9% | 504 B / 504 B |
| accepted findings | 3,656.0 us | 4,082.1 us | +11.7% | 131,936 B / 131,936 B |
| line index, LF | 247.2 us | 245.1 us | -0.9% | 342,701 B / 342,699 B |
| line index, CRLF | 305.5 us | 302.2 us | -1.1% | 342,701 B / 342,689 B |
| line index, mixed | 277.2 us | 284.2 us | +2.5% | 342,707 B / 342,688 B |

The clean optimized run had a 149.9 us standard deviation versus 18.5 us at
baseline; the accepted-finding run was 256.4 us versus 78.2 us. Those two
regressions are therefore retained as observations, not claimed as stable
effects. All line-index differences are within the measured spread. The
rejected-candidate corpus was the intended hot path and improved while retaining
the same allocation reading.

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

One unmeasured warm-up preceded five alternating baseline/optimized runs against
the DevProjex tree. Peak working set was sampled while each process was alive.

| Operation | Baseline time, median (range) | Optimized time, median (range) | Baseline / optimized peak RSS | Interpretation |
|---|---:|---:|---:|---|
| `analyze --format json` | 8,623.81 ms (8,230.73-12,603.79) | 8,012.67 ms (7,755.92-8,463.86) | 517.61 / 519.20 MiB | -7.1% time; RSS unchanged within 0.3% |
| `export context --format json` | 8,847.50 ms (7,984.35-9,677.31) | 9,250.56 ms (8,219.17-10,755.82) | 518.38 / 516.53 MiB | +4.6% time, within run spread |
| `export context --dry-run` | 7,913.79 ms (7,345.99-8,966.16) | 7,947.32 ms (6,876.98-8,272.02) | 513.63 / 511.97 MiB | +0.4% time, within noise |

The dry-run response was 304 bytes in both builds. Analyze and export responses
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
