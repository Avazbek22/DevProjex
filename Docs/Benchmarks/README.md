# Agent benchmark log

One file per day on which an agent benchmark decided something about the DevProjex MCP server.
Each file records what was measured, on which commits, how the sessions were run, what the
numbers were, which changes were kept, which were rolled back, and why. The purpose is practical:
an idea that already failed should not be tried again without new evidence, and a change that
worked should not be undone by accident.

Related documents:

- [Benchmark-History.md](../Benchmark-History.md): the September 2026 series told as a narrative,
  with the agents' and the judge's words quoted verbatim.
- [Benchmarks.md](../Benchmarks.md): scan and pipeline timings, measured without a model.
- The `v5.2-*.json` files in this folder are raw GUI, TUI and journal timing samples quoted in
  [Release-5.2-Stabilization.md](../Release-5.2-Stabilization.md). They are not part of this log.

## Days

| Day | Subject |
|---|---|
| [2026-09-08](2026-09-08.md) | First hands-on comparison with Repomix; the defect list for 5.2 |
| [2026-09-10](2026-09-10.md) | First blind runs; `get_file` hidden by its schema and the redactor breaking code, both fixed in #342 |
| [2026-09-11](2026-09-11.md) | The task profile decides which tool is cheaper; Python constants rolled back; the ranking timing gate is noise |
| [2026-09-13](2026-09-13.md) | One server per session; lighter tool catalog; first declaration body in search results |
| [2026-09-15](2026-09-15.md) | Declaration body limit set to 1,800 characters |
| [2026-09-29](2026-09-29.md) | Release QA runs on four task sets; #457 |
| [2026-09-30](2026-09-30.md) | Final 5.2 series: eleven tasks, eight repositories |
| [2026-11-07](2026-11-07.md) | v5.2.2 against master and Repomix on fourteen tasks |
| [2026-11-08](2026-11-08.md) | #502, #503 and #505 measured; #507 and #508 closed; keys and judge corrected; #509 to #513 measured |
| [2026-11-09](2026-11-09.md) | #516 and #518 kept, #519 closed; blind run on rich and FluentValidation; #517 and #520 |

## How the sessions were run

- **Client.** Headless Claude Code (`claude -p`) with `--strict-mcp-config` and no built-in file
  tools, so the agent can only use the MCP server under test.
- **Model.** Claude Haiku 4.5 (`claude-haiku-4-5-20251001`) for every session and every judge
  call, except the four-server run of 10 September, which used a larger Claude model.
- **Anonymity.** Each server is registered under a neutral name (`server_b`, `server_m` and so on).
  The agent does not know which product it is using. The judge sees the answers as `Answer A`
  and `Answer B`, with product and tool names removed.
- **Builds.** Each file lists the exact commit of every DevProjex build. A build marked
  *local merge* was compiled from a temporary merge of the named PR heads onto the named base,
  because the PRs were not merged yet; that merge commit exists only on the benchmark machine.

## How to read the tables

- **One column per build.** The column header names the build by version and commit, for example
  `v5.2.2 029270bd`. Every row compares the same tasks and the same number of sessions.
- **Context tokens** (November files): every input token the model processed during a session,
  cached input included. Each tool call resends the whole conversation so far, so this number
  follows the number of calls more than the size of individual responses.
- **New input tokens** (September files): input tokens without cache reads, as the client
  reported them at the time. On 8 and 10 September, where marked, tokens were estimated as
  characters returned divided by four.
- **Key files.** Each task has a key written against the repository: required files, required
  tests, files that are acceptable to name, and files known to be unrelated. "59/87" means 59 of
  87 required file mentions over all sessions of that build.
- **Invented paths**: paths in an answer that do not exist in the repository. They are counted
  separately from existing but unrelated files.
- **Judge tables** have four columns: pairs won by the first build, pairs won by the second, ties,
  and order-dependent pairs. The judge reads every pair twice with the answers swapped; a pair
  counts as won only when both readings agree, otherwise it is order-dependent.
- **Sample size.** Two or three sessions per task and build. On identical code the judge has
  split 6 to 1 over 12 pairs, and one build on one task has varied by a factor of two in tokens.
  A difference smaller than that is not a result.

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
