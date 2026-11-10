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
| `run.mjs` | Runs Claude sessions by default, or explicitly selected Codex arms, one folder per session |
| `keyscore.py` | Key metrics: required files and tests, known-wrong files, invented paths, calls, context, cost |
| `judge.py` | Blind pairwise judge (v3), both orders |
| `report.py` | Markdown report of a series |
| `benchlib.py`, `session.mjs` | Shared code; no network, no model |
| `codex.mjs` | Codex configuration, execution and stream normalization |
| `transcripts.py`, `attribution.py` | Client-neutral recorded calls and evidence for required paths |
| `provenance.py` | Reject successful calls that read another corpus or an unpinned remote |
| `replay.py` | Repeat recorded MCP calls without a model; compare hosts or recorded text |
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

Paid sessions run only on the owner's explicit request.

Use the cheapest model that reliably calls MCP tools: Claude Haiku 4.5 for Claude sessions, the documented cheapest suitable model for Codex sessions; never larger models for benchmark series.

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

## Codex sessions

Codex is an explicit arm option, not a replacement for the default Claude path. Use an installed
Codex CLI (validated with 0.167.0) with existing file-based authentication in `CODEX_HOME/auth.json`
or `~/.codex/auth.json`. The runner copies that file into a private per-session home and deletes
the copy afterwards; it never changes the original configuration or starts a login flow.
Keyring-only authentication is not supported by this isolated path.

The default is `gpt-6-luna`, verified in the signed-in model catalog and with an MCP call.
On 2026-11-10 its published Standard API rates per million tokens were $0.10 uncached input,
$0.01 cached input, $0.125 cache writes and $0.50 output
([model and pricing](https://developers.openai.com/api/docs/models/gpt-6-luna)). It was cheaper
than the other listed small model, [GPT-5.6 Luna](https://developers.openai.com/api/docs/models/gpt-5.6-luna)
($0.20 input, $0.02 cached, $1.20 output).
Availability and prices can change: check `codex debug models` and the official price before a
new paid series. `--model ID` overrides the arm/default model; a different model requires an
explicit matching `pricing` object in the arm, with a source and rates per million tokens.

```json
{
  "server_k": { "product": "v5.2.2 <commit>", "host": "v522", "agent": "codex" },
  "server_l": { "product": "Repomix 1.19.0", "repomix": "1.19.0", "agent": "codex" }
}
```

The arm record includes agent, model, CLI version, isolation profile and prices. Codex results
use `<series>-codex`; one invocation refuses a mixture of Claude and Codex arms. Resuming still
requires the recorded fingerprint. The source task file and `prompt.txt` remain the same.

```text
node run.mjs --tasks-file tasks/rich-fluentvalidation.json --arms-file codex-arms.json --tasks P1,C1 --repeats 1 --parallel 1 --budget-usd 3 --dry-run
node run.mjs --tasks-file tasks/rich-fluentvalidation.json --arms-file codex-arms.json --tasks P1,C1 --repeats 1 --parallel 1 --budget-usd 3
python keyscore.py --tasks-file tasks/rich-fluentvalidation.json --agent codex --tasks P1,C1 --by-task
python report.py --tasks-file tasks/rich-fluentvalidation.json --agent codex --tasks P1,C1
```

`--codex-bin` selects a native executable or the npm JavaScript launcher. Windows npm shims are
resolved to their JavaScript launcher, without shell interpolation. For Codex, `--max-turns`
bounds MCP call count; `numTurns` records completed CLI turns, not Claude's turn unit. Sessions
have the same timeout. A budget requires serial execution, includes recorded attempts on resume,
and stops subsequent sessions when completed spend or the first-session projection exceeds the
limit, or usage is incomplete. This is a between-session check, not a provider billing limit.

### Isolation

Each session runs `codex exec --json --ephemeral --strict-config --ignore-rules` from an empty
workspace outside the corpus, with the read-only sandbox, no project instructions, no user
configuration, and exactly the arm's MCP server in its generated private configuration. Only
that server is preapproved; its process runs with the corpus as its own working directory.
Shell, browser, computer, image, apps, plugins, memories, hooks and collaboration features are
disabled. The installed CLI's model catalog is restricted to direct tool presentation with no
shell, patch or Node execution and no deferred discovery. The model ID and inference provider
are unchanged. This avoids a code-execution wrapper being needed to reach MCP tools.

The profile uses the documented [`model_catalog_json`](https://learn.chatgpt.com/docs/config-file/config-reference)
setting. The [0.167.0 tool registry](https://github.com/openai/codex/blob/rust-v0.167.0/codex-rs/core/src/tools/spec_plan.rs)
gates shell and patch registration on those capabilities; the real probe checks the installed binary too.
The read-only sandbox does **not** establish a filesystem read jail. The guarantee is therefore
a restricted tool surface plus a fail-closed transcript check, not an OS-level read boundary.
Every started/updated/completed item is checked: only MCP calls to the selected server and text
or reasoning items are admitted. Foreign servers, built-in tools, malformed or unknown events
mark `isolation.status = "violated"` and `eligibleForMetrics = false`. Unsupported tool calls
reported on stderr are rejected too. Such sessions remain on
disk, retain their cost, appear separately in reports, and never enter key metrics. Failed or
incomplete Codex sessions are also excluded. Before a new CLI version is used, repeat the direct
read challenge on a random marker outside the empty workspace and verify an actual MCP success.

Corpus provenance is checked separately for both clients. Successful local packs and project
selectors must remain inside the prepared corpus; remote packs need its exact repository and
immutable commit. Reads of unknown packed outputs fail the check. Failed tool attempts that
return no corpus data do not invalidate a later local read. The runner stops when provenance
cannot be established, preserves the transcript and cost, and excludes that session from metrics.
Python must be available (`python` on Windows, `python3` elsewhere); set
`AGENT_BENCHMARK_PYTHON` to choose the executable. Historical transcripts are checked in memory
when loaded; scoring never rewrites their files.

`stream.jsonl` contains the unmodified CLI JSONL. `summary.json` maps MCP names to
`mcp__<server>__<tool>`, collects response characters/errors, the final answer and token usage.
Input excludes cache reads/writes, which have their own existing fields. Cost is calculated
from the recorded rates and marked `costSource = "calculated"`; `costComplete` distinguishes
finished usage from an interrupted lower bound. A ChatGPT sign-in may consume subscription
allowance rather than incur the API dollar estimate: the CLI does not expose an invoice.
Codex series use key metrics; `judge.py --agent codex` refuses before starting Claude.

The committed Codex stream fixture is a shortened real 0.167.0 isolation-probe stream. Its thread
identifier is replaced; no credential, user path or corpus content is included. Tests use this
fixture and a fake CLI, and never need an installed Codex or a model call.

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
- **Cost, time**: as the client reported them; Codex cost is the recorded API-rate estimate above.
- **Judge tables**: wins of each arm, ties and order-dependent pairs per criterion, then mean 1-5
  scores and the claims the judge flagged.

Relative paths keep the historical suffix matching. Absolute paths are converted to relative
paths only inside the prepared corpus root, including Windows drive paths in Markdown links;
paths outside the corpus cannot satisfy a key. Separators and parent segments are normalized
before checking that boundary. Recalculate older summaries when comparing path counts.

### Path evidence and replay

When `stream.jsonl` and `prompt.txt` are available, key metrics also count missing required
paths as `never-shown`, `listed`, or `content-shown`. The first means the path never appeared
in a response, the second includes trees and dependency references, and the third requires
observed source text, including numbered search windows and partial reads. Requested paths alone
are not evidence of delivery. Each required path records its first showing call/tool and first
content call/tool through `attribution.attribute`; named paths receive the same classification.
Sessions lacking transcripts are counted separately, not classified as never shown.
Repomix snippets without an observed file boundary cannot be attributed to a source file;
unlabelled code is not guessed from its contents. The categories measure observable delivery,
not whether the final explanation is correct or whether a whole file was read.

Replay needs no client sign-in and makes no model call:

```text
python replay.py --session <run-folder> --corpus <prepared-clone> --host <devprojex> --output <new-folder>
python replay.py --session <run-folder> --corpus <prepared-clone> --host <before> --other-host <after> --output <new-folder>
python replay.py --session <run-folder> --corpus <prepared-clone> --host <devprojex> --recorded --output <new-folder>
```

The default compares two fresh server sessions of the same host. The corpus must be pinned and
prepared first; use a new output folder. Calls retain their order and arguments; absolute corpus
paths are relocated and stored-result IDs follow the corresponding newly created result.
Each run saves complete responses and durations, plus per-tool counts and full unified diffs.
Normalization is deliberately narrow: line endings, exact corpus/state roots and their path
separators, spotlight envelope nonces, and stored-result IDs. Source timestamps, arbitrary IDs,
durations in source code, numbers and file contents are preserved. New response-level timing
fields must be identified and tested before adding a normalization; unexplained differences
must not be hidden by broader substitutions.

## Task key versions

New run summaries record the task's `keyVersion`. A summary without this field always uses
version 1, including after a task key changes. The current key remains in `key`; `previousKeys`
preserves earlier versions verbatim. Historical results and published tables are not rewritten.
`keyscore.py` and `report.py` disclose the versions used; `--key-version N` explicitly recalculates
selected tasks against that version without editing their summaries. Select only tasks that have
the requested version. `--key-version current` uses each selected task's latest key, including a
mixture of versions. Mixed recorded versions are listed separately in the version label.

D1 version 2 distinguishes message-only exceptions from supplied responses and names the exact
explicit Content-Type value. JWT/JWK supplied-response cases are acceptable context, not required
fallback coverage. Request cloning, proxy validation and the timeout default are included as
message-only producers, alongside their tests. For the original D1 counts use
`keyscore.py --tasks-file tasks/fourteen.json --tasks D1 --key-version 1` (plus the usual work/agent
options); use `--key-version 2` for the corrected coverage set. Existing verdict files remain
historical; this option recalculates key metrics, not model judgments.

T5 version 2 includes tests that use positional JSON response arguments. T6 and C2 version 2
measure the mandatory shared registration/documentation surfaces without requiring several
alternative templates together; template suitability remains outside the path-only metric.
T7 version 2 corrects the matching algorithm description without changing path counts.
T8 version 2 distinguishes signature edits from useful indirect regression tests.

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
