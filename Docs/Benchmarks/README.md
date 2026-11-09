# Benchmark log by day

One file per day on which agent benchmarks decided something about the MCP server: what was
measured and how, what came out, which changes were kept, which were rolled back, and why.
The point is to keep the reasoning next to the numbers, so that an idea that already failed
is not tried again blind, and an idea that worked is not undone by accident.

The September narrative with verbatim agent and judge quotes is in
[Benchmark-History.md](../Benchmark-History.md); scan and pipeline timings are in
[Benchmarks.md](../Benchmarks.md). The `v5.2-*.json` files in this folder are the raw GUI, TUI
and journal samples quoted in [Release-5.2-Stabilization.md](../Release-5.2-Stabilization.md);
they are not part of this log.

## Days

| Day | What was decided |
|---|---|
| [2026-09-08](2026-09-08.md) | First hands-on comparison with Repomix; the list of defects that shaped 5.2 |
| [2026-09-10](2026-09-10.md) | First blind runs: `get_file` was invisible to Claude Code and the redactor broke code; both fixed. Built-in tools fail on large repositories |
| [2026-09-11](2026-09-11.md) | The task profile decides which tool is cheaper; notice suppression saves almost nothing; Python module constants rolled back |
| [2026-09-13](2026-09-13.md) | v5.2 against Repomix on a larger set; instructions removed the `list_projects` step but did not change tree habits; the first search body went to the wrong declaration |
| [2026-09-15](2026-09-15.md) | The search declaration body defaults to 1,800 characters, not 3,000 |
| [2026-09-29](2026-09-29.md) | Release QA runs on four stand sets; the "partial" wording fixed; broad orientation stays the weak spot |
| [2026-09-30](2026-09-30.md) | Final 5.2 series: eleven tasks, eight repositories |
| [2026-11-07](2026-11-07.md) | v5.2.2 against master and Repomix, 126 sessions |
| [2026-11-08](2026-11-08.md) | Batch and impact hints removed; `related_files` naming and change focus tried and dropped; the ruler itself corrected; #509 to #513 measured |
| [2026-11-09](2026-11-09.md) | Blind run on two unseen repositories; large-file outline kept and tuned; tree expansion dropped; cold indexing made faster |

## How to read the numbers

- **DevProjex first, the other side second**, as plain numbers in that order. A table header
  names the pair.
- **Sessions** are headless Claude Code runs with one MCP server per side and no built-in file
  tools, on Claude Haiku 4.5 except the four-server run of 10 September. Each side's server
  name is anonymous to the agent.
- **Context tokens** (November files) count every input token the model processed in a
  session, cached ones included. A tool call costs a full turn of context, so this number
  follows the number of calls more closely than the size of the answers. The September files
  report new input tokens, as the client printed them then.
- **Key files.** Every task has a key written against the code: required files and tests,
  files that are acceptable to name, and files that are known to be unrelated. "Invented
  paths" are paths an answer names that do not exist in the repository. Keys were audited
  against the code on 8 November; see that day for why.
- **The judge** is Claude Haiku 4.5 comparing two anonymous answers in both orders. A verdict
  counts only when both orders agree; "split" pairs are the ones where they did not. Treat a
  judge tally on 12 pairs as a hint, not a result: on identical code it has split 6 to 1.
- **Samples are small.** Two or three runs per task and side. A difference smaller than the
  spread between repeats of the same build is not a difference.

## Rules that came out of these days

1. **Put the fact in the answer the agent is already reading.** Hints that ask for a
   different call form are not followed: a ready batched `get_file` line was offered 54 times
   and used 0 times; `symbols=true` appears in 11 of 263 searches. Facts in the response are
   used: the test names in `get_file`, the import paths in `get_file`.
2. **Any list in a response becomes a to-do list.** Affected-file lists, dependent lists and
   expanded folder listings were copied into answers or read file by file. Add a list only if
   every entry is something the agent should act on.
3. **Fewer calls beat smaller answers.** One call costs a whole turn of accumulated context,
   tens of thousands of tokens late in a session. A change that saves characters but adds a
   call is a loss.
4. **Measure against the right baseline, with mechanism counters.** Compare a build with the
   exact commit it branches from, and count the mechanism itself (outlines shown, tree calls
   in a row, hints followed), so that a total that moves for unrelated reasons is not
   credited to the change.
5. **Fix the ruler before trusting it.** Keys, judge rules and scorers were wrong several
   times. Every surprising result in this log was first checked against the code.
