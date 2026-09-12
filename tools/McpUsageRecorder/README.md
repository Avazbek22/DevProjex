# MCP usage recorder

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

`series-preflight.mjs` rejects a comparison unless one server, a fresh empty session, the recorder,
the full build SHA, pinned limits, model, and client version are all present. When a comparison uses
a qualitative evaluator, its measured candidate-order disagreement rate and sample size are also
required. `stream-json.mjs`
converts streamed client events into the same report while grouping repeated usage snapshots and
parallel tool calls by the model response identifier.

## Saved-answer evaluation

`oracles/tasks.json` records task-specific required files and factual claims. The deterministic
oracle classifies each saved answer as complete, incomplete, incorrect, or empty and reports every
missing, unexpected, or contradicted criterion. Tasks marked `partial` retain an explicit list of
criteria that still require qualitative assessment.

Qualitative comparisons keep correctness and preference as separate dimensions. Each pair is
assessed in both candidate orders. A verdict exists only when both orders map to the same candidate;
otherwise the result is an order disagreement. Every aggregate reports the disagreement count and
rate for both dimensions and for either dimension.

Evaluate saved summaries with:

```text
node tools/McpUsageRecorder/evaluate-saved.mjs --oracles tools/McpUsageRecorder/oracles/tasks.json --assessments ordered-assessments.json summary.json
```
