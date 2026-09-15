# MCP usage recorder

All twelve registered tasks now include explicit affirmative contradictions with
pinned source evidence. See [the criteria](oracles/CONTRADICTIONS.md) for what
each assertion means and the remaining language limitations. Denying a false
assertion does not count as making it; merely omitting evidence stays incomplete.

## Deterministic oracle reachability

`reachability.mjs` checks whether the files declared by `oracles/tasks.json` can be surfaced by the
real MCP tools without model calls. The disputable inputs live in `oracles/reachability.json`: pinned
repository commits, task-language search expressions, filename patterns, and starting files that a
declared search or tree input must actually expose before dependency traversal may use them.

From a clean checkout, one command publishes the current server, clones the three pinned repositories,
runs every call sequentially, writes the JSON result, and removes its temporary workspace:

```sh
node tools/McpUsageRecorder/reachability.mjs --output reachability.json
```

For an already prepared server and repository directory, pass
`--server FILE --product-sha FULL_SHA --repositories DIR`. The explicit source identity is mandatory
for a supplied executable, so a stale binary cannot be attributed to the current checkout.
The runner verifies every repository HEAD against the registry before calling the server. A required
file is classified as `one-call`, `two-or-three-call-chain`,
`requires-knowledge-absent-from-task`, or `unreachable`. Exact-path `get_file` is used only as the
control that distinguishes the last two classes; it is never evidence that the task itself supplied
the path. Search boundaries retain inspected, encountered, retained, written, and declaration-named
counts separately, including the exact active limits. The result pins SHA-256 fingerprints of the
registry, task oracles, and executable that produced it, in addition to every repository commit and
the separate source revisions reported for the product build and the analysis program.

This tool converts a client-observed NDJSON capture into one session report without estimating
model tokens from characters. It records MCP response bytes on the wire, decoded response size,
the exact model-input size when the client exposes it, each API usage counter, model turns, tool
calls, client and model identity, tool-loading mode, duration, and session outcome.

Run it with one command:

```text
node tools/McpUsageRecorder/record.mjs --input capture.ndjson --output report.json --client-version 1.2.3 --model example-model --tool-loading-mode dynamic
```

Use `-` for stdin or stdout. Payload text is represented by byte counts and SHA-256 hashes; the
report does not copy project or prompt text.

## End-to-end comparison pipeline

`pipeline.mjs` is the supported entry point for a comparative series. It probes every configured
server, creates or explicitly resumes the immutable store, runs one session at a time, appends each
attempt immediately, evaluates successful answers, checks all accounting boundaries, and emits the
reading analysis in one command:

```text
node tools/McpUsageRecorder/pipeline.mjs --mode new --definition series.json --root results --output report.json
node tools/McpUsageRecorder/pipeline.mjs --mode report --definition series.json --series results/series-id --format markdown
```

Continue an interrupted series with `--mode resume --series results/<series-id>`. Rebuild a report
without starting a server or client with `--mode report`; this mode verifies the saved observations,
run definition, and oracle registry against the series identity before reading raw records. Saved
assessments are report inputs with their own fingerprint and answer-identity checks.

The definition pins `seriesId`, `model`, `clientVersion`, `toolLoadingMode`,
`repetitions`, tasks, arms, limits, and prices. Each task has an `id` and `prompt`. Each arm has an
`id`, its own full lowercase `productBuildSha`, a `server` command, `toolConfiguration`,
and a non-empty `limits` object. A top-level `productBuildSha` is retained as legacy
series provenance only; it never substitutes for either arm's build identity.
The client command receives literal placeholders
`{prompt}`, `{sessionId}`, `{model}`, `{mcpConfigPath}`, `{toolConfigPath}`, and `{arm}` as individual
arguments. The tool-configuration placeholder is mandatory so the fingerprinted configuration is
also the configuration consumed by the client. A minimal
shape is:

```json
{
  "seriesId": "comparison-2026-09-13",
  "productBuildSha": "0123456789012345678901234567890123456789",
  "model": "model-id",
  "clientVersion": "client-version",
  "toolLoadingMode": "dynamic",
  "repetitions": 3,
  "client": {
    "command": "client-command",
    "args": ["--stream-json", "--session-id", "{sessionId}", "--model", "{model}", "--mcp-config", "{mcpConfigPath}", "--tool-config", "{toolConfigPath}", "{prompt}"]
  },
  "tasks": [{ "id": "T1", "prompt": "Pinned task text" }],
  "arms": [
    {
      "id": "baseline",
      "productBuildSha": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
      "limits": { "maximumResults": 200 },
      "server": { "command": "server-command", "args": ["mcp", "serve"] },
      "toolConfiguration": { "allowedTools": ["get_file"] }
    },
    {
      "id": "candidate",
      "productBuildSha": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
      "limits": { "maximumResults": 200 },
      "server": { "command": "server-command", "args": ["mcp", "serve"] },
      "toolConfiguration": { "allowedTools": ["search_project", "get_file"] }
    }
  ],
  "limits": {
    "maxAttemptsPerAssignment": 2,
    "probeTimeoutMs": 30000,
    "sessionTimeoutMs": 900000,
    "smallFileCharacters": 12000
  },
  "pricing": {
    "currency": "USD",
    "perMillionTokens": {
      "inputTokens": 0,
      "cacheWriteTokens": 0,
      "cacheReadTokens": 0,
      "outputTokens": 0
    }
  },
  "evaluation": {
    "orderCalibration": { "evaluatedPairs": 18, "orderDisagreementRate": 0.3333333333333333 },
    "oracleRegistry": "oracles/tasks.json",
    "savedAssessments": "saved-assessments.json"
  }
}
```

Before the first session, each arm must return non-empty server instructions and a complete
`tools/list` response, including every wire page. Instructions, catalog, configuration,
build SHA, and merged limits have independent fingerprints for each arm; an arm never
borrows another arm's snapshot. The client and model identities, complete run definition,
oracle contents, prices, and any supplied recorded assessments also become
series identity. Creation refuses an existing series directory. Resume
refuses any identity difference. A stored assignment is skipped only when the store finds a matching
successful raw record; failed or aborted work receives a new attempt number and remains in usage and
cost totals.

Every process capture is written with create-only semantics before it is adapted. Its SHA-256 is
stored in the session record, and report generation requires a one-to-one, fingerprint-matched
mapping between captures and records. A malformed stream retains every parseable usage event; a
capture that cannot be assigned safely blocks the report instead of becoming a zero-cost session.

The stream adapter retains model turns, tool calls and inputs, model-visible tool results, exact
per-result token counts supplied by the client, wire and decoded response boundaries, observable
model-input boundaries, duration, outcome, and final answer. The carry-cost report requires an exact
client-observed token count for every analyzed tool result and refuses to substitute a character
estimate. Client-reported duration and measured wall-clock duration remain separate.

Evaluation uses the deterministic task oracle first. Ties and still-unverified partial
criteria require both candidate orders. Use `evaluation.savedAssessments` for recorded
assessments, or replace that field with a `judge` command whose arguments consume
`{assessmentInputPath}`. The input is JSON containing the task, criteria, independent
correctness/preference dimensions, and anonymous answers A/B. The command returns JSON
with separate `correctness` and `preference` choices (`A`, `B`, or `tie`). It is invoked
sequentially in both orders. Each successful order and every failed attempt is stored
create-only; a retry does not overwrite the first order or its process capture.

The calibration sample size and measured order-disagreement rate are required before
either mode starts; they are supplied measurement provenance, not inferred from a
new series. Each assessment pins the SHA-256 of both candidate
answers after the `Experience` section has been removed. A verdict is accepted only when the two
orders identify the same candidate; correctness and preference disagreement counts and rates are
reported separately as top-level `orderDisagreement`, also in the Markdown table.
Recorded fake-server, fake-client, and judge-response fixtures exercise the complete
command without any model request. No additional model sessions were used to validate it.

The JSON `table` contains each arm's model turns, tool calls, all four usage counters,
cost from pinned rates, wall duration, per-task oracle classifications, and separate
ordered correctness/preference results. Error/aborted attempts remain in totals.
Before releasing a table the pipeline replays raw client transcripts and verifies
their turns, calls, usage, final answers, durations, and capture fingerprints against
the immutable records; it also verifies session-to-arm sums. A missing successful
assignment, changed identity, altered transcript, invalid judgment, or accounting
mismatch blocks release. Missing usage is never replaced with character/4 estimates.
Absent per-turn API usage or an unobserved output counter also blocks the table,
instead of treating unknown cost as zero. Failed-launch attempts are the explicit
zero-usage case because the client never started. Partial captures remain stored
even when their incomplete usage prevents a conclusive comparison.

Server/model/client commands and SHAs are explicitly pinned inputs, not inferred
from directory names. The runner checks client-reported model, version and session
identity, but cannot derive a Git SHA from an arbitrary server executable. Build
publication must bind its command to the declared commit. Server limits other than
the runner's attempt/probe/session deadlines must be configured in the pinned startup
arguments and client tool configuration; the runner does not invent server flags.

The reading analysis classifies whole-file `get_file` calls as `known-section-unused`,
`small-whole-read`, or `large-needs-address`, with counts, characters, and share of all tool-result
characters. A read chain is batchable only when every address after its first element was present in
a tool result before the chain began. Tool carry cost is the observed result-token count multiplied
by the number of later model turns, aggregated and sorted by tool.

## Capture events

Each input line is one JSON object:

- `session`: optional `clientVersion`, `model`, `toolLoadingMode`, and `startedAt`.
- `mcp.response`: exactly one of `wireBase64` or `wireText`, optional `decodedText`, `requestId`,
  `turnId`, and `success`.
- `model.input`: `turnId` plus observable `text`, or `bytes` and optional `sha256` when the adapter
  can observe only the encoded request.
- `model.usage`: `turnId`, a `usage` object, optional `toolCallIds`, and `responseError`. Snake-case
  API fields and camel-case recorder fields are accepted.
- `tool.call`: stable `id`, `turnId`, optional `name`, and `success`.
- `session.end`: `status` (`success`, `error`, or `aborted`), optional `endedAt` and `durationMs`.

Usage is grouped by `turnId`. Multiple snapshots from streaming events or parallel tool calls use
the maximum observed value of each counter for that turn, never their sum. Counters are summed only
across distinct model turns. If the capture ends without `session.end`, the report marks it aborted
and retains every observed usage counter and failed call.

The four model counters remain separate: ordinary input, prompt-cache write, prompt-cache read, and
output. `modelTurns` and `toolCalls` are also separate. A client adapter should emit `model.input`
only at the final request boundary; if that boundary is not observable, omit the event rather than
substituting MCP wire bytes.

Run the parser tests with:

```text
node --test tools/McpUsageRecorder/tests/recorder.test.mjs
```

## Immutable experiment series

An experiment series is created or resumed explicitly. Its configuration file contains the series
identifier, product build SHA, model, client version, tool-loading mode, server instructions, tool
configuration, limits, and per-million-token prices for all four usage counters. The stored manifest
keeps SHA-256 fingerprints instead of copying instructions and configurations into every result.

```json
{
  "seriesId": "comparison-2026-09-13",
  "productBuildSha": "0123456789012345678901234567890123456789",
  "model": "model-id",
  "clientVersion": "client-version",
  "toolLoadingMode": "dynamic",
  "serverInstructions": "Pinned server instructions",
  "toolConfiguration": { "allowedTools": ["search_project", "get_file"] },
  "limits": { "maxTurns": 40, "timeoutMs": 900000 },
  "pricing": {
    "currency": "USD",
    "perMillionTokens": {
      "inputTokens": 0,
      "cacheWriteTokens": 0,
      "cacheReadTokens": 0,
      "outputTokens": 0
    }
  }
}
```

```text
node tools/McpUsageRecorder/series.mjs new --root results --configuration series-config.json
node tools/McpUsageRecorder/series.mjs resume --series results/series-id --configuration series-config.json
node tools/McpUsageRecorder/series.mjs append --series results/series-id --report session.json --task task-id --repetition 1 --arm baseline
node tools/McpUsageRecorder/series.mjs summarize --series results/series-id --output summary.json
```

`new` refuses an existing series directory. `resume` compares every series-level identity field and
refuses any mismatch. Each task/repetition/arm slot is written once. An existing raw record is
reused only when its complete identity, session identifier, four usage counters, cost, and outcome
are identical; otherwise `append` fails and names the mismatched field. A different series therefore
always has a different directory, while continuing an existing series is an explicit checked action.
Session cost is derived from the four raw usage counters and the pinned price table; `append` does not
accept a manually calculated cost.

The summary is derived only from the immutable raw records. Before emitting rows it verifies that
turn usage sums to each session total, session usage sums to each arm total, every record belongs to
the manifest's single series, storage slots are unique, and session identifiers are not reused.
Error and aborted sessions remain in both usage and cost totals.

`series-preflight.mjs` rejects a comparison unless one server, a fresh empty session, the recorder,
the full build SHA, pinned limits, model, and client version are all present. When a comparison uses
a qualitative evaluator, its measured candidate-order disagreement rate and sample size are also
required. `stream-json.mjs`
converts streamed client events into the same report while grouping repeated usage snapshots and
parallel tool calls by the model response identifier.

## Saved-answer evaluation

`oracles/tasks.json` records task-specific required files, factual claims, and `pathExtensions`.
Every declared required, optional, or forbidden path must use an extension listed by its task, so a
new language is enabled in registry data rather than in extractor code. Registry loading fails when
these declarations disagree.

A file is named only when the answer contains a complete, standalone path declared by that task.
The path ends at its exact declared spelling; an adjacent `:line`, `:start-end`, `::test_name`,
`:SymbolName`, `#L42`, or `#anchor` is its selector, while any other adjacent character rejects
the match. Either slash style is accepted. An undeclared bare filename such as `package.json`, or
an undeclared path shown in prose or sample code, is ignored. Code formatting does not exempt a
declared path: an exact declared token still counts. The
deterministic oracle retains the complete/incomplete/incorrect/empty coverage categories for
comparison, but reports evidence and correctness separately: `requiredEvidence` is found/missing,
`knownContradictions` is detected/not-detected, and `semanticCorrectness` is always
requires-separate-check. The report prints «обязательные основания найдены» when applicable,
«известное противоречие обнаружено» on a match, otherwise «ни одно из заданных запрещённых
утверждений не обнаружено», and always «содержательная правильность требует отдельной проверки».
Absence of a finite phrase match is not proof of semantic correctness, even for tasks whose
declared oracle coverage is complete. Tasks marked `partial` also retain their specific list of
criteria requiring qualitative assessment.

Qualitative comparisons keep correctness and preference as separate dimensions. Each pair is
assessed in both candidate orders. A verdict exists only when both orders map to the same candidate;
otherwise the result is an order disagreement. Every aggregate reports the disagreement count and
rate for both dimensions and for either dimension.

Evaluate saved summaries with:

```text
node tools/McpUsageRecorder/evaluate-saved.mjs --oracles tools/McpUsageRecorder/oracles/tasks.json --assessments ordered-assessments.json summary.json
```
