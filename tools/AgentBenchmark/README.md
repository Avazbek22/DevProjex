# Agent benchmark

Blind sessions of a coding agent that can reach a repository only through one MCP server, scored
against a key written for each task. This is the stand behind the November entries of
[Docs/Benchmarks](../../Docs/Benchmarks/README.md): with it, any series in that log can be run
again from a checkout, against any DevProjex commit and a pinned Repomix version.

The September stand lived in a temporary folder and was lost to a cleanup; this one keeps the
scripts, prompt, tasks and keys in the repository and everything generated in a work folder
outside it.

## What is here

| File | Purpose |
|---|---|
| `tasks/fourteen.json` | Fourteen tasks on eight repositories (Python, TypeScript, C#, Go, Java, Rust), including two special stands |
| `tasks/rich-fluentvalidation.json` | Four tasks on two repositories that no fix was written against |
| `prompt.txt` | The agent prompt; `<REPO_PATH>`, `<SERVER>` and `<TASK_TEXT>` are filled per session |
| `arms.example.json` | Template of an arms file: which neutral server name runs which build |
| `prepare_corpora.py` | Clones the repositories at pinned tags, builds the special stands, verifies all of them |
| `build_hosts.py` | Builds DevProjex at given commits; installs an exact Repomix version |
| `run.mjs` | Runs the sessions (`claude -p`), one folder per session |
| `keyscore.py` | Key metrics: required files and tests, known-wrong files, invented paths, calls, context, cost |
| `judge.py` | Blind pairwise judge (v3), both orders |
| `report.py` | Markdown report of a series |
| `benchlib.py`, `session.mjs` | Shared code; no network, no model |
| `tests/` | Checks that need neither network nor `claude` |

## Requirements

- Node.js 20 or later, Python 3.10 or later, git.
- The `claude` CLI, signed in. Every session and every judge call is billed.
- .NET 10 SDK to build DevProjex hosts; npm to install Repomix.
- About 1 GB of disk for clones and two hosts.

## Work folder

Clones, hosts, MCP configs, indexes and results go to one work folder:

1. `--work DIR` on any command, or
2. the `AGENT_BENCHMARK_WORK` environment variable, or
3. `~/.devprojex-agent-benchmark`.

A folder inside the repository is refused, except `tools/AgentBenchmark/work/`, which is ignored
by git. Do not use the system temporary folder: a cleanup there once emptied a clone and a whole
series ran on an empty project without an error.

```
<work>/corpora/<repo>/              clone the agent sees
<work>/corpora/<repo>.stand.json    its commit and file count, checked before every session
<work>/hosts/<name>/                DevProjex build, host.json records the commit
<work>/repomix/<version>/           pinned Repomix
<work>/data/<series>/<arm>/<repo>/  DEVPROJEX_INTERNAL_DATA_ROOT of one arm
<work>/results/<series>/runs/<task>-<arm>-r<n>/   prompt.txt, stream.jsonl, stderr.txt, summary.json
<work>/results/<series>/arms/<arm>.json           what the arm ran: build, model, prompt
<work>/results/<series>/judge/<name>.json
```

## From nothing to a report

Commands run from `tools/AgentBenchmark`.

1. **Clone the repositories.** Shallow clones at the tags and commits in the task file, checked
   out with `core.autocrlf=true` on every platform, so the bytes match the recorded series.
   ```
   python prepare_corpora.py --tasks-file tasks/fourteen.json --tasks-file tasks/rich-fluentvalidation.json
   ```
   Each clone is checked for its commit (`git log -1`), its tracked-file count, its file count on
   disk and the existence of every path named in its task keys. Two stands are built from data in
   the task file:
   - `httpx-changes` (task N1): a copy of a fresh httpx clone, so every file has a new timestamp
     and the index is stale, plus uncommitted edits. Do not run `git status` inside it; that
     refreshes the index and removes the condition the task measures.
   - `hono-commit` (task D1): the hono clone plus one local commit with a fixed author and date,
     so its SHA is the same on every machine and is pinned as `expectHead`.

   A clone that already passes is left alone; `--force` clones again.

2. **Build the hosts.** One per commit under test, and Repomix at an exact version:
   ```
   python build_hosts.py --host v522=a194709a --host base=<commit> --repomix 1.18.1
   ```
   The commit must exist in the local repository (`--source`, default this checkout); fetch it
   first if not. The build uses `-m:1 -p:BuildInParallel=false`. A host name already holding
   another commit is an error; pick a new name.

3. **Write an arms file** from `arms.example.json`. Keys are neutral server names
   (`server_` plus lowercase letters or digits): the agent sees only that name, and the judge
   has it removed. `product` is the label you will read in reports.
   ```json
   { "server_k": { "product": "v5.2.2 a194709a", "host": "v522" },
     "server_l": { "product": "Repomix 1.18.1", "repomix": "1.18.1" } }
   ```

4. **Check the plan for free**, then run:
   ```
   node run.mjs --tasks-file tasks/fourteen.json --arms-file my-arms.json --dry-run
   node run.mjs --tasks-file tasks/fourteen.json --arms-file my-arms.json --repeats 2
   ```
   Defaults: model `claude-haiku-4-5-20251001`, 2 repeats, 3 sessions in parallel started 20 s
   apart, 40 turns, 15 minutes per session. `--tasks` and `--arms` take comma-separated subsets.
   A session that fails at the API level (rate limit, network) is saved as `failed-*.json` and
   retried on the next run with `--resume`.

5. **Score against the keys:**
   ```
   python keyscore.py --tasks-file tasks/fourteen.json --by-task
   ```

6. **Judge, if a second opinion is wanted:**
   ```
   python judge.py --tasks-file tasks/fourteen.json --ours server_k --theirs server_l --name k-vs-l --answers-only
   ```

7. **Report:**
   ```
   python report.py --tasks-file tasks/fourteen.json --arms server_k=v5.2.2,server_l=Repomix > report.md
   ```

## Cost

On Claude Haiku 4.5 a session costs about $0.10 to $0.30, so plan with $0.2 per session. The
fourteen tasks with two repeats are 28 sessions per arm, about $6; the four-task series is
8 sessions, about $1 to $1.7. A judge pair costs about $0.04 with `--answers-only` and twice
that with the tool verdict. `--dry-run`, `keyscore.py`, `report.py` and the tests are free.

## Rules

1. **One heavy command at a time.** Do not build hosts, run tests or run another series while
   sessions are running: timing and token numbers move with machine load.
2. **Fresh arm names.** An arm name with results in the series folder is refused. A reused name
   once mixed two builds' sessions under one label. `--resume` continues an interrupted arm only
   when its build, model, turn limit and prompt match the record exactly.
3. **One data root per arm.** Each arm gets its own `DEVPROJEX_INTERNAL_DATA_ROOT`, emptied when
   the arm starts, so no arm reuses another's index or cache.
4. **Check the keys before trusting a surprise.** Keys were wrong several times. Every key path
   must exist in the clone (preparation fails otherwise); a key fact that contradicts the code is
   fixed in the task file before the result is used.
5. **Key metrics decide; the judge is a second opinion.** The judge cannot see the code. Its rule
   is that a detail the key neither confirms nor contradicts is not an error; a verdict counts only
   when both orders agree. Use `--answers-only` after a key correction.
6. **Two or three sessions per task are a small sample.** On identical code one task has varied by
   a factor of two in tokens, and the judge has split 6 to 1 over 12 pairs. A smaller difference is
   not a result.
7. **Compare against the exact base commit**, built under its own host name in the same series.

## Reading the report

- **Required files / tests**: mentions of key files (`core` plus `supporting`) and key tests over
  all sessions of the arm, as found / needed.
- **Known-wrong files**: mentions of files the key lists as `unrelated`.
- **Invented paths**: paths with a folder that do not exist in the clone. The clones are verified
  first, so an emptied clone stops the report instead of inflating this number.
- **Tool calls, tool errors, tool output**: from the session stream.
- **Context tokens**: every input token the model processed, cached input included
  (`input + cacheRead + cacheCreation`). Each call resends the whole conversation, so this follows
  the number of calls more than the size of responses.
- **Cost, time**: as the client reported them.
- **Judge tables**: wins of each arm, ties and order-dependent pairs per criterion, then mean 1-5
  scores and the claims the judge flagged.

The path matching is the one used for every number in `Docs/Benchmarks` since 2026-11-08, so new
results compare with the log.

## Task files

```json
{ "series": "fourteen", "autocrlf": true,
  "repos": { "httpx": { "language": "Python", "url": "https://...", "tag": "0.28.1", "commit": "<40 hex>", "files": 125 },
             "httpx-changes": { "language": "Python", "fixture": { "base": "httpx", "kind": "uncommitted-edits", "edits": [ { "path": "...", "old": "...", "new": "..." } ] } } },
  "tasks": [ { "id": "T1", "repo": "httpx", "kind": "bug localization", "text": "...",
               "key": { "core": [], "supporting": [], "tests": [], "acceptable": [], "unrelated": [], "notes": "..." } } ] }
```

`files` is the tracked-file count of the clone. Fixture edits replace the first occurrence of
`old`, written with `\n`, in the file's own line endings; a `commit` fixture adds `commit` with
`message`, `author`, `email`, `date` and optionally `expectHead`. A new series is a new task file
with its own `series` name; its results never mix with another series.

## Tests

```
node --test tools/AgentBenchmark/tests/session.test.mjs
python -m unittest discover -s tools/AgentBenchmark/tests
```

They cover the session summary, arm rules, MCP configs, corpus checks, key metrics on a trimmed
real session, judge bookkeeping and validation of both task files. CI runs them in the
*Ranking Evaluator Tests* job.
