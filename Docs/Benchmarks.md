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

For DevProjex, elapsed time is `analyze --format json` plus `export context` in
two real processes; RSS is the larger main-process peak. For Repomix, elapsed
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
