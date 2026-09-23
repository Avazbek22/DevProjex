# v5.2 stabilization: local verification report

Status: draft, 2026-09-23. This report records local evidence, not release approval.

The stabilization baseline is `dfcaff70e7407457378aee07a29f158aad0306c5`. The
combined backend comparison extends through `0ec72b78`; later profile-load verification
(`4b5b3520`), root-facts profiling (`6d79b72f`), and partial-selection compression-fact
reuse (`01b3fac1`) are recorded separately.
Final cache-publication safeguards (`b9fc735a`) do not change those timing comparisons.
The backend task does not include a real desktop run; desktop readiness remains a
separate release check.

## Scope and method

The work preserves v5.2 selection, filtering, ordering, export, and content-processing
contracts. Changes target confirmed cancellation/lifetime defects and measured backend
costs. Fixed animation delays and UI pacing are outside scope.

- Local execution used Windows and .NET 10, Release builds, targeted test filters, and
  `-m:1 -p:BuildInParallel=false`. Build/test/benchmark execution was serialized.
- Comparisons use the same workload within each isolated benchmark. Warm-ups precede
  measured samples; elapsed values below are medians unless explicitly identified as ranges.
- The real-project probes read the DevProjex repository and isolate application data.
  They exercise backend components without opening a desktop window or playing animations.
- Allocation numbers describe managed allocations in the measured region, not retained
  memory, native memory, process working set, or peak resident memory.
- Different rows measure different phases and revisions. Their improvements are not
  additive and must not be multiplied into a whole-application speedup claim.

## Real DevProjex combined backend A/B

The primary evidence is [the recorded backend comparison](Benchmarks/v5.2-gui-backend.json):
baseline `dfcaff70` versus `0ec72b78`, .NET SDK 10.0.401 / runtime 10.0.12, on the same
frozen physical DevProjex working tree. The workload includes the then-pending regression,
probe, and report files. Compression and redaction are disabled; default GUI selection
is applied. Source writes were suspended throughout the comparison.

The probe runs the actual `SelectionSyncCoordinator` default snapshot, projection from its
captured inventory, selection publication, and `MetricsPipeline` through completion of
background publication. It excludes window construction, profile persistence, animation
gates, and first paint. Baseline uses its historical `InvalidateCaches` reload path; the
comparison uses the new `RefreshDiscoveryCaches` path.

Each version ran in two independent processes, with 31 consecutive loads per process.
Run 0 is reported separately. The primary medians pool **all 60 Run 1–30 observations per
version**, rather than selecting only the fastest late iterations. The filesystem cache
was not purged. All six medians were independently recomputed from the stored samples.

| Measurement | Baseline | Comparison |
| --- | ---: | ---: |
| Total measured backend time, warm median | 355.9716 ms | 147.9872 ms |
| Total backend managed allocation, warm median | 164,001,744 B | 38,615,824 B |
| Selection computation, warm median | 215.9249 ms | 64.1271 ms |
| Inventory projection, warm median | 34.7532 ms | 21.7336 ms |
| Metrics scan, warm median | 37.7347 ms | 27.8773 ms |
| Metrics through background publication, warm median | 103.5926 ms | 58.4953 ms |
| Additional file-version opens, constant in all 62 runs per version | 10,419 | 5,853 |

Metrics-publication elapsed time already includes the metrics scan. Total backend time
also includes selection publication. Individual phase medians must not be added together
to reconstruct the median of the per-run total.

The recorded invariant audit confirms identical counts and published values across all
124 observations: 2,712 files, 2,997 inventory entries, 2,283 full content reads, and
28,407,252 content bytes. Git mode remained `RespectGitIgnore`, with the same ignore-option
selection. Fewer metadata opens did not reduce the required content reads.

| Published metric | Identical value in baseline and comparison |
| --- | ---: |
| Tree lines | 2,981 |
| Tree characters | 121,200 |
| Tree estimated tokens | 30,300 |
| Content lines | 688,380 |
| Content characters | 27,075,656 |
| Content estimated tokens | 6,768,914 |

First-load observations in the two fresh processes were 1,052.2072 / 906.1383 ms for the
baseline and 834.8240 / 765.9538 ms for the comparison. These are only two observations per
version, with an unpurged filesystem cache, not a statistically established cold-disk result.

Earlier short runs were unstable: four processes with five warm samples each produced
pooled total medians of 483.1109 → 319.8766 ms and an apparent projection regression of
42.1839 → 97.1053 ms. That phase result did not persist in the longer primary runs above.
Early-iteration volatility is visible, but its cause was not isolated; this report does
not assert that JIT/tiering caused it. The longer, fully recorded comparison supersedes
the short samples as the primary backend result.

Earlier component probes localized expensive repeated ignore-matcher construction and
sequential metrics freshness checks. The final result combines content-validated matcher
reuse (`e068c986`), simple suffix-rule construction (`3d504635`), bounded parallel freshness
checks (`49280fff`), and stable content-identity reuse (`cd1339c0`, `0ec72b78`). This combined
A/B does not independently attribute a percentage of the total improvement to each change.
It does not establish that historical approximately 100 ms behavior has been restored or
that the full desktop becomes ready in approximately 148 ms.

## Isolated comparative measurements

These measurements identify particular hot paths. They are not desktop load-time claims.

| Workload | Before → after time | Before → after managed allocation | Notes |
| --- | --- | --- | --- |
| Preview metrics, 199,900 selected lines from a 200k-line Unicode/CRLF document, file-backed | 59.191 → 12.740 ms | 41,579,200 → 120 B | Exact line, character, and token counts preserved |
| Same preview-metrics workload, in memory | 4.814 → 2.228 ms | 41,579,200 → 120 B | Construction excluded; warm filesystem cache for the file-backed variant |
| Sparse context projection, 100k files, one selected file | 9.280 → 6.072 ms | 801,688 → 1,336 B | Retained child-list capacity 100,000 → 2; path/order parity checked each iteration |
| Context projection, 100k files, 50k selected files | 20.095 → 15.865 ms | 10,981,816 → 8,316,400 B | Dense-selection control alongside the sparse case |
| Inventory projection, 100k files / 1k sibling ignore scopes | 158.994 → 4.297 ms | 3,811,896 → 4,278,152 B | **Allocation increases by 466,256 B** for the operation-local scope index |
| Inventory projection, 10k files / 1k scopes | 40.383 → 1.355 ms | Not used as an allocation claim | Synthetic scope-heavy workload, not this repository's scope distribution |
| Preview control clipboard, 100k lines, file-backed | 91.948 → 15.886 ms | 96,400,200 → 36,809,344 B | Clipboard still requires the resulting text; existing newline behavior preserved |
| Preview control selection, 100k lines, file-backed | 28.277 → 11.023 ms | Not recorded here | In-memory selection: 10.524 → 6.101 ms |
| Search context merging, 5k candidates / 20 context lines | 91.193 → 7.941 ms | 2,674,392 → 1,282,416 B | Paths, match counts, line markers, and Unicode text verified |
| Search context merging, 5k candidates / zero context lines | 76.542 → 0.664 ms | 2,327,472 → 938,408 B | Removes repeated boundary enumeration; no provenance/ranking changes |
| Ignore matcher construction, actual 435-line root `.gitignore` | 106.8596 → 54.7221 ms | 112,743,112 → 62,657,280 B | `3d504635`; 97 eligible simple ASCII suffix rules avoid regex construction |
| GUI profile snapshot load, 1,000 active marks across 20 projects | 39.8786 → 18.7672 ms | 8,257,256 → 4,127,920 B | `4b5b3520`; isolated profile phase, not whole-project loading |
| GUI profile snapshot load, 10,000 active marks across 20 projects | 119.3714 → 59.6512 ms | 99,823,672 → 49,919,912 B | Same operation-local marks snapshot reused; no persistent cache |

The suffix benchmark measures `GitIgnoreMatcher.Build` only: ignore-file reading is outside
the timed region, followed by one warm-up and seven samples. It does not time per-file
matching or project loading. A same-run forced-regex control measured 117.8306 ms /
112,753,224 B versus 54.7221 ms / 62,657,280 B for the optimized implementation.

### Profile marks: remove a duplicate state-document load

The profile fixture measures `ProjectProfilePersistenceCoordinator.LoadSnapshotAsync`
with 20 projects, 1,000 or 10,000 active marks in total, and one tombstone per project.
Primary and backup JSON documents are each 219,602 B or 2,160,722 B respectively, below
the existing 8 MiB bound. Each case runs six loads; the table uses the median of the five
warm observations.
This measures profile loading only, which the combined backend A/B explicitly excludes.

State-document parses drop from two to one and explicit `LoadMarksAsync` calls from one
to zero. The parse counter observes the state-document loader; it excludes separate
primary/backup schema probes and is not a count of all physical reads. Loaded revisions
remain 51 and 501, with active marks and tombstone revision metadata preserved.

The successful profile lookup carries the already detached marks snapshot returned by
legacy migration. GUI consumption follows the existing status, recovery, cancellation,
and identity-readiness gates. Missing/custom lookup results retain the independent marks
fallback; subsequent lookups remain fresh. The operation-local payload is excluded from
JSON serialization. No persistent cache, storage format, or new store interface is added.

### Compression-enabled loading and partial-selection reuse

[The transformed-load samples](Benchmarks/v5.2-gui-transformed.json) record two separate
read-only diagnostics on DevProjex. Both use body compression with redaction disabled,
one window-lifetime compression session, and a fresh metrics pipeline per load. Session
construction is outside the timed loop; native cold preparation is included in Run 0.
These short diagnostics do not replace the longer raw-load baseline comparison above.

The first diagnostic, before `01b3fac1`, used full selection: 2,716 tree files and
2,287 full content reads per preparation pass. Existing full-selection reuse was healthy:
the following metrics phase performed **zero** full reads, fingerprints, or native analyses.
One session-cold preparation took 2,058.2621 ms with 1,785 native analyses. Three warm
preparations took 84.9848 / 78.1575 / 85.9329 ms, each with 1,785 cache hits and zero
native analyses. Required source reads for fingerprint freshness remain. Estimated retained
budget counters were 585,472 B for compact facts before metrics and zero afterwards;
the compression cache counter was 4,649,616 B, below its existing 64 MiB bound. These are
budget estimates, not measured retained memory or working set. This is current-only profiling,
not evidence that this change improved cold preparation.

A partial selection exposed a different path: preparation retained facts for selected
files, but metrics initialization rejected the entire snapshot because it scans the full
tree. `01b3fac1` validates the retained selection against the current root instead. The
existing scan then consumes only current-tree files and checks each fact's transform
identity and source version. Final freshness/publication checks, cache bounds, cancellation,
and full-tree metrics population are unchanged; no new persistent cache was introduced.

The second diagnostic uses one final binary and one frozen corpus: 2,717 tree files,
1,359 selected paths, and 1,143 selected text files. It compares normal retention with
an explicit `ReleasePostLoadReadFacts` immediately after preparation. The discarded-facts
control models the old rejection, but **is not historical-binary release A/B timing**.
After one cold-session priming load, three warm pairs ran in D/R, R/D, D/R order.

| Metrics phase, three observations per variant | Discarded facts control | Retained facts |
| --- | ---: | ---: |
| Full content reads per pass | 2,288 | 1,145 |
| Full content bytes per pass | 28,518,607 B | 13,446,049 B |
| Selected supported / unsupported content stream opens | 897 / 246 | 0 / 0 |
| Unselected supported / unsupported content stream opens | 889 / 256 | 889 / 256 |
| Content fingerprints per pass | 1,786 | 889 |
| Native analyses in warm passes | 0 | 0 |
| Time through metrics publication, median | 80.1720 ms | 56.9065 ms |
| Managed allocation, median | 108,635,864 B | 54,307,808 B |

All seven observations retain all 2,717 file-cache entries and publish identical six-metric
tuples. Preparation reads the same 1,143 selected text files in each observation; subsequent
metrics avoid rereading those files, removing 15,072,558 B of repeated content IO. Retained
facts are released after consumption. Stream-open counts and full-read counts are separate
diagnostics and must not be added. The robust result is eliminated repeated work; timings
are only three-observation warm medians, not a new whole-GUI speedup claim.

### Deferred: broader root-facts reuse

The read-only `6d79b72f` probe used the actual repository at 3,001 inventory entries,
with one initial run and seven warm samples. It observed 412 root-facts builds over
231 unique paths: 194 during initial availability, 181 repeated during selected-root
discovery, and 37 new builds during scanning. There were no cache evictions.

Repeated builds account for a median 20.8598 ms of summed elapsed work, 6.9793 ms of
union wall-clock coverage, and 1,369,744 B allocated inside the facts builder. Parallel
elapsed durations overlap: neither duration is a measured or guaranteed saving from
an unimplemented optimization. Broader operation-scoped reuse was deferred before
release because it requires additional lifetime/invalidation design for this measured cost.

Scanner hooks reported 547 events over 273 unique paths, including 182 paths shared
with facts discovery (363 facts builds). These hooks are not physical syscalls and can
observe retained batches. Existing scan diagnostics instead recorded 344 combined,
two directory, and one file enumeration. These counters must not be added together or
presented as complete duplicated workspace IO. This investigation changed only the probe,
not the production discovery/cache architecture.

### Journal retention fast path at GUI startup

GUI composition constructs `AgentJournalStore` unconditionally, before creating the window.
Its constructor synchronously performs the retention sweep, even when the activity
panel is disabled. It reads the live-session registry and, when cleanup may be needed,
validates journal headers. This is startup work, not part of ordinary project reload
or the main backend A/B above.
No accidental startup of the MCP server, TUI, or dependency-facts engine was found on
the normal GUI route. Desktop IPC and live-session monitoring serve existing contracts.

The opt-in `cf78e962` probe measures isolated synthetic journal directories, never actual
user journals. [All twelve observations are recorded](Benchmarks/v5.2-journal-startup.json).
Default retention remains 200 sessions / 30 days; valid recent fixtures are restored
outside timing. Each fixture has one first observation and three repeated observations
in the same process. Setup warms filesystem data and serializer metadata, so these are
not process-cold or disk-cold measurements.

| Valid journal headers presented | Repeated constructor median | Current-thread managed allocation, median | Files remaining |
| --- | ---: | ---: | ---: |
| 0 | 0.4119 ms | 11,840 B | 0 |
| 200 | 19.2530 ms | 2,334,096 B | 200 |
| 512 | 66.4650 ms | 5,953,672 B | 200 |

Timing includes the synchronous constructor and retention sweep, not disposal or fixture
setup. The 512-file case includes 312 normal retention removals per observation; this is
not a pure header-read comparison. Presented-file counts are not measured syscall/read
counters. First observations were 5.4377 / 30.6462 / 74.5276 ms respectively.

The retention sweep now skips journal-header reads only when the physical `.jsonl` file
count is at or below the configured maximum and every file has a recent write time.
No verified subset can be eligible for deletion in that case. A reparse point, unreadable
metadata, an expired file, or an excess count takes the existing validation/deletion path.
The live-session registry is still read; cleanup is neither deferred nor rescheduled.
The [follow-up samples](Benchmarks/v5.2-journal-startup-fastpath.json) used the same
isolated fixture and Release test harness, but are not an interleaved A/B run:

| Valid journal headers presented | Previous warm median | Fast-path warm median | Previous / new current-thread allocation |
| --- | ---: | ---: | ---: |
| 0 | 0.4119 ms | 0.4205 ms | 11,840 / 11,760 B |
| 200 | 19.2530 ms | 5.9497 ms | 2,334,096 / 75,216 B |
| 512 | 66.4650 ms | 67.9844 ms | 5,953,672 / 5,953,704 B |

The 200-file case is about 3.2x faster and allocates about 31x less in this synthetic
constructor workload. The 512-file control retains the full cleanup behavior. These
numbers do not establish a whole-GUI startup improvement or explain the reported
whole-second project-load delay. Targeted journal retention tests and the opt-in probe pass.

### Current normal-folder load calibration

The opt-in `GuiBackendLoadMeasurementTests` now uses the production folder-refresh path,
`RefreshDiscoveryCaches`, instead of fully invalidating compiled ignore matchers on
every iteration. The earlier form of this local probe overstated normal warm reload cost;
it did not indicate a regression in the production load path. The [six recorded samples](Benchmarks/v5.2-gui-backend-normal-folder-load.json)
on the current DevProjex tree contain 2,723 selected files. The first headless backend
load took 695.49 ms; subsequent loads took 250.25, 181.48, 113.52, 110.52, and
102.29 ms as the process and filesystem warmed. Background metrics publication took
99.89 ms on the first pass and 60.95–84.34 ms thereafter. These phases exclude first
paint and the existing visual quiet-period gate; they must not be presented as desktop
ready times or added to the historical v5.0/v5.1 recollection.

A later current-only headless rerun, compiled from `124b3be5` on .NET 10.0.12, used the
then-current DevProjex tree (3,028 inventory entries; 2,742 selected files) and six
backend loads. The first load through metrics publication took 804.92 ms; the five warm
paired totals were 286.70, 177.89, 172.54, 163.92, and 149.08 ms (median 172.54 ms).
Each pass made one workspace scan, one dynamic pass, and 2,313 full content reads totaling
28,920,131 B, with 5,913 file-version opens; metrics publication had a 66.50 ms warm
median and 3,703,448 B median managed allocation. Composition was measured separately
at 73.91 ms. A second opt-in selection probe confirmed the same read/version counters
and a 177.50 ms warm paired-total median. These are different corpus and phase
samples from the frozen A/B and the earlier 2,723-file calibration, not evidence of a
further speedup or full desktop readiness. The probes left no persistent fixture folders.

One further current-only `--no-build` headless pass on the 2,745-file DevProjex tree
reported a 699.8 ms first backend load plus 90.7 ms metrics publication. Its last three
warm loads were 99.1, 114.5, and 106.9 ms; their separate metrics publications were
63.3, 77.8, and 57.8 ms. Each pass made 2,316 full text reads (about 29.0 MB) and
5,919 version-handle opens. The corpus and environment changed again, so this is a
current calibration, not a new A/B improvement or desktop-ready time.

The root `.gitignore` has 435 lines. An isolated cold matcher build took 49–96 ms and
allocated about 62.7 MB; the matchers were reused during the same scan. A targeted
`RegexOptions.Compiled` A/B did not establish an end-to-end benefit, so the matcher
implementation and fixed UI timings remain unchanged.

### TUI backend content metrics on demand

The terminal workspace now opens without computing full content-output metrics that are
not displayed by the initial TUI tree. An explicit request for the full plan still
computes those metrics. [Two recorded real-root runs](Benchmarks/v5.2-tui-deferred-content-metrics.json)
used alternating full and deferred backend paths, each with one warm-up pair and five
measured pairs. The included corpus grew by one file between runs, so their timing
distributions are reported separately rather than pooled.

| Run | Included files | Full-path warm median | Deferred-open warm median | Full reads during deferred open |
| --- | ---: | ---: | ---: | ---: |
| Initial | 2,728 | 135.13 ms | 114.56 ms | 0 instead of 2,299 |
| Verification | 2,729 | 134.58 ms | 128.84 ms | 0 instead of 2,300 |

In both runs, deferred open also avoided one content-metrics call per included file.
An on-demand follow-up made the expected reads, reproduced all output metrics, and
produced byte-identical structured Tree JSON in the checked sample. Median process-wide
managed allocations were essentially unchanged. The medians differ by 4–15% across
the two runs and do not establish a stable whole-TUI speedup. The timings exclude
terminal rendering and application readiness.

## MCP process A/B: no broad speedup established

A real stdio MCP process comparison used a 20k-file synthetic corpus, one warm-up, and five
measured calls per operation. Effective default selection still applies: this does not mean
that every operation reads all 20k content files. This checkpoint preceded later GUI and
matcher work; it is not a final aggregate comparison of the complete change set.

| Operation | Baseline median | Comparison median |
| --- | ---: | ---: |
| Tree | 9.125 ms | 8.064 ms |
| Analyze | 1,112.194 ms | 954.874 ms |
| Search | 1,020.400 ms | 1,022.773 ms |
| Narrow | 4.768 ms | 4.957 ms |
| Related files | 290.292 ms | 290.442 ms |

The results do not establish a general MCP acceleration. Response lengths matched across
the compared runs; equal lengths alone are not proof of byte-identical responses. Client
allocations from this harness are not server allocations. The isolated search-merge result
above must not be substituted for an end-to-end MCP speedup.

A separate current-only Release process probe on the real DevProjex root gave warm medians
of 372.200 ms for `search_project`, 15.051 ms for narrow `get_file`, and 16.258 ms for
wide `get_file` across five calls each. These absolute values are not a baseline/current
comparison, and no further search-path change was justified from them.

## Correctness and targeted verification ledger

Counts below overlap across runs and must not be summed into a unique-test total.

| Change | Defect or contract checked | Local verification |
| --- | --- | --- |
| `5ad6f179` | Cancellation before text/preview export must preserve existing destination bytes and stream position | Four new baseline failures; 15/15 targeted passes |
| `04aed385` | GUI selection-persistence disposal must not release a disposed semaphore | Reproduced `ObjectDisposedException`; 14/14 passes |
| `7761ce1a` | Terminal persistence must drain active writes and skip superseded/discarded flushes | Six baseline failures; 16/16 passes |
| `fd27148e` | Stream preview selection metrics without per-line strings | 10/10 passes; exact output metrics |
| `ff6f4abe` | Reuse included nodes and bound sparse child-list capacity | 32/32 targeted passes; benchmark checks path/order equality |
| `9a408917` | GUI preview/export projection must stop traversal on cancellation | Ten new baseline failures; 30 Unit + four headless UI passes; node visits 10,000 → 1 |
| `571e11db` | Scope inventory rules to ancestors while preserving nested repositories, duplicate/aliased scopes, disabled modes, and ordering | 22 Unit + 68 Integration passes; 11 platform skips |
| `3a740704` | Stream preview control selection/clipboard without per-line copies | 13 passes; Text fallback LF and Document native-newline contracts retained |
| `a4a4a9f1` | Whole-tree compression preparation reuses ordered paths | Eight passes; explicit-root control traversals 2 → 0 with both files still prepared |
| `796b6854` | Failed/canceled/noncacheable dependency generations must not leak eviction bookkeeping or evict a newer same-key entry | 18 Unit + 14 Integration passes; one platform skip; after 100 failed retries, stale order entries 100 → 0; recovered-key extraction count 5 → 4 |
| `eb5a11cc` | Canceled preview range/search reads must not wait for a concurrent blocked export | Two baseline timeout failures; 25 targeted passes; matching cancellation token observed before export completed |
| `ed2d0455` | Project export skips unused upfront analysis while dry-run retains it | Three baseline read-count failures; 22 passes / two platform skips; fixture removes two full reads / 131,103 B, output bytes preserved |
| `f1433e76` | Dependency preflight and manifest-cache reuse honor cancellation | Six baseline failures; 24 Unit + nine Integration passes / one platform skip; pre-cancel manifest visits 10,000 → 0, mid-cancel visits 10,000 → 1, absent-control probes 100 → 1; hash bytes preserved |
| `8e6dfcfd` | Text/Markdown context exports skip unused upfront metrics; JSON/XML and dry-run retain them | Ten baseline read-count failures; 46/46 passes; output/budget parity retained |
| `2dbe51a4` | Search context merge avoids quadratic boundary enumeration | 49/49 passes; counts/provenance/ranking unchanged |
| `49280fff` | Independent metrics freshness probes run with bounded concurrency, preserving order/cancellation and cache-generation checks | 39/39 targeted passes |
| `cd1339c0` | Stable identity requires matching metadata before/after the same content handle; public and legacy after-read results remain unchanged | 125/125 targeted passes, including 22 new coherence cases, existing analyzer coverage, and prewarm/GUI reuse controls |
| `e068c986` | Normal reload refreshes discovery but reuses only content-validated compiled ignore matchers | One baseline reuse failure + seven controls; 41/41 after passes; same-metadata rewrites, nested additions/removals, Smart Ignore, semantic changes/unavailability, and cancellation covered |
| `3d504635` | Simple ASCII ignore-suffix fast path preserves matching semantics | 255 Unit passes / three skips, including 33 new cases; six native-Git parity passes |
| `0ec72b78` | Raw GUI metrics reuse stable same-handle identity while retaining final path/publication checks and conservative fallbacks | 55/55 targeted passes, including 16 new cases: seven initial IO-counter failures, seven controls, and two cancellation checks |
| `4b5b3520` | GUI profile load reuses its successful lookup's marks snapshot without losing revision, recovery, cancellation, or fallback semantics | One initial duplicate-load failure + four controls; final 102 targeted passes / two opt-in skips, including eight new correctness cases; separate enabled benchmark run: two passes |
| `6d79b72f` | Measure repeated root-facts work before considering broader session reuse | One enabled read-only probe passed; production optimization deferred, not claimed as a speedup |
| `01b3fac1` | Reuse selected compression facts while populating metrics for the full tree | Two baseline read-count failures + five passing controls; 37/37 after passes, including seven new cases and existing freshness, coherent-read, visual-gate, and cancellation checks; two separately enabled transformed-load probes passed |
| `63b8fd0b` | An active v1 write and queued v1 flush must not discard a newly scheduled v2 selection | Two new deterministic races; GUI persistence class 15/15 and Terminal persistence class 17/17 passed; exact v1/v2 writes and final idle state checked |
| `3449e03d` | Resolution-cancellation tests must reach an admitted cache entry, not cancel during earlier preflight | Test-only correction; 13/13 passes, with entry/node existence, unregistered weight, cancellation stage, and token asserted |
| `b9fc735a` | A retired successful resolution must not replace the manifest snapshot of a newer same-key cache entry | One deterministic baseline failure; 23 Unit + 10 Integration passes after, including public facts/edges/coverage equivalence, hot reuse, budgets, configuration invalidation, and manifest gates |

An additional cross-surface run covering selection contracts, MCP inventory caching, and
inventory projection passed 31/31 tests.

After `d7acb762`, a final interaction-focused batch passed all **146 selected checks**:

- 60 Unit checks for coherent metrics reads, bounded freshness concurrency, discovery
  refresh, profile/selection persistence, cancellation, and unchanged visual gating.
- Nine headless UI checks for latest-project publication, rapid selection cancellation,
  ignore-filter round trips, window shutdown, detached event handlers, and canceled
  preview traversal.
- 44 Integration checks for cross-surface selection, MCP inventory, dependency-cache
  generations/budgets, coherent source reads, redaction, and search completeness.
- 33 Terminal checks for export read counts, snapshot/read-ahead cancellation, output
  integrity, and real-process discovery/search/read/pack workflows.

These are targeted checks, not a full-suite run or 146 newly added tests. UI execution
used the project's Microsoft Testing Platform method filters; other projects used
VSTest filters. Existing unrelated xUnit analyzer warnings were not changed.

The dependency changes preserve cache budgets, exact-generation removal, and separate
parse/resolution invalidation. Stable content identities preserve the existing `Identity`
observation for export/redaction consumers and add an opt-in `StableIdentity`; public
reader wrappers do not pay for an unused before-read metadata capture. Prewarm retention
uses the stable identity. Atomic replacement still requires a final path-version check.

The GUI fast path is limited to the concrete `FileContentAnalyzer`, raw untransformed
reads, and the default physical version provider. Custom analyzer wrappers, custom version
providers, transformed/retained metrics, and known-binary handling retain their legacy
paths. A changed handle identity is rejected. A rare unidentified result falls through to
a fresh legacy public read between before/after version checks; its old bytes are never
assigned a newly observed path stamp. This fallback can require two content reads and is
intentionally conservative.

### Dependency manifest snapshots: preserve generation ownership

A deterministic interleaving reproduced an existing retention defect: resolver A was
evicted while in flight, resolver C completed under the same key, then A finished and
replaced C's manifest snapshot with its own graph. The index cache still held C, so both
graphs remained reachable through cache-owned structures while the index budget accounted
for only C. Snapshot count was already bounded: this is not an unbounded leak or a proven
stale-output defect, and some underlying file facts can be shared.

`b9fc735a` carries the actually used index entry to manifest publication. Under the existing
cache lock, publication must still belong to that exact entry before replacing a snapshot.
The retired request returns its own result normally; it cannot replace the newer cache's
snapshot. Root, manifest, file-stamp, content-identity, absent-control-file checks, and
cache-hit reporting remain intact. No extra cache or production test hook was added.

The original baseline fixture showed distinct retired/current graph identities with one
accounted index. The final regression adds a nonempty dependency edge and verifies public
facts, edges/maps, coverage, root, manifest/declaration identities, cache weights, and hot
reuse. Fixture sizes therefore are not a before/after memory benchmark. The demonstrated
improvement is correct cache ownership, not a quantified reduction in working set.

## Additional release-stabilization checks

The following changes came after the frozen GUI backend A/B above. Their targeted checks
do not change or extend that historical timing comparison. Test counts overlap; they are
not a unique-test total.

| Area | Commits | Targeted verification and contract |
| --- | --- | --- |
| GUI startup and settings | `0cf8d861`, `76b6b556`, `1a569854`, `7f1e5f82`, `c8b7c40e`, `77f4a5b6` | Optional IPC, session watcher, and agent-activity storage failures no longer block startup. Git version probing leaves the constructor path. Transient settings failures preserve prior state; concurrent windows merge update history and avoid stale update indicators. Update coordinator 25/25; final preference/headless startup checks 4/4. |
| GUI selection and project history | `17d49cf4`, `6d0d6a80`, `19847436`, `169cfb67` | Unreadable recent-project state does not erase history; hidden marks survive legacy option profiles; a flush drains changes scheduled during it; replaced scope cache entries cannot publish stale results. Targeted 45 Unit + 15 Integration for history, 13 Unit for marks, 16 Unit for flush, and deterministic scope-race tests. |
| MCP inventory and source safety | `86747971`, `fa8a2271`, `7f9edfac`, `8af6c6b1`, `2433384a`, `06ee8f2a` | Stored results are tied to the opened file handle, a genuinely empty `get_file` read remains distinguishable from withheld content, search reports partial counts, and one canceled inventory waiter cannot cancel another. Live `list_projects` and `read_pack` no longer change an active search's revision. Published filter descriptions match accepted schema keys. Inventory cancellation/invalidation 8/8 Integration plus 1/1 real-process check; live read/list races and queued cancellation 3/3; descriptor contract 2/2. Native identity behavior was locally exercised on Windows only. |
| Prepared redaction | `040e0c96` | An unscannable source that later becomes readable cannot leak uninspected text through the prepared analyzer or bounded JSON output. Five targeted regressions failed before the fix; final targeted Unit checks 12/12. Original TooLarge estimates are retained. |
| Dependency configuration | `2ca556a9`, `a54a705f` | Invalid TypeScript, C#, CMake, Cargo, and Ruby paths yield explicit unsupported-configuration diagnostics instead of exceptions. The Cargo/Ruby regressions failed 3/3 before and passed 3/3 after, with five adjacent controls. Parsing and resolution cache ownership remain separate. |
| TUI initial plan and settings | `8a2d5632`, `6d4be2dc`, `7abbf120` | Content-output metrics are deferred until requested while selection marks and structured output remain identical. Transient settings reads no longer replace saved history/language with defaults, and an inaccessible optional settings root does not block defaults. A malformed or out-of-tree persisted focus no longer prevents project opening. Metrics: Terminal 43/43 and Integration 10/10; settings: 26 pass/two platform skips; focus PTY and adjacent controls 4/4. Real-root timing observations are recorded above. |
| Retention and cache boundaries | `bd3a8f73`, `7d1766e6`, `0df248c4` | Recent journals skip unnecessary header reads without changing retention eligibility; unreadable repository-cache indexes and remote identity writes fail safely; MCP search navigation reuses bounded unchanged-content results. These are separate workloads, not an aggregate application speedup. |
| Git executable trust | `33795715` | PATH directory aliases, project/current-directory aliases, and final file links are resolved to physical paths before accepting a Git/SSH executable. A Windows junction reproduced the original bypass; 71 focused Release checks passed, while the direct file-symlink test skipped on this host for lack of privilege. Current normal PATH resolution was 0.597 ms first and 0.374–0.405 ms in five warm samples; this is an absolute cost, not an A/B speedup. |
| GUI reload publication | `b13ca0b6` | Canceling a same-project reopen before the new tree is published no longer reports a successful open through the restored prior project state. One headless UI regression failed before and passed after; four focused Unit checks include cancellation after publication, which remains a success. |
| MCP related evidence | `8fe4fb94` | `related_files` reports the number of unique uninspected evidence sources in both inline and immediate stored-result responses while withholding unverified reference text. One new regression failed before; two focused and three adjacent Integration checks passed after. |
| Dependency cache root and probe ownership | `87839528`, `43882c7e` | Equal manifest/config fingerprints from distinct physical roots cannot share a resolved dependency graph. The cross-root TypeScript shadow-path regression and two nearby Integration checks passed. Appearance or removal of an unselected shadow path within one root now invalidates cached resolution without reparsing source facts; both regressions failed before the fix, then 9 focused Unit and 3 Integration checks passed. Exact physical probes are capped at 4,096; overflow disables resolved-index retention. A warm real-root index of 1,807 files took 30 ms (absolute timing, no pre-change baseline). |
| CLI search read budget | `516244c6` | Raw search rechecks actual file size against the remaining 64 MiB inspection budget instead of relying solely on pre-scan metadata. A grown-file regression failed before; two focused and 19 process checks passed after. The response now marks such a partial inspection explicitly. |
| MCP journal cancellation | `f13e6029` | Canceling journal start no longer consumes its bounded startup retry. One new regression failed before the fix; four focused Unit checks passed afterward. |
| MCP artifact inventory | `f8c9a25b` | Removing the final signature from a previously ignored generated-looking directory invalidates its stale inventory proof, so source files become visible. The new regression failed before; six focused Integration checks passed afterward, including a 50,000-event deep-change fast-path control. |
| TUI structural refresh | `fb9467fa`, `63931507` | A refresh built before a selection change cannot replace the newer checkbox/plan state; it retries against the current revision. The regression failed before and five focused structural checks passed after. An unrelated existing PTY test raced the asynchronous settings publication; it now waits for both the actual tree change and file-count change, with two focused PTY checks passing. No product delay changed. |
| GUI preview metrics freshness | `f9325776` | The visible status snapshot remains available while selection/recovery/full-tree refresh is pending, but incomplete or stale metrics are not reused as completed full-preview selection metrics. Three deterministic regressions failed before their respective guards; three new and 61 adjacent Release checks passed after. The 50 ms selection debounce is unchanged. |
| CLI transformed-search budget | `424be49d` | Transformed search conservatively rechecks current source sizes before its ordered redaction pipeline, including cumulative budget and empty-prefix handling. Two new regressions failed before; five focused and 19 existing process checks passed after. Five warm FileInfo sweeps over 2,739 actual DevProjex paths had a 42.50 ms median; this is an upper-scope metadata-cost proxy, not a whole-command A/B. Raw search adds no metadata queries. A write after the recheck remains a race. |
| Bounded context private paths | `cdf5841b` | With Hide Private Data enabled, bounded text/Markdown tree roots, JSON/XML project roots, and structured diagnostic paths now use the existing local-user masking policy. Four format regressions failed before; 12 targeted Integration checks passed after, including unchanged-output controls with the setting off. |
| Scope discovery F5 coherence | `151c8dcf` | A concurrent root invalidation/replacement can no longer let a retired scope validation report a reusable cache. A deterministic race regression failed before; 23 discovery checks passed with one Windows POSIX skip, plus 26 adjacent cache checks. Unrelated-root invalidation preserves the hit. A fresh Release normal-folder profile of the changing DevProjex worktree (2,739 files, 3,025 inventory entries) measured 767.38 ms first backend pass and 360.29/268.64/177.99/177.61/147.51 ms later passes; this is not an A/B against the frozen workload above. Repeated root-facts probes covered roughly 6–10 ms overlapping wall time and do not justify a lifetime-cache redesign before release. |
| MCP private root metadata | `beb1851c` | With Hide Private Data enabled, `get_tree` masks the local-user segment of its root label, and batch/read/search hints use bounded `#index` references instead of leaking absolute local paths. One regression failed before; two focused and six adjacent Integration checks passed after, including non-private parity. |
| Complete context diagnostic paths | `d3459d03` | Complete JSON/XML exports apply the existing private-path presentation to diagnostic paths independently of source mapping. Two regressions failed before; 16 targeted Integration checks passed after, including bounded-output controls. |
| Desktop IPC outcomes | `fe81737c` | Malformed non-string `preview.open.view` returns `DPX-DESKTOP-INVALID-PAYLOAD`; a failed registration refresh cannot turn an already applied desktop action into a false rejection. Both regressions failed before; the targeted desktop IPC class passed 49 tests with three Unix-only skips on Windows. |
| MCP search oversized context | `391f48d2` | An oversized adjacent context line no longer hides a short match in an otherwise complete search result. The regression failed before; four focused Integration checks passed afterward, including the separate oversized-matching-line partial-result boundary. |
| Shared dependency extraction cancellation | `ced2bf4f` | A canceled producer no longer cancels an independent caller that joined its file-facts cache entry; the live caller retries with its own token. The deterministic regression failed before and passed after; 20 focused failure/cancellation Unit checks passed. Successful-reuse metrics exclude the canceled shared attempt. |
| Desktop instance list timeout | `cd17284b` | `ui list --timeout` now returns `DPX-DESKTOP-TIMEOUT` with exit code 5 and cancels the registry scan token; caller cancellation still returns code 130. One regression failed before; four focused Terminal checks passed afterward. |
| Content preparation source admission | `04d47418` | Shared redaction/compression preparation rejects out-of-root selected paths before opening source content and retains its post-read policy check. Two mode regressions failed before with one source-analyzer call each, then two focused Unit and 12 adjacent Integration checks passed. Two optional file-symlink cases skipped because this Windows host cannot create them; pre/post path checks cannot eliminate a replacement race between validation and open. |
| Context metric source admission | `48356f7f` | Structured JSON/XML export and token-budget accounting now validate source-backed metric paths before a fallback analyzer read. Three regressions failed before; 9 focused Unit and 9 adjacent Integration checks passed. Immutable prepared snapshots remain eligible without a second source read. |
| TUI pull consistency | `56ae05c5` | After a successful repository update, TUI keeps export gated until a noncancelable structural refresh completes; a failed refresh leaves the repository state marked inconsistent. Update/branch/preview PTY controls and transition checks passed 12/12 in Release. The adjacent `Ctrl+U` test was corrected to the documented null-frontier behavior without changing selection logic. |
| Folder export staging links | `a67ccd3f` | A progress-callback junction swap in a staging subdirectory previously caused folder export to write outside staging. A regression failed before per-file ancestor and final-stage validation; the two-case theory passed afterward. The focused Release Integration batch passed 10 checks with two Unix-only skips on Windows. A 2/2 follow-up confirmed that cleanup leaves an external sentinel unchanged. Path-based checks cannot eliminate a hostile same-user swap between validation and OS open. |
| Managed Git reset cancellation | `25b6e507` | Cancellation remains effective through fetch and immediately before hard reset. Once reset of the managed cache begins, caller cancellation cannot terminate the Git process halfway through materialization; the existing two-minute operation deadline remains. A deterministic regression failed before; 4 focused Unit and 2 local-repository Integration checks passed afterward. A Git process failure or deadline during reset can still leave a partial cached worktree. |
| Selected content source admission | `286303cf` | GUI clipboard, streamed file output, combined export, and bounded preview now pass the physical project root separately from any display-path alias; transformation contexts also supply their own root when a caller omits it. A stable junction previously let selected content read an external file, including with Hide Secrets. The plain/protected regressions failed before, then 4 focused and 71 related Unit checks passed. Plain output omits unavailable entries; protected output fails closed. An optional no-context/no-root API still accepts caller-supplied arbitrary paths. |
| Shared dependency resolution cancellation | `fa2db563` | A canceled index producer no longer cancels an independent caller joined to its shared resolution entry. The live caller retries a bounded number of joins, then resolves privately if necessary; failed-entry removal remains instance-conditional. A deterministic regression failed before, then 25 focused Unit checks passed; the new test verifies one weighted successful entry, truthful re-resolution metrics, and a subsequent warm hit. |
| MCP project-address metadata | `124b3be5` | The published `project` schema and six project-tool descriptions now specify a unique listed name, an absolute path returned by `list_projects`, or a listed `#index`; allowed Git URLs remain conditional on remote opt-in. Protocol behavior is unchanged. A metadata regression failed before; 4 focused Integration checks passed afterward across local and remote-enabled modes. |
| TUI failed-mutation reconciliation | `850fc5f4` | A failed managed Git update or branch switch may have changed part of the cached worktree. TUI now gates export and performs the existing noncancelable structural refresh even when the mutation reports `false`, then retains the original failure outcome; refresh failure leaves export blocked. Two deterministic regressions failed before, and 13 focused transition/update/branch Terminal checks passed afterward. Network or preflight failures now also incur a refresh on the error path. |
| Profile snapshot atomic replacement | `651d110b` | The profile read-cache identity now includes creation time already available from the same `FileInfo.Refresh`, alongside length, last-write time, and the 4 KiB prefix; no extra content read was added. A same-length/restored-mtime atomic replacement changing a later profile returned stale selection before the fix. The regression failed before and 240 focused Unit checks passed afterward. Filesystems that do not distinguish replacement creation times skip the new test; a replacement restoring every identity field still needs a full hash to detect. |
| Managed branch-switch cancellation | `27334642` | Cancellation remains effective through branch verification/fetch and immediately before detach. Once managed checkout starts, detach and worktree-branch recording use caller-independent tokens under their existing per-command two-minute deadlines, allowing TUI to reconcile true or false outcomes. Two regressions failed before; 6 focused Unit, 3 local-repository Integration, and 1 branch PTY check passed afterward. The earlier tracked-branch config write does not materialize the worktree; interruption there remains a Git-config error-path risk, not a demonstrated partial-tree defect. |
| Managed cache staging links | `48c7d70a` | Clone staging creation rejects a linked staging root, while publication requires an immediate child of that root and rejects linked entries before and after the move. Three deterministic local-link regressions failed before the safeguards; 6 focused Unit checks and a local clone-to-session Integration control passed afterward. A hostile concurrent filesystem replacement remains outside this path-based API's guarantee. |
| MCP batch file continuation options | `6cc00d09` | Generated `get_file` continuation arguments now retain the request's explicit profile and exclusions, including an explicitly empty exclusion list. Without these, a later page could resolve a different selection and fail to read the same file. Two regressions failed before the fix; 5 focused Integration checks passed afterward. |
| TUI secret-mark publication | `d83f072d` | Failed or canceled context planning no longer replaces marks on the still-active workspace. TUI opening defers mark application until its prepared workspace is published, closing the superseded-open gap while successful direct factory callers retain their marks. Two regressions failed before the fix; 9 focused Terminal checks passed, 1 optional benchmark was skipped, and the existing profile-mark planning Unit invariant passed. |
| Managed cache trash containment | `7851c6e0` | Startup cleanup and cache-removal paths now reject linked `.staging` or `.trash` components before enumerating, moving, or deleting trash entries. Four deterministic regressions showed external deletion or incorrect movement before the guard; all four and four adjacent normal-cleanup controls passed in Release afterward. Concurrent path replacement remains outside the path-based API's guarantee. |
| Current-schema profile backup recovery | `4a9ecdd0` | A primary profile document declaring the current schema but missing its required `profiles` object is now invalid, allowing a valid backup to supply existing profiles. Previously lookup returned missing and the next save could overwrite both copies without the old entries. Two regressions failed before the fix; 28 defensive-validation and 7 version-compatibility Unit checks passed afterward. Legacy schema migration is unchanged. |
| Recent-project backup recovery | `53c16df1` | Current-schema recent-project documents must contain their four writer-required arrays; a semantically incomplete primary no longer masks a valid backup or loses history on the next write. One backup-preservation regression failed before the fix; the 2 new regressions and a focused 78-pass Recent/persistence Unit set passed afterward, with 2 Unix-only cases skipped on Windows. A validation callback reuses the single physical read; legacy schemas retain their migration path. |
| Current-schema user-settings backup recovery | `aeeb60b0` | A current-schema settings document missing required `viewSettings` no longer masks a valid backup, so a subsequent view change preserves existing language, compact mode, and update metadata. The regression failed before the fix; 29 focused UserSettings/backup/terminal-command Unit checks passed afterward. `updateCheckSettings` remains optional to preserve an existing compatibility contract. |
| Desktop-control committed-open reporting | `7596bfa0` | A failed registry refresh after a successful GUI project open or an already-loaded desktop request no longer reports that the applied action failed. Two headless UI regressions failed before the fix; 5 focused open/startup/language UI checks passed afterward, including the unchanged error for a genuinely missing project. Expected registry I/O failures remain logged and do not suppress load errors. |
| Current-theme backup recovery | `5b39c80f` | A current-schema/revision theme document without an object-valued `presets` property is invalid, so it cannot replace a valid backup containing a customized preset with factory defaults. The missing and null cases failed before the fix; a 51-pass focused theme/persistence Unit set passed afterward. Partial preset dictionaries retain the existing normalization contract. |
| Optional journal GUI startup | `a4cb9aca` | GUI composition now remains available when the optional journal directory cannot be opened. The fallback reader rejects every read, watch, and clear operation without accessing that path; the journal store itself and MCP/CLI fail-closed behavior remain unchanged. Two focused headless Unit checks passed for unavailable and normal storage; an existing symlink-specific case was skipped on this Windows environment. A pre-fix run of the new composition test was not recorded. |
| Current-schema secret-mark backup recovery | `104e5f8d` | A current-schema marks document missing `projects`, or a project object missing `states`, no longer masks a valid backup and loses existing marks on the next write. Both regressions failed before the fix; 55 focused persistent-marks/profile-defense Unit checks passed afterward. Older schema migration and the existing isolation of other malformed project entries remain unchanged. |
| Bounded file-prefix short reads | `72538739` | The 512-byte classifier probe now accumulates permitted short reads until the bound or EOF, and checks cancellation between reads. Previously UTF-16/32 BOMs could be missed, later NUL bytes could be classified as text or oversized estimates, and cancellation could return an estimate. Eight regressions failed before the fix; all eight plus 17 adjacent encoding/classification Unit checks passed afterward. A normal complete prefix still uses one stream read. |
| Non-array secret-mark state recovery | `f571bc62` | A current-schema project with `states` set to null or a non-array is now invalid at the document boundary, so updating a valid sibling cannot erase that project's valid backup marks. Both cases failed before the fix; 57 focused persistent-marks/profile-defense Unit checks passed afterward, including legacy malformed-neighbor isolation. |
| Theme-reset write failure | `26a06ff2` | Resetting GUI settings now requires the theme-store write to succeed before changing live theme or user preferences; a failed user-settings write produces a failure toast and retains the existing retry-on-close path. The failure message is localized in all 20 locales. Focused Release checks passed: 57 Unit and 7 headless UI cases; the UI stayed off screen. |
| Atomic JSON backup mirroring | `c41f508b` | After primary commit, interrupted backup copying previously could truncate the last valid `.bak`. A deterministic partial-copy regression failed before the fix. The primary is now copied to a same-directory temporary backup and published only after success; the old backup and write-result semantics remain intact on failure. Focused Release runs across JSON persistence and dependent stores passed 257 tests, with 3 Unix-only skips on Windows. |
| MCP included-byte refresh | `df513c9d` | When a cached file grew but remained under `max_file_bytes`, the effective size was refreshed while `IncludedBytes` still reported the old total. An under-cap growth regression failed at 6 versus 32 bytes before the fix; the existing no-exclusion path now recomputes the selected sum with saturating arithmetic. Three focused Integration checks passed afterward, including over-cap filtering and pack token-budget ordering. |
| Project-scope access revocation | `d7a053c6` | A `SecurityException` raised while enumerating root entries after an initial access check no longer aborts the project load or exposes partially collected root facts; the root is treated as inaccessible, matching neighboring scanner behavior. A deterministic partial-enumeration regression failed before the fix; 58 focused Unit checks passed afterward, with 3 platform skips. |
| Valid backup through recovery commit | `98939534` | When recovering a corrupt primary from a valid backup, the primary replacement previously wrote the corrupt old primary over `.bak` before the staged mirror. If that mirror then failed, no valid backup remained. A deterministic recovery regression failed before the fix; existing `.bak` is now retained until mirror publication. Normal recovery checks passed 8/8; dependent-store Release checks passed 286 tests with 3 Unix-only skips on Windows. |
| MCP size-refresh cancellation | `98179bfd` | A canceled `max_file_bytes` request previously kept reading file sizes for all 100 fixture files before reporting cancellation. The refresh now checks the token before each stat; the red regression observed 100 reads, and the green run observed one. Four focused Integration checks passed afterward, including the cached-growth byte count. |
| GUI content-total cache race | `7b911ee9` | A file-fact eviction or replacement during aggregate calculation could publish and cache a stale content total. A deterministic interleaving failed before the fix (94 versus 74 characters). File-fact revisions now guard cache hits and publication under a consistent lock order without changing scan generation semantics. Two new race scenarios and 67 adjacent Release Unit checks passed. A current-only headless DevProjex run retained 5,919 version probes and showed no obvious hot-path regression; noisy timings do not establish a precise A/B improvement. |
| Recent-history startup lock retry | `84e7d52d` | A temporarily locked recent-project store during synchronous `--last` construction was previously memoized as an empty, loaded history, causing a false no-recent-project result. The deferred shared load now retries off the UI thread with a bounded lock wait, and another temporary failure remains retryable on a later IPC request. The constructor contention regression failed before the fix; 4 focused headless UI cases and a warning-free Release UI build passed afterward. |
| TUI project-export planning | `7c7afb62` | Actual folder/ZIP export and portable-profile save now use the existing structural selection reprojection instead of eagerly computing unused content metrics. The export summary and structured context paths still compute their required metrics. Two regressions failed before the fix; 7 focused Terminal checks passed afterward, including ZIP content and saved selection. On the real 2,745-file DevProjex tree, five same-binary paired planning samples had medians of 47.58 → 8.67 ms (−81.8%) and 2,129,600 → 1,359,976 B allocated (−36.1%). The planning pass made 2,745 → 0 metrics calls and 2,316 → 0 full-file reads (29,014,832 → 0 B); files, included bytes, fingerprint, diagnostics, and source stamps matched. This does not measure the copy/ZIP phase or whole-export speedup. |
| Multi-file preview metadata denial | `55b291ef` | A `SecurityException` from the small-file metadata probe no longer aborts the entire multi-file preview; the normal per-file preparation path continues and preserves other entries. The new SecurityException scenario failed before the fix while its IOException control passed. Both passed afterward, plus 68 adjacent Release Unit checks with 2 host-symlink skips. |
| Bounded GUI preference read | `3e36ecf8` | Startup reading of the optional agent-activity preference is capped at 4 KiB, including growth after opening the file; an oversized or unreadable preference keeps the feature disabled. A 4,097-byte regression failed before the fix, while 4,096 bytes remained valid; four focused UI tests and a warning-free Release UI build passed afterward. |
| Shared dependency preparation cancellation | `f104d5b9` | A canceled owner of the prepared-source cache no longer cancels an independent caller joined to the same read. The live caller retries once, then reads privately on another canceled shared generation. The deterministic regression failed before the fix; it and three adjacent prepared-source cache controls passed in Release. |
| Prompt prepared-source waiter cancellation | `98008aa8` | A caller waiting on another request's prepared-source read now observes its own cancellation promptly without evicting the owner's cache entry. The deterministic regression timed out before the fix; it and four adjacent prepared-source cache controls passed in Release afterward. |
| CLI measured-admission freshness | `4dde399a` | A content export with `--max-tokens` now verifies that the measured source versions still match after transformation and before publishing output. A deterministic source change between measurement and materialization previously produced a file despite the stale budget; the new regression failed before the guard, and 7 targeted Release checks passed afterward. A write after the version check remains a filesystem race. |
| Theme startup-lock persistence | `2a38b325` | When the theme store is temporarily unavailable during startup, subsequent edits merge only explicitly changed sliders, mode, and effect into the latest saved document rather than replacing a preset from factory fallback values. The data-loss regression failed before the fix; 17 session, 41 store, and 3 headless startup UI checks passed afterward. Normal loaded sessions retain full-preset persistence. During startup contention, the visible theme may remain at fallback values until the next launch. |
| TUI human-readable context planning | `0dcadc81` | TUI Text/Markdown context preparation and actual export now use structural selection reprojection instead of computing content metrics unused by these formats; JSON/XML retain their full-metrics plan. Two regressions failed before the fix with an unwanted metric call; 11 targeted/adjacent Release checks passed afterward, including exact output-byte parity and summary characters/tokens. The earlier same-binary DevProjex planning proxy measured 47.58 → 8.67 ms for these two plan paths, but no end-to-end context-export speedup is claimed. |
| MCP pack reader-aware cleanup | `22cb6784` | Disposing the pack registry with open read leases no longer leaves its temporary session directory behind on Windows. Cleanup waits for both active creates and readers, then runs once after the final handle closes without blocking registry disposal on the reader. The Windows regression failed before the fix; 5 focused Unit and 2 Integration controls passed afterward. |
| CLI profile planning | `68a87049` | `profile save` and `profile import --apply` now build the same selection/diagnostics without unused content metrics. Both new regressions failed before the fix; 43 focused and adjacent Release Terminal checks passed afterward. A representative same-binary DevProjex planning probe at 2,746 files eliminated 2,746 metrics calls and 2,317 full reads (29,050,444 bytes); paired medians were 156.29 → 130.41 ms for its broader full-versus-deferred TUI-open paths. That probe is not a direct CLI command speedup measurement. |
| Search read-hint manual marks | `c3675602` | A local-profile symbol-search hint now retains `--profile local` when effective manual secret marks exist, so its suggested follow-up does not silently drop that protection. For a remote source whose public URL cannot reproduce the protected checkout/profile, no runnable read command is emitted. Both regressions failed before the fix; 26 focused Search/redaction controls passed, followed by four final wording and standard-path controls. |
| Repository-cache index recovery | `0cc94772` | An incomplete primary cache index no longer masks a valid backup and causes startup garbage collection to remove an indexed repository. Schema v1/current and non-null entries are required before normalization, without a second JSON parse; future-schema documents remain untouched. The expanded regression failed in five of eight incomplete-index variants before the final guard, including missing schema and legacy null entries; all 8 variants and 8 adjacent Release controls passed afterward. |
| MCP authenticated remote cache identity | `93bf0dd5` | MCP remote resolution now partitions cache operations and sessions by the existing password-free source identity, retaining HTTPS usernames for distinct accounts while keeping public addresses and errors anonymous. Previously an authenticated URL could miss its own cloned checkout, and account-qualified sources shared a reservation key. The deterministic regression failed before the fix; 6 focused Unit and 3 Integration checks passed afterward, including branch-error display sanitation. |
| Pending manual update check | `7b398908` | An explicit Check click while the update coordinator's operation gate is held now waits for its turn instead of silently disappearing. At most one manual check may wait or run; cancellation and service errors release that slot. The deterministic automatic-check interleaving failed before the fix (one service call instead of two); three new cases passed, and the combined focused Release filter passed 33/33 afterward. |
| MCP related-evidence protection | `c405df08` | `related_files` now indexes requested source lines in one forward pass and checks sorted protected ranges with binary search without changing evidence order, redaction, or strict overlap boundaries. Dense regressions failed before the fix at 2,098,176 line-boundary searches and 4,194,304 range reads for 2,048 queries; after the fix 4 focused Unit and 5 adjacent MCP Integration checks passed. The deterministic work bounds demonstrate the asymptotic improvement, not a measured end-to-end latency claim. |
| ZIP stdout closed-pipe handling | `02ff6654` | A downstream reader closing the raw ZIP stdout pipe now follows the terminal's existing quiet-success convention instead of reporting `DPX-IO-FAILURE`. The catch is limited to the native broken-pipe signal inside ZIP stdout; an unrelated I/O code still fails. The new CLI regression failed before the fix while its I/O control passed; six focused and adjacent Release Terminal cases passed afterward. |
| Desktop-control shutdown during project load | `b5614b09` | Final window closing now cancels an in-flight project scan before awaiting desktop-control server teardown. Previously the server awaited an IPC open that could not observe its shutdown token through the project-load pipeline, while pipeline cancellation waited until the Closed event. A deterministic headless IPC/blocked-scan regression timed out before the fix; the new case and three adjacent Release UI lifecycle controls passed afterward. The change does not cancel a closing attempt still awaiting selection or export decisions. |
| MCP batched file-range reuse | `f6a5e737` | `get_file` batch reads now scan each transformed file once for total lines and up to sixteen requested start offsets, rather than recounting and rescanning the file for every disjoint range; the journal also reuses per-file redaction line counts while retaining each delivered range's record. A deterministic sixteen-range regression observed sixteen full scans before the change and one afterward. Eleven focused Unit and ten adjacent Integration checks passed, including mixed line endings, Unicode scalar continuation, exact batch status, and two separate masked-secret journal counts. No whole-request latency claim is inferred from the scan count. |
| Explicit profile-save clock order | `e24ebcae` | A CLI profile save or import-with-apply that observes a stored revision newer than its candidate timestamp now reports a conflict instead of success without writing the requested selection. The older unconditional delayed-retry no-op remains unchanged. A clock-skew CLI regression failed before the fix (success without persistence); afterward 38 profile-command Release checks and 2 focused store controls passed. |
| Desktop-control shutdown during tree refresh | `ed7cdf04` | Final window closing cancels an active tree refresh before awaiting desktop-control server teardown. A headless IPC filter/F5 race with a blocked inventory build previously left close waiting; the regression and three adjacent Release UI lifecycle checks passed afterward. The change leaves the existing refresh and window-close policies intact. |
| Budgeted MCP pack partial accounting | `477d9869` | A selected file above the mandatory 16 MiB scan ceiling previously made `pack_context` with `max_tokens` fail as I/O instead of returning the safe partial pack. Measured-budget admission now treats only explicitly unscannable files as zero-content omissions; admission- and preparation-stage omissions are merged for the trusted warning and excluded from delivered journal paths. The oversized integration regression failed before the fix; one focused Unit and three Integration checks passed afterward, including no leaked content and the safe-only journal. Other estimated metrics still require an exact read. |
| Canceled Git worktree creation | `9f5252cc` | Cancellation during `git worktree add` could leave a partial directory behind. The add-exception path now invokes the existing bounded, caller-independent detached-worktree cleanup and rethrows the original failure. The deterministic cancellation regression failed before; it and the adjacent stalled-cleanup control passed 2/2 in Release. The ordinary unsuccessful-add path is unchanged. |

The CLI dense-search dictionary experiment was intentionally discarded: three 5,000-hit
process samples per variant gave medians of 475 ms for the prior lookup and 480 ms for
the index. No production or test change from that experiment was kept, and no CLI
speedup is claimed.

## Limitations and pending release checks

- This is not evidence of real desktop GUI readiness. The backend/headless checks do not
  validate first paint, actual perceived load completion, popup rendering, DPI, platform
  composition, or the complete desktop lifecycle. Measurements use Release test hosts,
  not packaged desktop or ReadyToRun release artifacts.
- There is no trustworthy, comparable v5.0/v5.1 baseline. Recalled historical load times
  cannot be used to claim that a previous performance level has been restored.
- Local runs were on Windows. Platform skips include filesystem cases such as filename
  line feeds and symlinks. Native-Git parity on this host does not replace Linux/macOS CI.
  Coherence tests model writable-during-read behavior with controlled test streams; they
  do not claim that the Linux desktop was executed locally.
- Only targeted local suites were completed. CI was not awaited for this report; no CI success,
  full-suite success, merge, publication, or release approval is implied.
- The UI project uses a Microsoft.Testing.Platform runner that ignored one attempted
  VSTest-style `dotnet test --filter`; that run was interrupted without a suite result.
  Its exact tree-checkbox check then passed 1/1 through the runner's `--filter-method`.
- Warm filesystem-cache timings are not cold-storage throughput. Earlier exploratory
  probes used a slightly changing development corpus; the primary 124-observation A/B
  uses one frozen workload. No hardware-normalized cross-machine comparison is available.
- The scoped inventory index intentionally trades additional operation-local allocation
  for substantially fewer scope checks. The MCP process comparison does not demonstrate
  a broad performance gain.
- Metadata-only content freshness retains its documented limitation: a same-length
  rewrite with a restored timestamp is indistinguishable without a content hash.
- The combined backend measurements are complete for the recorded workload. A real
  desktop run remains a separate release check, outside this backend task, before drawing
  conclusions about the complete visible project-opening experience.
- A targeted Avalonia headless UI check verified that every realized project-tree checkbox
  has no tooltip or automation help text while Hide Secrets and MCP menu help remain intact.
  Its 40,440-byte tree screenshot was captured under the ignored local
  `artifacts/release-stabilization/tree-checkbox-no-tooltip.png` path. It is visual evidence
  from a headless fixture, not a claim about every desktop compositor.
- MCP discovery dependency-facts warm-up remains a separate release risk. The current
  `list_projects` path has no selection plan; a background build would mutate shared
  state, while holding its operation gate through a 4.7-second cold index could delay
  the next tool. A cancellable, foreground-priority scheduler and disposal drain need
  independent design and verification; no speculative warm-up was added here.
- MCP search's 64 MiB aggregate inspection admission uses planned file sizes, while the
  ordered transform workers enforce a separate 16 MiB per-file scan limit and 96 MiB
  in-flight budget. Files that grow after planning can therefore cause more than 64 MiB
  of total source reads in one request without marking the result partial. A strict
  aggregate limit needs ordered producer-side live admission and skipped-tail accounting;
  a one-time size recheck cannot close the same-request race. No unverified shared-pipeline
  change was added before release.
- Selected-content source admission still checks filesystem paths around the read.
  Concurrent changes to ancestor directories on Unix are not covered by the final-file
  no-follow open; a strict physical-root guarantee requires descriptor-relative traversal.
  This was not broadened into a late cross-platform I/O rewrite without comparable tests.
- A local profile with an explicitly empty selected-path list has different established
  meanings across surfaces: CLI/TUI/MCP select no files, while GUI intentionally treats
  zero checked tree boxes as the entire tree. Opening such a cross-surface profile in GUI
  can therefore preview or export all files. Aligning this behavior requires a product
  decision; this stabilization branch preserves the existing GUI contract.
- GUI view-setting persistence can synchronously wait for the cross-process settings
  lock for up to five seconds when another process holds it. Moving persistence off the
  UI path requires a lifetime/ordering design and an end-to-end contention regression;
  no lock timeout or saved-setting semantics were changed late in this branch.
- Packaged v5.1-to-shared user-data migration currently allows startup to continue after
  a busy migration lock or transient migration failure. Startup can then write factory
  defaults into the destination; a later migration attempt treats that destination as
  initialized and can leave legacy settings and profiles unmigrated. The safe narrow
  policy is a bounded retry followed by a visible fail-closed startup error before any
  stores are created, but this changes launch behavior during contention. The product
  decision is requested in PR #451; migration code has not been changed in this branch.
- Managed Git quota checks do not test cancellation inside their filesystem enumeration.
  A very large or slow cache tree can therefore delay completion of a canceled operation
  beyond the process-reap deadline. No deterministic timing reproduction or safe bounded
  scanner change was established for this release patch.
- ZIP export currently treats an automatically named destination as replaceable, even
  when its conflict policy is `Fail`; a second export to the same name can replace the
  first archive. Folder export chooses a free suffix, while the GUI file picker has an
  overwrite prompt. Changing the service default without an explicit GUI-confirmed
  overwrite signal could break that flow, so this behavior needs a product decision.
- TUI project-export confirmation offers Overwrite for an existing folder, but the
  shared validator rejects folder replacement and CLI `--force` supports ZIP only.
  The button always fails for folder conflicts. Hiding the unsupported action versus
  enabling destructive folder replacement is a product choice recorded in PR #451;
  folder-export behavior was not changed in this branch.
- Two GUI windows that both loaded a missing local project profile can lose independent
  first edits: the second save has no baseline, treats every selection field as changed,
  and can overwrite the first window's setting. Normal TUI editing keeps a pre-edit
  baseline, while explicit imported-profile publication intentionally does not. A safe
  GUI baseline must be captured before user edits, but filter controls are currently
  editable during asynchronous initial load; disabling them briefly or tracking edit
  intent changes the loading UX. This release branch leaves the behavior unchanged
  pending the product decision recorded in PR #451.

## Cleanup status

Cleanup is **pending**, not completed. The attempted cleanup of four preview benchmark
`bin`/`obj` directories was rejected by policy before execution. Those directories were
inventoried at 1,283,874,520 B; reported reclaimed space is **0 B**. Baseline source archives,
benchmark outputs, and other generated build artifacts also remain to be addressed under
the applicable cleanup policy. The rejection must not be bypassed through another shell.

A final read-only inventory at 09:36 UTC found 56 project-derived `bin`/`obj` directories totaling
14,461,519,165 B of logical file sizes. Resolved paths were restricted to the workspace
and reparse points were rejected. This is a pending-artifact inventory, not space reclaimed
or a claim that all these bytes were created by this task; no initial inventory exists.
Source archives and other non-`bin`/`obj` temporary outputs are additional pending items.

## Evidence and reproducibility

The working verification ledger is `artifacts/release-stabilization/verification-log.md`.
The durable combined A/B evidence is [v5.2-gui-backend.json](Benchmarks/v5.2-gui-backend.json),
containing 124 timing/allocation samples, measurement boundaries, and the invariant audit.
Committed regression tests and benchmark harnesses are the reproducible evidence; this
document excludes personal environment paths and operational account information.

Relevant harness documentation:

- [Preview selection metrics](../tools/Benchmarks/Preview/README.md)
- [Scale and sparse projection](../tools/Benchmarks/Scale/README.md)
- [MCP process and search merge](../tools/Benchmarks/Mcp/README.md)

The opt-in `GuiSelectionLoadMeasurementTests` and `GuiBackendLoadMeasurementTests` use
`DEVPROJEX_GUI_BENCHMARK_ROOT` to select a read-only target. Record the exact revision,
effective file count, phase boundaries, warm-up policy, counters, and distribution when
repeating measurements. Keep backend probes distinct from a real desktop run.

`ProjectProfileMarksReuseTests` enables its two isolated profile-load benchmarks with
`DEVPROJEX_PROFILE_MARKS_BENCHMARK=1`. `ProjectScopeDiscoveryRootFactsMeasurementTests`
uses `DEVPROJEX_GUI_BENCHMARK_ROOT` for read-only profiling; its diagnostics distinguish
summed work, overlapping wall-clock coverage, and scanner hook events.

`GuiTransformedLoadMeasurementTests` also uses `DEVPROJEX_GUI_BENCHMARK_ROOT`. Run its
full-selection and partial-selection methods separately. The latter alternates retained
and explicitly discarded compact facts on a single binary; preserve this distinction
when reporting results. The stored transformed-load JSON retains every observation,
not just the three-observation medians.

`AgentJournalStartupMeasurementTests` is enabled only with
`DEVPROJEX_JOURNAL_STARTUP_BENCHMARK=1`. It creates disposable synthetic journals with
default retention and does not inspect the user's application-data directory.
