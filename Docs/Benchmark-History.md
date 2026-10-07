# Benchmark history: from 5.1 to 5.2

How the DevProjex MCP server was measured against Repomix during September 2026, what the
agents and the blind judge said, and what changed in the product because of it. Every
section names the date, the DevProjex commit under test and the Repomix version. In every
comparison DevProjex comes first and Repomix second; "tokens" are the new input tokens Claude
Code reports for a session, with cache reads counted separately. Quotations are verbatim from
the session logs; the servers were anonymous to the agents (`server_a`, `server_b`) and to
the judge (`Answer A`, `Answer B`).

## The short version

Final series, 29 to 30 September, DevProjex `02c0fde7` against Repomix 1.18.1: eleven tasks
on eight open-source repositories in six languages, every task run twice per tool, 22
sessions per side, Claude Haiku 4.5 as the agent and as the blind judge.

- **973,020 tokens against 1,174,907** for the same 22 sessions, at $4.07 against $4.25.
- **85 of 90 required files found against 83 of 90**; all key files 115 of 136 against 102 of 136.
- **The judge trusted the DevProjex answer in 17 pairs of 22**, Repomix in 3; correctness 16 to 3,
  actionability 18 to 1.
- **Secrets:** both tools leaked 0 of 7 planted credentials; the code around them stayed
  readable through DevProjex in 7 of 7 files and through Repomix in 1 of 7.
- **Where Repomix wins:** broad "how does X work" questions, where packing once and grepping
  is cheaper, and the total number of tool calls (461 against 490).

Three facts behind the numbers:

1. **The task decides the winner, not the tool.** Packing wins broad orientation; reading only
   what a search found wins questions with a specific answer. Measured on 11 September:
   broad exploration 82 calls and 70,825 tokens against 47 and 50,199; precise lookup 33 calls
   and 23,450 tokens against 29 and 50,393.
2. **Trust follows `file:line`.** In every verdict that preferred DevProjex the agent or the
   judge named line references it could verify. Repomix answers cite positions inside the
   packed file, and the judge treated those as wrong.
3. **Agents decide to read a whole file right after a result, with no reasoning.** So every
   improvement that worked was placed inside tool results. Instructions and descriptions
   moved the first step of a session and nothing after it.

## What the agents said

Why they kept DevProjex:

> "Server B's `pack_codebase` generated a 190k-token full codebase dump upfront, which was
> unnecessary for this focused search task. I only needed about 5% of that content."
> (duel, httpx, 10 September)

> "To get equivalent information, server_b required reading the entire 744K-token packed
> codebase, while server_a's targeted file reads and searches stayed surgical."
> (hono, 10 September)

> "The trust signal was specificity: line numbers were consistent across multiple tool calls,
> and I verified the logic by reading exactly the code referenced. server_b's line numbers came
> from a repacked file that I had to read in separate calls." (httpx, 11 September)

> "server_b required creating a 196k-token packed output that included unrelated code;
> subsequent searches and reads must reference packed-output IDs and line numbers rather than
> actual file paths." (Serilog, 11 September)

> "Server_b's grep encountered result size limits and had to save output to disk, creating
> extra work. Server_a's search results included 'Best declaration body' showing the exact code
> snippet, which I verified matched the file reads." (duel, httpx, 29 September)

What the judge wrote when it chose DevProjex:

> "Answer B's line numbers are fabricated (off by 10,000+ lines: `context.go:975` claimed as
> 18597, `gin.go:482` as 22179), missing the supporting `context_appengine.go` file."
> (gin, bug localization)

> "Answer A provides correct line numbers (71, 96) while Answer B's are drastically wrong
> (6107, 6218)." (gson, dependency impact)

Why they sometimes kept Repomix:

> "Structured XML output that could be systematically searched and jumped to via line
> numbers. server_a requires multiple search queries with regex patterns that can return
> partia[l results]." (Serilog, change impact, 11 September; the log cuts the sentence off)

> "Server_b's grep with context lines let me find exact matches and read only the relevant
> sections. Precise grep results with file paths and line numbers; packed output was
> well-indexed and searchable." (hono, change impact, 10 September)

> "Both answers correctly identify the six-rule order and core files, but Answer B is more
> complete and precise: it includes supporting files (Since.java, FieldAtt[ributes]..."
> (the judge's one Repomix win on gin, gson and axum; the log cuts the sentence off)

What they complained about in DevProjex, each of which became a fix:

> "The server instructions advertise a `get_file` tool, but it was not in the tool list."
> (10 September, fixed the same day in #342)

> "Secret redaction rewrote ordinary source at `httpx/_client.py:471`, replacing
> `password=password` with a placeholder. That is not a secret, and it means any packed code
> can be silently altered." (10 September, fixed in #342)

> "No line numbers in search results made it harder to cross-reference between files."
> (11 September, fixed in #366)

> "Every search says '[Search skipped] 7 selected binary files... Results are partial' although
> only real binaries were skipped." (29 September, fixed in #457)

## 5 to 8 September: the 5.1 engine

*DevProjex `c249c309` for the CLI scan and `647769cf` (PR #305) for the first agent run ·
Repomix 1.17.0, then 1.18.0.*

**The CLI cold scan**, both tools following `.gitignore` and checking for secrets:

| Corpus | DevProjex ms | Repomix ms | DevProjex peak RSS | Repomix peak RSS |
|---|---:|---:|---:|---:|
| Flask (236 files) | 2,805 | 548 | 106.6 MiB | 291.4 MiB |
| Godot (14,261 files, 327 MB) | 62,445 | 8,269 | 1,037.6 MiB | 3,478.0 MiB |

The content pipeline was rebuilt after that measurement. On the same pinned inputs the
reviewed medians became 1,092 ms for Flask and 10,391 ms for Godot export, 3.25 times faster
on Godot and short of the 4 times that was the target; peak memory on Godot did not improve,
because the peak is detector and runtime churn rather than retained text. The two tools
select different file sets, so these are not identical-workload rankings; the exact commands
are in [Benchmarks.md](Benchmarks.md).

**The first agent run.** A Claude subagent that did not know which server was which answered
four questions about Flask and the Repomix repository, with at most 14 calls per run.

- 22 calls and about 22,300 tokens against 21 calls and about 37,300 (estimated as characters
  divided by four at that time).
- All eight answers correct. The agent trusted DevProjex in 4 of 4 runs because every result
  carried a `file:line` reference, and called `pack_context` with `detail=signatures` "the best
  single call of the exercise".
- Its defect list: search re-printed overlapping context for adjacent hits; `pack_context`
  did not document its enum values; `project` rejected the name `list_projects` had just
  returned; `related_files` took 1.6 s cold against 38 ms warm; `get_file` read one range per
  call. Four of the five went into PRs #308 and #331 within two days: merged context windows,
  project names accepted, dependency facts warmed after discovery, and batch reads of up to
  eight files and sixteen ranges.

## 10 September: the first blind runs

*DevProjex `e056940e`, fixed the same day in PR #342 (`22ab97bc`) · Repomix 1.18.0 ·
`encode/httpx` 0.28.1 (about 8,800 lines) and `honojs/hono` v4.13.7 (about 42,800 lines) · six
tasks, keys from merged pull requests · the only series on a larger Claude model.*

**Four anonymous arms** in separate headless Claude Code sessions, three httpx tasks, two
runs each, medians:

| Arm | Calls | Tokens | Seconds |
|---|---:|---:|---:|
| DevProjex | 11 | 29,100 | 94 |
| Serena | 14 | 28,400 | 96 |
| Repomix | 13.5 | 42,200 | 117 |
| filesystem MCP (control) | 15.5 | 88,100 | 166 |

Every arm found every core file in every run, so these tasks measure cost. Every DevProjex
agent gave a trust score of 4 of 5 and explained the missing point the same way: the phantom
`get_file` and the rewritten `password=` line quoted above.

**Two blockers, fixed in #342 the same day.** Claude Code had silently dropped `get_file`
because the batch-read feature had put a top-level `not` into its input schema; the client
registered seven tools instead of eight. A contract test now rejects `not`, `allOf`, `anyOf`,
`if`, `then`, `else`, `$ref` and `const` in every tool schema. The connection-string detector
matched `password=<identifier>` in plain code and cut the rest of the line; it was rewritten
as a region parser that knows where a value can start. After the fix no agent mentioned
either problem again, and trust scores moved from all 4s to two 5s and six 4s.

**Built-in tools against DevProjex** (24 sessions, Claude Haiku 4.5): stock Claude Code with
only `Read`, `Grep` and `Glob`, against DevProjex before and after the fix.

- httpx (small): built-in tools 26,600 tokens and 14 calls against 32,900 and 16 through
  DevProjex, same answers. Read plus grep is enough here and the tool catalog is pure cost.
- hono (large): built-in tools found 0 of 4 core files and hit the 25-turn cap in 3 of 4
  sessions; DevProjex found 4 of 4 for 49,300 tokens.
- The `get_file` fix was worth 55,600 to 49,300 tokens on hono; `get_file` became the most
  used tool of the run (62 calls, ahead of `search_project` at 61).
- The honest claim: not "cheaper than a plain agent", but "keeps the agent inside its budget
  on a repository where built-in tools alone do not finish".

**Duels.** One agent gets both servers in one session, must use both, and says which it
would keep. After #342 it kept DevProjex in 8 of 8 duels in both orderings, median trust 5
against 4. Recurring reasons: "surgical precision: search with patterns, jump to exact lines,
extract only what's needed" and "attribution was clear at every step"; the recurring
complaint: "requires chaining multiple calls to correlate related functions". A duel measures
preference, not economy: the agent did most of its work through DevProjex (16 calls against
4), so the low Repomix counts mean it was barely used.

**The sequential format** of the same day, one agent solving each task twice with one server
at a time, split 2 to 2 on the keep verdict. One of its agents noticed the line-number issue
before any judge did: "the line numbers differ slightly because server_b reports absolute line
positions in the packed XML file while server_a reports relative line numbers."

## 11 September: the task profile decides the winner

*DevProjex `61b5f004` (PR #353) and `acbfa6bb` (PR #366) · Repomix 1.18.0 · httpx, hono and
Serilog v4.3.0 (C#) · one agent, both servers, each task solved twice · Claude Haiku 4.5 from
here on.*

**Three repositories, six tasks.** Kept DevProjex in 5 of 6, trusted it more in 3 (Repomix 2,
one tie), mean trust 4.67 against 4.50. Repomix needed less: 86 calls and 79,821 tokens
against 59 and 63,879. Correctness was identical on every task. The verdicts in both
directions:

- httpx bug localization: "I never needed to load 190K tokens of unrelated code; I went
  directly to the relevant sections."
- hono change impact: "Server_b's grep results were quantitatively impressive but fragmented.
  I never saw the full `JSONRespond` interface definition in one readable chunk, only separate
  matches. The 194 match output forced paging through results to sample them."
- hono bug localization, trusted Repomix: "exact line numbers, inline context"; DevProjex was
  marked down for "no line numbers in search results". The search output of that build
  repeated the full path on every match and context line and buried the numbers (`#362`,
  fixed in `#366`).
- Serilog change impact, kept Repomix: the "structured XML output" verdict quoted above;
  DevProjex "required multiple narrowing searches to converge on the answer".

**The same binary split by task shape** (`acbfa6bb`, same format):

| Task profile | DevProjex | Repomix |
|---|---:|---:|
| Broad exploration: vaguely stated bug localization and change impact | 82 calls, 70,825 tokens | 47 calls, 50,199 tokens |
| Precise lookup: an exact value, the blast radius of an internal interface, a minimal reading set, a question whose obvious answer is wrong | 33 calls, 23,450 tokens | 29 calls, 50,393 tokens |

On the precise set DevProjex was cheaper on all four tasks individually, kept in 3 of 4, with
trust 5, 5, 5, 4 against 4, 4, 3. Packing once and then grepping pays one large fixed cost and
reads cheaply afterwards, so it wins when the question needs a whole area. Reading only what a
search found pays nothing up front, so it wins when the question has a specific answer. From
this point on the question was never "which is cheaper" but "cheaper for what".

**Two probes without a model.**

- Secrets: each server was handed a fixture with seven credentials. Credentials that reached
  the caller: 0 of 7 for both. Code around the credentials still readable afterwards: 7 of 7
  through DevProjex, 1 of 7 through Repomix, which excludes a file once it finds a secret in
  it. The class, its members, the connection-string keys and the environment variable names
  disappear with the file.
- Latency: the first probe said DevProjex is ready 2.7 times sooner (432 to 468 ms to tools
  listed, against 1,168 to 1,245 ms). Re-measured on `5f54ea38`, the advantage cancels: the
  first DevProjex search warms an inventory and costs 1.0 to 1.8 s, repeat searches 15 to
  80 ms; Repomix packs a whole repository in about 0.4 s and greps it in 4 to 29 ms. Time to a
  first answer is a tie, 1.4 to 2.3 s against 1.5 to 1.8 s.

**Twelve whole-file reads, examined one by one.** Seven were reflex reads of a file whose
declaration the agent already had in hand (about 26 KB). Three happened because alphabetical
truncation never listed the wanted file (about 46 KB; `src/context.ts` alone was 22,900
characters after a search capped at 30 of 891 matches). Two were name guesses from a tree
listing with zero yield. In 8 of 12 the agent had already received the declaration name, and
in all 12 the call followed the previous result with no visible reasoning. A tool description
is read once at session start and cannot influence that moment, so from here on every change
meant to steer the agent was placed inside result text. In the same run the capabilities
shipped for exactly this purpose went unused: symbol-anchored reads 0 of 30 `get_file` calls,
`with_symbols` 0, `expand_related` 0, whole-file reads still 12 of 30.

**Where response bytes go** (PR #347): capping a wide search took one result from 43,182 to
16,519 characters. Suppressing the repeated `[Effective filters]` and `[Protection]` notices
saved 217 characters on tree and pack responses and exactly 1 character on `get_file` and
`search_project`, while the one-time cost grew: instructions 885 to 1,044 characters,
`tools/list` 33,816 to 34,612. The cap is where the money is; the banners never were.

## 13 to 15 September: closing the gap one lever at a time

*DevProjex `5f54ea38` (PRs #386, #387, #389), `0cdf95b7` (PR #396), `1f4796ec` (PR #402),
`581e52f7` (PR #404), `1515c4a3` (PR #414) · Repomix 1.18.0.*

**Search headers, session-stored results, a breadth-first slice of large result sets and
declaration-first ordering** (`5f54ea38`) on the six-task set, one repeat each:

| DevProjex build | DevProjex calls | DevProjex tokens | Repomix calls | Repomix tokens |
|---|---:|---:|---:|---:|
| `61b5f004`, 11 September | 86 | 79,800 | 59 | 63,900 |
| `acbfa6bb`, 11 September | 82 | 70,800 | 47 | 50,200 |
| caps lifted | 65 | 74,000 | 41 | 56,700 |
| `5f54ea38`, 13 September | 71 | 60,700 | 50 | 62,100 |

Searches became cheaper, not fewer: 25 searches cost less than the previous arm's 19, and the
in-session verdicts of the `5f54ea38` arm kept DevProjex on 4 of 6 tasks ("efficient targeted
results with precise line numbers immediately"). The
nudge that pointed agents to `read_pack` fired in 7 of 25 searches and was followed 0 times;
the agent went to `get_file` (4), `get_tree` (1) or Repomix's `pack_codebase` (2), and the
same response carried an older line telling it to narrow the pattern instead. Later hints were
therefore designed as refusals that carry the correct call rather than as suggestions.

**The first order-aware blind series** (`0cdf95b7`, six tasks, two repeats, 24 sessions,
$3.43):

| Metric | DevProjex | Repomix |
|---|---:|---:|
| Context tokens | 419,684 | 439,185 |
| Tool calls | 234 | 214 |
| Turns | 246 | 226 |
| Cache reads | 7,058,934 | 6,417,294 |
| Cost | $1.73 | $1.70 |
| Required paths missed | 13 of 52 | 22 of 52 |

The judge read every pair in both orders and counted a verdict only when the orders agreed:
correctness 11 to 1 with no disagreement, while preference disagreed with itself on half the
pairs. Correctness is a stable judged metric; preference is not. Two earlier conclusions did
not survive: the token deficit of 11 September was gone on this set, and the broad-task rule
split, with one broad task cheaper for DevProjex and another for Repomix. Run-to-run spread
got its number: the same task on the same server cost 45,824 tokens once and 23,299 the next
time, so per-task gaps under two times are noise and only aggregates carry.

**Catalog slimming** (`#399` and `#401`, old build `0cdf95b7` against `1f4796ec`, four tasks,
$1.04):
the tool catalog that every turn carries went from 8,370 to 6,735 tokens; sessions that
opened with a wasted `list_projects` call went from 3 of 4 to 0; cache reads fell from
2,077,395 to 1,947,818 over 76 turns, 129,577 fewer and exactly the predicted amount; cost
$0.528 to $0.515, time 256 to 237 s. A brace-glob example added to the instructions fired 0
times in either arm and single-directory tree calls only went from 11 to 8: instruction prose
moves the first step of a session, not habits.

**Declaration bodies in search results** (`#403`, `1f4796ec` against `581e52f7`, four tasks,
$1.03): turns 75 to 61, calls 71 to 57, searches 31 to 23, wall time 277 to 228 s, cache reads
12.6 percent lower, but response characters 24.5 percent higher and required paths missed
2 of 18 to 5 of 18. The transcripts showed why: the body went to the first declaration in path
order, not the best match; 5 of 15 bodies came from test or benchmark files, and on Serilog
one body went to a declaration whose name did not contain the searched term at all. The fix
(`#406`) chose the body by name match, with the deterministic order only as a tie-break.

**The clean measurement of the body limit** (`1515c4a3`, one binary, three settings, four
tasks, three repeats, 36 sessions, $3.9):

| Body limit | Turns | Cost | Complete answers | Required paths missed | Contradictions |
|---|---:|---:|---:|---:|---:|
| off | 173 | $1.27 | 2 of 12 | 22 of 54 | 0 |
| 1,800 characters | 161 | $1.21 | 5 of 12 | 14 of 54 | 0 |
| 3,000 characters | 179 | $1.38 | 2 of 12 | 30 of 54 | 1 |

3,000 was the shipping default at the time and lost on every axis, including the first answer
the oracle ever graded as incorrect. The default moved to 1,800 (`#415`). The earlier
recommendation to
raise the limit had come from declaration-size statistics (70 percent of declarations fit in
1,800 characters); size statistics did not predict behaviour, sessions did.

## 29 September: release QA and the last fixes

*DevProjex `e263e503` and `2d3da84a` (PR #456), `fa876cca` (PR #457), `1ed69509` · Repomix
1.18.0, then 1.18.1.*

**The release QA build, four tasks, solo and in duels** (`e263e503`, one repeat). Solo
totals:

| Metric | DevProjex | Repomix |
|---|---:|---:|
| Tool calls | 84 | 120 |
| Turns | 88 | 124 |
| Tool output, characters | 285,000 | 432,000 |
| Context tokens | 165,000 | 241,000 |
| Cost | $0.66 | $1.02 |
| Time | 300 s | 412 s |
| Core files found | 6 of 7 | 7 of 7 |
| Supporting files found | 3 of 8 | 5 of 8 |
| Test files found | 4 of 5 | 3 of 5 |

DevProjex was cheaper on every one of the four tasks and missed one core file (the agent listed
every call site but not the defining `httpx/_transports/default.py`). In the duels it was kept
4 of 4 in both orders with trust 5 every time against 1 to 3 for Repomix, and the agent spent 66
calls on it against 12. Two verdicts from the reversed order, where DevProjex was `server_b`:
"Server_b provides clear error messages with actionable guidance when projects aren't found
('use project=#1'), whereas server_a silently proceeded without project validation", and
"Server_a (Repomix) required cross-referencing packed output where line numbers were offsets
into the con[catenated file]" (the log cuts the sentence off).

**The six-task series rerun on `2d3da84a`** (two repeats, $3.39 plus a $0.42 judge):

| Metric | DevProjex | Repomix |
|---|---:|---:|
| Context tokens | 393,337 | 452,869 |
| Tool calls | 209 | 242 |
| Turns | 221 | 254 |
| Cost | $1.58 | $1.81 |
| Time | 784 s | 837 s |
| Required paths missed | 4 of 42 | 8 of 42 |
| All key paths missed | 21 of 68 | 24 of 68 |

Judge correctness 7 to 3 with two order-dependent pairs. DevProjex won change impact (93,650
tokens against 133,625, 45 calls against 74, judge 2 to 0), dependency impact (judge 2 to 0)
and precise fact (half the calls, judge 2 to 0). It lost broad feature orientation: on one
task the judge went 0 to 2 because the agents skipped the docs and type files (supporting
files 1 of 4 against 4 of 4), on another DevProjex needed 33 calls against 22. One Repomix
session hit the turn limit with no answer.

**What the DevProjex agents complained about**, and what `#457` did about it:

| Complaint from the notes | Change |
|---|---|
| "[Search skipped] 7 selected binary files... Results are partial" on every hono and Serilog search; three agents re-queried because of it | Binaries reported as skipped, the search as complete |
| "Search shows uses rather than definitions"; no definitions-only search in MCP | `search_project` gained `symbols: true` |
| Agents guessed `project: "#0"` in 2 of 12 sessions and passed a `/C:/...` path in 1 of 12 | Both forms accepted |
| Truncated declaration bodies force extra reads | The truncation notice names the exact lines to read |
| An ambiguous `symbol` failed with no way forward | The error lists the candidate line ranges |

The A/B (old build, first version, final version; six tasks, three repeats per arm, $6.89):
context 582,000, 578,000 and 544,000 tokens; cost $2.34, $2.37 and $2.26; tool errors 8, 3
and 3; project-address errors 6, 0 and 1; binary complaints 6, 0 and 1. The per-cell spread
is about 25 percent, so the token change is inside noise and the result is counted as friction
removed, not as a token win. The judge's correctness verdict between the final and the old
build was 8 to 4 with three ties and three order-dependent pairs, and its preference 8 to 8,
which is not significant either.

**New stacks** (`fa876cca`, Repomix 1.18.1): gin v1.12.0 (Go, 130 files), gson 2.14.0 (Java,
311 files), axum 0.8.9 (Rust, 490 files), six tasks, two repeats, $4.02 plus a $0.46 judge.

| Metric | DevProjex | Repomix |
|---|---:|---:|
| Context tokens | 453,000 | 570,000 |
| Tool calls | 216 | 258 |
| Cost | $1.78 | $2.24 |
| Time | 868 s | 941 s |
| Tool output, characters | 862,000 | 1,093,000 |
| Tool errors | 3 | 2 |
| Required paths missed | 0 of 52 | 6 of 52 |
| All key paths missed | 10 of 78 | 22 of 78 |
| Self-reported trust, mean | 4.25 | 4.17 |

DevProjex was cheaper on five of six tasks (dependency impact on gson 28 calls against 52,
change impact on axum 32 against 54); the one loss was feature orientation on gson, the broad
profile again. The judge gave correctness and preference 11 to 1 with no order-dependent
pairs. Its reasons were dominated by one observation, quoted above: Repomix answers cite line
numbers of the packed file (references above line 3,000 in a 1,483-line file), and the judge
counted them as wrong; in about 4 of the 11 wins that was the main reason. All 38 `file:line`
references in the DevProjex answers were within the files' real line counts. The three
DevProjex tool errors were an invented `start_range` argument, a bad `start_line` value and
`get_file` on a directory.

**Five languages** (`1ed69509`, Repomix 1.18.1): jsoup 1.23.2 (Java, change impact), click
8.5.0 (Python, bug localization), fmt 12.2.0 (C++, precise fact), MediatR 14.2.0 (C#,
dependency impact), chi 5.3.2 (Go, feature orientation); tasks written by five separate
agents; two repeats. Totals: 534,000 tokens and 253 calls against 573,000 and 268; required
paths missed 2 of 38 against 5 of 38; all key paths missed 10 of 58 against 17 of 58. The judge
in this run scored five dimensions on the answers and three on the agents' scrubbed experience
notes:

| Judge verdict, 10 pairs read in both orders | DevProjex | Repomix | Tied or order-dependent |
|---|---:|---:|---:|
| Correctness | 7 | 1 | 2 |
| Completeness | 5 | 0 | 5 |
| Trust | 9 | 1 | 0 |
| Actionability | 8 | 1 | 1 |
| Preference | 7 | 1 | 2 |
| Tool convenience, from the notes | 8 | 0 | 2 |
| The agent's trust in its own tool | 5 | 1 | 4 |
| Efficiency | 6 | 4 | 0 |

Mean scores on a 1 to 5 scale, DevProjex against Repomix: trust 3.70 against 1.35,
actionability 3.80 against 1.60, correctness 3.55 against 2.15, completeness 3.45 against 2.60.

The "biggest pain" each agent reported, side by side:

| Repomix agents | DevProjex agents |
|---|---|
| "grep searches returning oversized results (110k chars) forcing linear reading" | "truncation warnings requiring re-verification" |
| "packed markdown format with line numbers but no file paths" | "regex pattern trial-and-error to find tests" |
| "can't read file ranges by line number; forced progressive grep calls" | "symbol search doesn't support regex patterns" |
| "oversized search results exhausting token budget, forcing multiple narrow searches" | "sequential multi-step investigation with no dependency graph" |
| "excessive Mediator pattern match volume (230+ results)" | "high noise on precision/width searches" |

The two losses were Java change impact (111,533 tokens against 96,229) and Python bug
localization (131,349 against 126,101, 61 calls against 45). The judge was checked as well: it
flagged 54 claims in DevProjex answers and 61 in Repomix answers as suspicious, and several of
its DevProjex flags were wrong (chi `_examples/versions/main.go`, the MediatR sample
`Publisher.cs` and the chi tests `TestMuxNestedNotFound` and `TestTreeFindPattern` all exist,
and its own CP1 reference note about fmt's error messages was wrong). One DevProjex answer
named a click test that does not exist.

## 29 to 30 September: the 5.2 picture

*DevProjex `02c0fde7`, 96 commits before the 5.2 tag · Repomix 1.18.1 · the six stack tasks
and the five language tasks, two repeats each, $8.32 for the sessions and $1.97 for the
judge.*

| Metric | DevProjex | Repomix |
|---|---:|---:|
| Context tokens, 22 sessions | 973,020 | 1,174,907 |
| Tool calls | 490 | 461 |
| Cost | $4.07 | $4.25 |
| Time | 2,005 s | 1,814 s |
| Tool errors | 5 | 1 |
| Tool output, characters | 1,822,300 | 2,317,503 |
| Required files found | 85 of 90 | 83 of 90 |
| All key files found | 115 of 136 | 102 of 136 |

| Judge verdict, 22 pairs read in both orders | DevProjex | Repomix | Tied or order-dependent |
|---|---:|---:|---:|
| Correctness | 16 | 3 | 3 |
| Trust | 17 | 3 | 2 |
| Actionability | 18 | 1 | 3 |
| Completeness | 16 | 1 | 5 |
| Preference | 16 | 3 | 3 |
| Tool convenience, from the notes | 13 | 4 | 5 |
| The agent's trust in its own tool | 6 | 7 | 9 |
| Efficiency | 12 | 5 | 5 |

Mean scores on a 1 to 5 scale, DevProjex against Repomix: trust 3.83 against 1.74,
actionability 3.99 against 1.72, correctness 3.82 against 2.59, completeness 3.91 against
2.84. The pack-line-number caveat from the new-stacks run applies to the trust and
actionability rows; the token, call and file counts do not depend on the judge.

Per task, each row the sum of two sessions:

| Task | Repository | Tokens, DevProjex / Repomix | Calls, DevProjex / Repomix | Required files, DevProjex / Repomix |
|---|---|---:|---:|---:|
| G1 bug localization | gin (Go) | 68,959 / 88,283 | 29 / 30 | 6 of 8 / 6 of 8 |
| G2 precise fact | gin (Go) | 73,661 / 90,950 | 39 / 39 | 4 of 4 / 3 of 4 |
| J1 dependency impact | gson (Java) | 64,334 / 85,160 | 32 / 42 | 14 of 14 / 14 of 14 |
| J2 feature orientation | gson (Java) | 79,233 / 65,145 | 46 / 28 | 12 of 12 / 12 of 12 |
| R1 change impact | axum (Rust) | 84,702 / 115,294 | 34 / 36 | 4 of 4 / 4 of 4 |
| R2 feature orientation | axum (Rust) | 108,760 / 131,273 | 71 / 46 | 10 of 10 / 9 of 10 |
| JV1 change impact | jsoup (Java) | 105,201 / 112,890 | 47 / 36 | 7 of 8 / 8 of 8 |
| PY1 bug localization | click (Python) | 111,724 / 116,400 | 45 / 49 | 4 of 4 / 4 of 4 |
| CP1 precise fact | fmt (C++) | 114,391 / 159,511 | 68 / 66 | 6 of 6 / 5 of 6 |
| CS1 dependency impact | MediatR (C#) | 84,809 / 90,409 | 42 / 50 | 12 of 12 / 12 of 12 |
| GO1 feature orientation | chi (Go) | 77,246 / 119,592 | 37 / 39 | 6 of 8 / 6 of 8 |

The row where Repomix is ahead on tokens is feature orientation on gson, and the rows where
it needs fewer calls are the two feature-orientation tasks on gson and axum, the profile
found on 11 September. The agents' trust in their own tool is the one judged dimension
Repomix leads, by one. Hints placed inside results were followed unevenly: the directory
refusal that carries the right `get_tree` call 1 of 1 times, the several-ranges hint 0 of 2,
the tree-depth hint 4 of 8. The five DevProjex tool errors were two invented `start_range`
arguments, one `get_file` without a path, one `get_file` on a directory and one search
rejected for its arguments, against one Repomix grep that searched for a file header inside
its own pack.

## What changed, in one table

| Finding | Change | Measured effect |
|---|---|---|
| Claude Code dropped `get_file` over a `not` in its schema (10 Sep) | #342: portable schema subset, contract test | hono 55,600 to 49,300 tokens; complaint gone |
| Redactor rewrote `password=auth[1]` in plain code (10 Sep) | #342: connection-string region parser | complaint gone; trust scores from all 4s to two 5s and six 4s |
| Search output hid line numbers behind repeated paths (11 Sep) | #366: path written once | later verdicts cite DevProjex line numbers as the trust signal |
| A wide search returned 43,182 characters (11 Sep) | #347: 16,000-character cap | 16,519 characters |
| Whole-file reads are decided in the result, not the description (11 Sep) | #387 with the search fixes merged beside it (#366, #368): declaration headers, stored results, breadth-first slice | six-task tokens 79,800 on `61b5f004` to 60,700 on `5f54ea38` |
| The tool catalog cost 8,370 tokens every turn (13 Sep) | #399, #401: slimmer catalog, root count in instructions | 6,735 per turn; opening `list_projects` 3 of 4 sessions to 0 |
| Declaration body in search results (13 to 15 Sep) | #403, #406, #415: body by name match, default 1,800 | turns 173 to 161, required paths missed 22 to 14 of 54 |
| "Partial" results, no declaration search, guessed project addresses (29 Sep) | #457 | tool errors 8 to 3; binary complaints 6 to 0 |
| "Partial" still read as a warning when only the output cap applied (29 Sep) | `1ed69509`: boundary line names the cap | complaints 3 of 12 sessions to 0 |

## What the month taught

1. The task profile, not the product, decides which tool is cheaper. Packing wins broad
   orientation; targeted reading wins questions with a specific answer. Both are true at once.
2. Trust follows attribution. Every verdict for DevProjex named verifiable `file:line`
   references; every verdict for Repomix named exhaustive grep over one snapshot, or a
   DevProjex defect that hid line numbers at the time.
3. An agent decides to read a whole file immediately after reading a result. Guidance meant
   to change that decision has to be in the result; descriptions and instructions move the
   first step of a session and nothing after it.
4. Judged correctness is stable across answer order; judged preference is not. Quote
   preference only with its disagreement rate.
5. Size statistics do not predict agent behaviour. The 3,000-character default looked right
   on a histogram and lost on every measured axis.
6. Replacing a secret value keeps the code around it readable; dropping the file does not.
7. Built-in file tools are enough on a small repository, where an MCP catalog is pure
   overhead, and run out of budget on a large one.
8. Identical sessions differ by 20 to 25 percent. Per-task comparisons under two times are
   noise; only aggregates with the sample size attached mean anything.

## Method and caveats

- **Sessions.** Headless Claude Code with one MCP server per arm, a turn cap of 25 to 40, one
  to three sessions in parallel. The four-arm run of 10 September used a larger Claude model
  and exhausted the budget; every series after it ran on Claude Haiku 4.5. Repomix ran in its
  default MCP mode through `npx`, version pinned per series.
- **Judge.** Claude Haiku 4.5 reads both answers with product and tool names scrubbed, in both
  orders; a verdict counts only when both orders agree, and the number of order-dependent
  pairs is reported with every tally. It rewarded verifiable `file:line` references, which
  penalised Repomix answers that cite positions inside the packed file; token, call and file
  counts do not depend on it. Its own errors are listed above.
- **Tasks.** Written by the author of DevProjex, and for the five-language set by five separate
  agents, with keys taken from merged pull requests or verified by `git grep`. The same set
  that exposed a defect was used to confirm its fix, which makes this a development benchmark
  and not an independent one. Repositories were pinned by tag or commit.
- **Sample sizes.** One to three repeats per cell; the largest series is 22 sessions per side.
- **Tokens and cost.** New input tokens and cost as reported by the client's usage counters.
  The 8 September numbers predate that harness and were estimated as characters divided by
  four.
- **Provenance.** The harness lived in a temporary folder outside the repository and was
  lost to a cleanup on 29 September; the scripts, every summary table and the verdicts quoted
  here were recovered from the session log, but the per-session transcripts and the judge's
  raw outputs for the final series were not. The next series runs from
  `tools/AgentBenchmark` inside the repository so that it can be re-run from a checkout.
