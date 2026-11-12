# Agent benchmark log

One file per day on which an agent benchmark decided something about the DevProjex MCP server.
Each file records what was measured, on which commits, how the sessions were run, what the
numbers were, which changes were kept, which were rolled back, and why. The purpose is practical:
an idea that already failed should not be tried again without new evidence, and a change that
worked should not be undone by accident.

Related documents:

- [Benchmark-History.md](../Benchmark-History.md): the September 2026 series told as a narrative,
  with the agents' and the judge's words quoted verbatim.
- [Ranking.md](../Ranking.md): the frozen ranking protocol and every result measured with it.
- [Benchmarks.md](../Benchmarks.md): scan and pipeline timings, measured without a model.
- The `v5.2-*.json` files in this folder are raw GUI, TUI and journal timing samples quoted in
  [Release-5.2-Stabilization.md](../Release-5.2-Stabilization.md). They are not part of this log.

## Days

| Day | Subject |
|---|---|
| [2026-08-29](2026-08-29.md) | Token estimate calibrated against cl100k and o200k; divisor 4 kept |
| [2026-09-07](2026-09-07.md) | Scan speed against Repomix; frozen ranking protocol: importance ranking stays experimental, `focus-v1` passes its gates |
| [2026-09-08](2026-09-08.md) | First hands-on comparison with Repomix; the defect list fixed in #308 and #331 |
| [2026-09-09](2026-09-09.md) | Ranking and dependency facts checked against the code; three defects confirmed (#338) |
| [2026-09-10](2026-09-10.md) | First blind runs; `get_file` hidden by its schema and the redactor breaking code, both fixed in #342 |
| [2026-09-11](2026-09-11.md) | The task profile decides which tool is cheaper; whole-file reads explained; Python constants rolled back; the ranking timing gate replaced |
| [2026-09-12](2026-09-12.md) | Unused capabilities, silent truncation and "tokens" that were characters divided by four; #391 and #392 |
| [2026-09-13](2026-09-13.md) | One server per session; lighter tool catalog; first declaration body in search results |
| [2026-09-15](2026-09-15.md) | Declaration body limit set to 1,800 characters |
| [2026-09-29](2026-09-29.md) | Release QA runs on four task sets; #457 |
| [2026-09-30](2026-09-30.md) | Final 5.2 series: eleven tasks, eight repositories |
| [2026-11-07](2026-11-07.md) | #488 and #491 for narrowed views; check before release; v5.2.2 against master and Repomix on fourteen tasks |
| [2026-11-08](2026-11-08.md) | #502, #503 and #505 measured; #507 and #508 closed; keys and judge corrected; #509 to #513 measured |
| [2026-11-09](2026-11-09.md) | #516 and #518 kept, #519 closed; blind run on rich and FluentValidation; #517 and #520 |
| [2026-11-10](2026-11-10.md) | `get_tree` depth from the requested path (#533) and the `Exported by:` line (#534) measured and kept; new ranking baseline; first Codex CLI series on the 5.2 release, v5.2.2 and Repomix 1.19.0; keys checked, missed paths classified, `list_projects` made optional (#544) |
| [2026-11-12](2026-11-12.md) | Batch reads that return structure (#545), measured twice with Codex CLI; a lighter catalog (#546) and comment hits with structure members (#547), measured without a model and in one series of three builds; the same build measured twice; all eighteen tasks with Claude Haiku on the 5.2 release, v5.2.2 and Repomix 1.19.0 |

## How the days connect

1. **Measure without a model first** (29 August, 7 September). The token estimate and the ranking
   protocol were fixed before any agent ran, so later changes to dependency facts had a gate.
2. **Watch an agent** (8 September). The pilot produced the first defect list; its fix, batched
   reads, caused the first blocker two days later.
3. **Blind runs and their blockers** (10 September). A schema keyword hid `get_file`; the redactor
   broke code. Both were fixed the same day, and the paired run showed DevProjex needed more calls.
4. **Where the calls go** (11 September). Cost depends on the task profile; whole-file reads are
   decided right after a result, so changes moved into result text.
5. **The measuring tools were wrong** (12 September). "Tokens" were characters divided by four and
   paired sessions were not independent; from 13 September every series uses one server per
   session and the client's own token counts.
6. **Levers that remove whole turns** (13 to 15 September). A lighter catalog and the declaration
   body in search; the body size was set by sessions, not by size statistics.
7. **Release checks** (29 and 30 September). Each run fixed what the previous one found; the final
   series gave the published 5.2 numbers. The stand was then lost to a `%TEMP%` cleanup and
   rebuilt from the session log on 7 October.
8. **Honest answers in narrowed views** (7 November). #488 and #491, then a check before release
   that turned agent complaints into #494 to #500, then the fourteen-task baseline.
9. **More facts and lists do not help** (8 November). Hints were not followed, lists were copied;
   the keys and the judge were corrected, and the real complaints led to #509 to #513.
10. **Replace content, test on unseen code** (9 November). The large-file structure kept, the
    expanded tree dropped, and the gains confirmed on two repositories never used before.
11. **Fixes from the analysis, measured** (10 November). The tree depth and package manifest
    fixes removed empty subtrees and found the manifest in every session, without adding reads;
    the first series run from `tools/AgentBenchmark`.
12. **A second client** (10 November). The stand gained Codex CLI arms. On the same tasks Codex
    makes fewer than half of the calls Claude Haiku makes. Its first series placed v5.2.2 ahead
    of the 5.2 release in named files and far ahead of Repomix in calls and context. Most
    required paths that an answer missed had been shown by the server.
13. **A difference measured twice** (12 November). Batch reads that return structure removed
    repeated requests and most tool errors. The first series also showed fewer named files; ten
    new sessions per task, judged against thresholds set beforehand, did not.
14. **The same build measured twice** (12 November). A lighter catalog and member lists were
    checked on recorded calls first, then in one series of three builds. Two thresholds were
    crossed by less than the difference between two runs of one build.
15. **Three sides with Claude** (12 November). All eighteen tasks on the 5.2 release, v5.2.2 and
    Repomix 1.19.0, with the judge. Haiku asks for one file per call, so the batch changes
    measured with Codex hardly appear, and most answers ask for the places that use a name.

## How the sessions were run

- **Stand.** [tools/AgentBenchmark](../../tools/AgentBenchmark/README.md) reproduces the November
  series from a checkout: pinned clones, host builds, runner, key metrics, judge and report.
- **Client.** Headless Claude Code (`claude -p`) with `--strict-mcp-config` and no built-in file
  tools, so the agent can only use the MCP server under test. The Codex series of 10 and 12
  November ran on Codex CLI under the stand's isolation profile, with the same restriction.
- **Model.** Claude Haiku 4.5 (`claude-haiku-4-5-20251001`) for every session and every judge
  call, with two exceptions: the four-server run of 10 September used a larger Claude model, and
  the Codex series of 10 and 12 November used `gpt-6-luna` at low reasoning effort, without a
  judge. Their costs are calculated from published API prices for the recorded tokens.
- **Anonymity.** Each server is registered under a neutral name (`server_b`, `server_m` and so on).
  The agent does not know which product it is using. The judge sees the answers as `Answer A`
  and `Answer B`, with product and tool names removed.
- **Sources.** September sessions were rebuilt on 7 October from the session log after the stand
  was emptied; the printed tables survived, the per-session transcripts did not. November stands
  are kept with their raw session summaries.
- **Builds.** Each file lists the exact commit of every DevProjex build. A build marked
  *local merge* was compiled from a temporary merge of the named PR heads onto the named base,
  because the PRs were not merged yet; that merge commit exists only on the benchmark machine.

## How to read the tables

- **One column per build.** The column header names the build by version and commit, for example
  `v5.2.2 029270bd`. Every row compares the same tasks and the same number of sessions.
- **Context tokens** (November files): every input token the model processed during a session,
  cached input included. Each tool call resends the whole conversation so far, so this number
  follows the number of calls more than the size of individual responses.
- **New input tokens** (September files from 13 September): input tokens without cache reads, as
  the client reported them. Before 13 September, tokens were estimated as characters returned
  divided by four (see [2026-09-12](2026-09-12.md)).
- **Key files.** Each task has a key written against the repository: required files, required
  tests, files that are acceptable to name, and files known to be unrelated. "59/87" means 59 of
  87 required file mentions over all sessions of that build.
- **Invented paths**: paths in an answer that do not exist in the repository. They are counted
  separately from existing but unrelated files.
- **Judge tables** have four columns: pairs won by the first build, pairs won by the second, ties,
  and order-dependent pairs. The judge reads every pair twice with the answers swapped; a pair
  counts as won only when both readings agree, otherwise it is order-dependent.
- **Missed paths** (from 10 November): for a required path that an answer did not name, whether
  the server never showed it, listed it, or showed its content.
- **Fixed part of a request** (12 November): the context of a session fitted as a fixed part for
  each model request plus the tool output read again by later requests. It estimates what the
  system prompt and the tool catalog cost on every call.
- **Sample size.** Two or three sessions per task and build, unless a file says otherwise. On
  identical code the judge has split 6 to 1 over 12 pairs, one build on one task has varied by a
  factor of two in tokens, and ten sessions per task have differed by 18 calls in one task and
  by 20 of 230 in total. A difference smaller than that is not a result.

## Rules established by these runs

1. **Put the fact into the response the agent already reads.** A ready batched `get_file` call was
   offered 54 times and used 0 times; `symbols=true` appears in 11 of 263 searches. Facts placed in
   a response are used: the `Tests for this file:` line led to a read of a listed test 10 times
   in 33, and the `Imports:` line was followed 10 times in one task.
2. **Every list in a response becomes a to-do list.** Affected-file sections, dependent lists and
   expanded folder listings were copied into answers or read file by file (2026-11-08,
   2026-11-09). Add a list only if every entry is something the agent should act on.
3. **Calls cost more than characters.** One call resends 28 to 34 thousand tokens of accumulated
   context on average. A change that shortens responses but adds calls loses.
4. **Compare against the exact base commit and count the mechanism.** Count how often the change
   actually fired (maps shown, hints followed, tree calls in a row), so that a total that moved
   for other reasons is not credited to the change.
5. **Check the measuring tools first.** Keys, judge rules and scoring scripts were wrong several
   times (2026-09-29, 2026-11-08). Every surprising result in this log was checked against the
   code before a decision was taken.
6. **Repeat a surprising difference before acting on it.** On 2026-11-12 one build named 139 of
   150 required files against 147 over five sessions per task. Ten new sessions per task, with
   thresholds fixed before the run, gave 121 of 130 against 119. Decide on the new sessions
   only; numbers pooled with the first run repeat the difference that prompted the second.
7. **Measure the same build twice before choosing a threshold.** On 2026-11-12 two series of ten
   sessions per task on one build differed by 18 calls in one task and by 20 in total. A
   threshold of 10 calls per task was crossed by spread alone.
8. **Measure a change with the client it is for.** Codex CLI sends several files in one
   `get_file` call and makes about six calls per session; Claude Haiku asks for one file per call
   and makes about seventeen. A catalog 5,259 characters shorter gave about 400 tokens per
   request with Codex; with Claude the fixed part of a turn was 1,675 tokens larger for a tool
   list 8,701 characters longer.
