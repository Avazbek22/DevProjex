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
- Only targeted local suites were run. CI was not awaited for this report; no CI success,
  full-suite success, merge, publication, or release approval is implied.
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
