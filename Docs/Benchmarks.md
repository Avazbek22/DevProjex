# Benchmarks

These measurements are reproducible observations, not general performance
claims. The harness and pinned inputs are in
[`tools/ScanBenchmark`](../tools/ScanBenchmark/README.md).

## Cold scan against Repomix

Measured on 2026-09-07 on Windows 10.0.26200 with an Intel Core i9-13900HX
(24 cores, 32 logical processors), 64 GB RAM, .NET SDK 10.0.400, Node 24.13.0,
Git 2.45.1.windows.1, the Release headless DevProjex 5.1 executable, and
`repomix@1.17.0`. Repositories were fetched once at these immutable commits:

| Corpus | Commit | Tracked files |
|---|---|---:|
| `pallets/flask` | `d318b683471101618febed18996405ad26462110` | 236 |
| `yamadashy/repomix` | `85e3969b010c72b905203812d1a3f5beb84a2102` | 1,189 |
| `godotengine/godot` | `34d06658a85845111a50db9e485ec4a0701d4298` | 14,261 |

Each table cell is the median of three repetitions. A cold observation uses a
new process, a fresh detached worktree path, and a fresh per-tool application or
configuration cache. Its paired warm observation uses another process and the
same cache. The operating-system page cache was not flushed. Repomix was acquired
through `npx --yes repomix@1.17.0` before timing; registry resolution and download
are excluded.

For DevProjex, the historical `Elapsed` column is the sum of two distinct operations:
`analyze --format json` followed by `export context` in two real processes; it is
not the latency of either command. RSS is the larger main-process peak. For Repomix, elapsed
time and RSS cover its one real Node pack process. RSS does not aggregate child
processes. Output bytes are exact file sizes. DevProjex token counts come from
its content metrics; Repomix token counts come from its summary. Those estimators
are not the same tokenizer, so compare each count with its own output, not as a
cross-tool token-accuracy result.

### Tool defaults

Both tools use their shipped default exclusion and security behavior.

| Corpus | Tool | Cold ms | Warm ms | Peak RSS MiB cold / warm | Files | Output bytes | Estimated tokens |
|---|---|---:|---:|---:|---:|---:|---:|
| Flask | DevProjex | 1,552 | 1,506 | 70.3 / 70.5 | 212 | 1,540,097 | 390,100 |
| Flask | Repomix | 536 | 516 | 291.2 / 178.4 | 230 | 1,199,718 | 287,044 |
| Repomix | DevProjex | 2,408 | 2,301 | 93.7 / 94.2 | 951 | 6,387,531 | 1,478,431 |
| Repomix | Repomix | 1,057 | 789 | 923.9 / 244.9 | 1,147 | 5,893,415 | 1,412,796 |
| Godot | DevProjex | 13,549 | 13,371 | 227.0 / 224.4 | 13,811 | 326,980,875 | 69,922,360 |
| Godot | Repomix | 9,225 | 7,601 | 4,345.7 / 2,443.9 | 14,147 | 327,119,792 | 87,904,430 |

### Git-ignore baseline with secret checking

This series removes product-specific default patterns as far as the public
options permit. DevProjex uses `--exclude none --git-mode gitignore
--hide-secrets` (`analyze` also emits findings). Repomix uses
`--no-default-patterns --no-dot-ignore`; its default security check remains on.

| Corpus | Tool | Cold ms | Warm ms | Peak RSS MiB cold / warm | Files | Output bytes | Estimated tokens |
|---|---|---:|---:|---:|---:|---:|---:|
| Flask | DevProjex | 2,805 | 2,775 | 106.6 / 106.5 | 236 | 1,554,629 | 395,467 |
| Flask | Repomix | 548 | 512 | 291.4 / 179.4 | 232 | 1,564,107 | 456,685 |
| Repomix | DevProjex | 6,178 | 6,139 | 170.0 / 170.4 | 1,189 | 6,885,876 | 1,613,218 |
| Repomix | Repomix | 975 | 753 | 913.5 / 234.7 | 1,153 | 6,840,483 | 1,778,186 |
| Godot | DevProjex | 62,445 | 58,936 | 1,037.6 / 1,017.4 | 14,261 | 327,740,310 | 70,191,036 |
| Godot | Repomix | 8,269 | 7,364 | 3,478.0 / 2,440.6 | 14,149 | 327,185,858 | 87,930,903 |

The file sets remain different even in the second series. The tools do not share
ignore grammars, binary handling, generated-output structure, or secret-finding
policy; for example, Repomix reported and excluded a suspicious Godot test file,
while the DevProjex inventory reflects its own selection and redaction contract.
The table therefore does not support a claim that either tool is faster on an
identical corpus.

### Content-pipeline optimization baseline

The performance work based on `c249c309` first repeated the defensive series with
one unprofiled control sample per corpus. Unlike the historical aggregate above,
these columns expose the two real DevProjex operations separately. The immutable
raw samples, including CPU time and per-operation RSS, are in
[`baseline-c249c309.json`](../tools/ScanBenchmark/results/baseline-c249c309.json).
The final optimization measurements use the same binary layout, corpus commits,
arguments, machine, and harness.

| Corpus | Analyze ms cold / warm | Export context ms cold / warm | Combined ms cold / warm |
|---|---:|---:|---:|
| Flask | 1,331 / 1,270 | 1,398 / 1,347 | 2,729 / 2,618 |
| Repomix | 2,627 / 2,638 | 3,152 / 3,212 | 5,779 / 5,850 |
| Godot | 27,837 / 28,379 | 33,785 / 36,737 | 61,622 / 65,117 |

The optimized branch was measured once per operation after the implementation,
using the same pinned inputs and defensive arguments. These are control runs
without a profiler; they are not presented as three-run medians. The complete
samples are in
[`optimized-perf-secret-pipeline-and-engine.json`](../tools/ScanBenchmark/results/optimized-perf-secret-pipeline-and-engine.json).

| Corpus | Analyze before / after ms | Analyze speedup | Export before / after ms | Export speedup | Analyze peak RSS before / after MiB | Export peak RSS before / after MiB |
|---|---:|---:|---:|---:|---:|---:|
| Flask | 1,331 / 1,017 | 1.31× | 1,398 / 1,104 | 1.27× | 105.6 / 101.4 | 105.0 / 100.8 |
| Repomix | 2,627 / 1,448 | 1.81× | 3,152 / 1,520 | 2.07× | 164.1 / 155.4 | 169.9 / 156.4 |
| Godot | 27,837 / 11,719 | 2.38× | 33,785 / 11,014 | 3.07× | 962.5 / 999.2 | 910.6 / 967.6 |

The Godot export improved by 3.07× in the end-to-end harness, short of the 4×
target. A separate unprofiled stage-attribution control reached 9,857 ms (3.43×),
which also remains short. Peak process RSS did not improve on Godot even though
retained source text is now bounded: the peak includes concurrent detector and
runtime allocation churn, not only live prepared content. Both limitations are
reported rather than hidden behind the faster stage totals.

The one-run Godot export comparison above increased peak RSS from 910.6 MiB to
967.6 MiB (+57.0 MiB). That increase is an explicit memory price of the measured
speedup; bounded retained source text did not translate into a lower process peak.

After the correctness review, the defensive series was repeated three times per
temperature without a profiler. The table reports medians; every repetition used
the same pinned manifest and arguments as the baseline. Compared with the earlier
single optimized controls, the cold medians were 1,003/1,092 ms instead of
1,017/1,104 ms for Flask, 1,451/1,541 ms instead of 1,448/1,520 ms for Repomix,
and 9,204/10,391 ms instead of 11,719/11,014 ms for Godot (analyze/export).

| Corpus | Analyze ms cold / warm | Export context ms cold / warm | Combined ms cold / warm | Analyze peak RSS MiB cold / warm | Export peak RSS MiB cold / warm |
|---|---:|---:|---:|---:|---:|
| Flask | 1,003 / 996 | 1,092 / 1,107 | 2,094 / 2,103 | 101.3 / 101.1 | 101.5 / 101.1 |
| Repomix | 1,451 / 1,385 | 1,541 / 1,512 | 2,992 / 2,897 | 159.9 / 158.1 | 155.1 / 157.8 |
| Godot | 9,204 / 8,626 | 10,391 / 10,404 | 19,595 / 19,030 | 996.4 / 816.9 | 949.5 / 969.5 |

Against the original cold baseline, the reviewed Godot medians are 3.02× faster
for analyze and 3.25× faster for export context. They still do not meet the 4×
target. The three Godot export samples were 10,391, 10,411, and 10,377 ms cold,
and 10,404, 12,306, and 10,224 ms warm; the slower second warm sample is retained
rather than discarded.

The reviewed harness reports 77,689,944 estimated tree-plus-content tokens for
Godot instead of the earlier 70,191,036. This is an intentional correctness
change: detector policy exclusions such as lock files now contribute their
unchanged source metrics to analyze even though the detector is not invoked for
them. The selection remains 14,261 files and the harness export remains
327,740,310 bytes.

The correctness run also pinned repeatability separately from timing. The
canonical 14,261-path Git manifest had SHA-256
`c77c62241832eef5b9eb96b16c3e1ace1a0831961633eacc7b19a9914fdb404b`.
All three analyzes produced selection fingerprint
`4d84367ec5931826c83030a9474242d8c41ac3ebcf50cab4909041052c946d11`,
43 findings, and the same safe-findings SHA-256
`01f35b4cfbc9f70bc16b4852c59a2d89c6b5c2cf378c313dd6e6e9cb4909ae93`.
Their complete JSON documents shared SHA-256
`03691ef0fa31004bf01b8b8201c7565f12dbcbf0b814951c14f0b0b2dee412bb`.
Three exports from one fixed checkout path were each 327,543,528 bytes with
SHA-256 `bed955722ab8cfb1f528061359dcb1970b0cba58c8cb654cbb8316b29b0dd569`.
The document embeds its checkout root, so that last hash is a repeatability check
for the fixed path, not a path-independent corpus digest. The Godot finding count
therefore remains 43 after the line-range cache correction.

The Godot stage trace is published as
[`content-pipeline-godot-stages.json`](../tools/ScanBenchmark/results/content-pipeline-godot-stages.json).
Per-file stages overlap across workers, so their values are aggregate active time
and must not be summed as wall time.

| Stage | Baseline aggregate ms | Optimized aggregate ms |
|---|---:|---:|
| Selection | 2,445 | 2,151 |
| Source read | 2,458 | 2,479 |
| Compression | 19 | 18 |
| Detector initialization | 64 | 49 |
| Detection | 10,909 | 7,582 |
| Redaction and output | 7,501 | 926 |
| Cleanup | 1,438 | 22 |

The detector characterization over 306,482,455 characters measured 835 ms in
the keyword prefilter, 4,240 ms in rule detection, and 4,689 ms combined, with
the same 43 findings. Four file workers were retained: measurements with 1, 2,
4, and 8 workers showed no stable gain above four, while eight doubled the
maximum retained work window. The long-line regression uses twelve findings on
a two-MiB single line; the range-backed implementation stays below an 8 MiB
allocation ceiling instead of copying that line for every finding. Analyze's
transformed-content path creates zero prepared files; export still uses an
immutable snapshot as required for consistency.

The same baseline also ran two identical narrow `get_tree` → `search_project` →
`get_file` sequences in one initialized MCP process. Warm detector state helped,
but the unchanged project inventory was still rebuilt for every call.

| Corpus | First sequence ms | Second sequence ms | Same-process speedup |
|---|---:|---:|---:|
| Flask | 1,258 | 646 | 1.95× |
| Repomix | 4,057 | 3,271 | 1.24× |
| Godot | 37,160 | 34,004 | 1.09× |

After inventory and projection reuse, the same real-process sequence produced.
The first and second columns are consecutive executions of the identical
`get_tree` → `search_project` → `get_file` sequence against one unchanged checkout,
using one initialized MCP process and one DevProjex binary/version:

| Corpus | First identical sequence ms | Second identical sequence ms | Same-process speedup |
|---|---:|---:|---:|
| Flask | 705 | 35 | 20.36× |
| Repomix | 882 | 126 | 6.98× |
| Godot | 1,988 | 51 | 38.66× |

For Godot, the reported 38.66× denominator is specifically 1,988 ms divided by
51 ms for those first and second consecutive sequences; it does not compare two
versions or two different queries.

The cache retains immutable inventory and path projections only while a root
watcher and Git/control-file stamps prove the snapshot current. Source content is
not served from that cache: every content read still uses the validated root-jail
handle. Directory ancestor indexing now stops at an already visited ancestor.
The scanner's root-subtree scheduler was not replaced: selection measured about
2.15 seconds of the 9.86-second best Godot control, while transformation remained
the dominant stage, so the extra concurrent traversal state was not justified by
the measured bottleneck.

Stage attribution uses `dotnet-trace` with `dotnet-common`, sampled thread time,
verbose GC/allocation events, and the content-only-free `DevProjex-ContentPipeline`
provider. Every profiled observation has a separate unprofiled control; `.nettrace`
files are temporary and excluded from published results.

Three Godot Repomix pairs in the accepted run terminated after processing with a
Windows native access-violation or heap-corruption code. Each failed pair was
discarded in full and repeated from a clean application cache; only repetitions
whose process exited successfully contribute to the medians. The committed
harness records this bounded one-retry policy and fails without a partial report
if the retry also fails.

## Pack-first against exploration

This measurement used `@modelcontextprotocol/sdk` 1.30.0 against a real
`devprojex mcp --root <flask>` process on the pinned Flask checkout above. The
fixed task was: “Find where the session cookie is signed and which configuration
keys affect it.” MCP's mandatory secret redaction stayed enabled.

Scenario A called `pack_context` once for `src/`. Scenario B called a compact
`get_tree`, searched for signing and configuration terms, read the three
discovered files (`src/flask/app.py`, `src/flask/sansio/app.py`, and
`src/flask/sessions.py`), then packed those paths. Both packs exceeded the inline
response threshold, so the harness read every stored-pack page with `read_pack`;
those transport calls and responses are included below.

| Scenario | MCP calls | Response characters | Estimated response tokens | Packed content |
|---|---:|---:|---:|---|
| Pack first | 21 (1 pack + 20 pages) | 354,548 | 79,653 | `src/`; 350,552 stored characters |
| Explore, then pack | 14 (6 discovery/read + 1 pack + 7 pages) | 230,487 | 51,075 | 3 paths; 120,336 stored characters |

Exploration used seven fewer calls overall after stored-pack paging and reduced
response volume by about 35% in characters and 36% in estimated tokens for this
task. This measures transport volume, not model answer quality or wall-clock
latency. Tokens were counted locally with the `o200k_base` mapping from
`gpt-tokenizer` 4.0.0. Treat them as an estimate calibrated to **±5%** because a
connected model may use a different tokenizer or message framing.

## Reproduction

```powershell
dotnet build Apps/TerminalHost/DevProjex.TerminalHost.csproj -c Release
pwsh tools/ScanBenchmark/run-scan-benchmark.ps1 -Repetitions 3
npm install --prefix tools/ScanBenchmark --ignore-scripts
node tools/ScanBenchmark/measure-mcp.mjs <devprojex> <flask-root> <result.json>
```

Corpus clones and run outputs are created below a unique system temporary
directory outside the repository. The scan harness removes them in `finally`;
the MCP caller is responsible for providing and removing its pinned checkout.
