import assert from 'node:assert/strict';
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import test from 'node:test';
import { recordEvents } from '../lib/recorder.mjs';
import { validateSeriesConfiguration } from '../lib/series-preflight.mjs';
import { recordStreamJson } from '../lib/stream-json.mjs';
import {
  compareTaskAnswers,
  evaluateTaskAnswer,
  extractNamedPaths,
  loadTaskOracleRegistry,
} from '../lib/task-oracle.mjs';
import { reconcileOrderedAssessments, summarizeOrderedAssessments } from '../lib/order-consistency.mjs';

test('parallel tool calls share one model turn and one usage snapshot', () => {
  const report = recordEvents([
    { type: 'session', clientVersion: '1.0', model: 'model-a', toolLoadingMode: 'dynamic' },
    { type: 'model.usage', turnId: 'turn-1', usage: {
      input_tokens: 100,
      cache_creation_input_tokens: 20,
      cache_read_input_tokens: 30,
      output_tokens: 5,
    }, toolCallIds: ['call-a', 'call-b'] },
    { type: 'tool.call', id: 'call-a', turnId: 'turn-1', name: 'search_project' },
    { type: 'tool.call', id: 'call-b', turnId: 'turn-1', name: 'get_file' },
    { type: 'model.usage', turnId: 'turn-1', usage: {
      input_tokens: 100,
      cache_creation_input_tokens: 20,
      cache_read_input_tokens: 30,
      output_tokens: 5,
    } },
    { type: 'session.end', status: 'success', durationMs: 250 },
  ]);

  assert.equal(report.totals.modelTurns, 1);
  assert.equal(report.totals.toolCalls, 2);
  assert.deepEqual(report.totals.usage, {
    inputTokens: 100,
    cacheWriteTokens: 20,
    cacheReadTokens: 30,
    outputTokens: 5,
  });
  assert.equal(report.turns[0].usageRecords, 2);
});

test('aborted capture retains usage and failed work', () => {
  const report = recordEvents([
    { type: 'model.usage', turnId: 'turn-1', usage: { input_tokens: 40, output_tokens: 2 } },
    { type: 'tool.call', id: 'call-a', turnId: 'turn-1', name: 'search_project', success: false },
    { type: 'mcp.response', turnId: 'turn-1', requestId: 'call-a', wireText: '{"error":true}', success: false },
  ]);

  assert.equal(report.session.status, 'aborted');
  assert.equal(report.session.successful, false);
  assert.equal(report.totals.usage.inputTokens, 40);
  assert.equal(report.totals.failedToolCalls, 1);
  assert.equal(report.totals.failedMcpResponses, 1);
});

test('model turn without tool calls remains a turn', () => {
  const report = recordEvents([
    { type: 'model.input', turnId: 'turn-1', text: 'plain request' },
    { type: 'model.usage', turnId: 'turn-1', usage: { input_tokens: 3, output_tokens: 7 } },
    { type: 'session.end', status: 'success' },
  ]);

  assert.equal(report.totals.modelTurns, 1);
  assert.equal(report.totals.toolCalls, 0);
  assert.equal(report.totals.modelInputBytes, Buffer.byteLength('plain request'));
});

test('error response records wire and decoded boundaries independently', () => {
  const wire = Buffer.from([0, 1, 2, 255]);
  const decoded = 'DPX-MCP-ERROR';
  const report = recordEvents([
    {
      type: 'mcp.response',
      requestId: 'request-1',
      wireBase64: wire.toString('base64'),
      decodedText: decoded,
      success: false,
    },
    { type: 'session.end', status: 'error', durationMs: 12 },
  ]);

  assert.equal(report.totals.wireResponseBytes, 4);
  assert.equal(report.totals.decodedResponseBytes, Buffer.byteLength(decoded));
  assert.equal(report.totals.failedMcpResponses, 1);
  assert.equal(report.session.status, 'error');
});

test('streaming usage fields merge within a turn and sum across turns', () => {
  const report = recordEvents([
    { type: 'model.usage', turnId: 'turn-1', usage: { input_tokens: 10, cache_read_input_tokens: 5 } },
    { type: 'model.usage', turnId: 'turn-1', usage: { output_tokens: 9 } },
    { type: 'model.usage', turnId: 'turn-2', usage: { input_tokens: 3, output_tokens: 2 } },
    { type: 'session.end', status: 'success' },
  ]);

  assert.deepEqual(report.totals.usage, {
    inputTokens: 13,
    cacheWriteTokens: 0,
    cacheReadTokens: 5,
    outputTokens: 11,
  });
  assert.equal(report.totals.modelTurns, 2);
});

test('stream-json keeps parallel calls in one model turn', () => {
  const pinned = validPreflight();
  const report = recordStreamJson([
    { type: 'system', subtype: 'init', session_id: pinned.sessionId, model: pinned.model,
      [`${'clau'}de_code_version`]: pinned.clientVersion },
    { type: `${'assi'}stant`, message: { id: 'message-1', usage: {
      input_tokens: 11, cache_creation_input_tokens: 12, cache_read_input_tokens: 13, output_tokens: 14,
    }, content: [
      { type: 'tool_use', id: 'call-1', name: 'get_file' },
      { type: 'tool_use', id: 'call-2', name: 'get_tree' },
    ] } },
    { type: 'stream_event', event: { type: 'message_start', message: { id: 'message-1', usage: {
      input_tokens: 11, cache_creation_input_tokens: 12, cache_read_input_tokens: 13, output_tokens: 0,
    } } } },
    { type: 'stream_event', event: { type: 'message_delta', usage: { output_tokens: 140 } } },
    { type: `${'assi'}stant`, message: { id: 'message-1', usage: {
      input_tokens: 11, cache_creation_input_tokens: 12, cache_read_input_tokens: 13, output_tokens: 14,
    }, content: [] } },
    { type: 'result', is_error: false, duration_ms: 50 },
  ], pinned);

  assert.equal(report.totals.modelTurns, 1);
  assert.equal(report.totals.toolCalls, 2);
  assert.deepEqual(report.totals.usage, {
    inputTokens: 11,
    cacheWriteTokens: 12,
    cacheReadTokens: 13,
    outputTokens: 140,
  });
  assert.equal(report.capture.actualUsageObserved, true);
  assert.equal(report.capture.completeOutputUsageObserved, true);
});

test('stream-json retains usage when the session is interrupted', () => {
  const pinned = validPreflight();
  const report = recordStreamJson([
    { type: 'system', subtype: 'init', session_id: pinned.sessionId, model: pinned.model,
      [`${'clau'}de_code_version`]: pinned.clientVersion },
    { type: `${'assi'}stant`, message: { id: 'message-1', usage: { input_tokens: 23, output_tokens: 5 },
      content: [{ type: 'tool_use', id: 'call-1', name: 'get_file' }] } },
  ], pinned);

  assert.equal(report.session.status, 'aborted');
  assert.equal(report.totals.usage.inputTokens, 23);
  assert.equal(report.totals.toolCalls, 1);
  assert.equal(report.capture.completedEventObserved, false);
  assert.equal(report.capture.completeOutputUsageObserved, false);
});

test('series preflight rejects each unsafe boundary', () => {
  const cases = [
    configuration => { configuration.mcpConfig.mcpServers.second = {}; },
    configuration => { configuration.recorderConnected = false; },
    configuration => { configuration.sessionState.previousTurns = 1; },
    configuration => { configuration.limits = {}; },
    configuration => { configuration.clientVersion = 'different'; },
    configuration => { delete configuration.evaluator.orderDisagreementRate; },
  ];

  for (const mutate of cases) {
    const configuration = validConfiguration();
    mutate(configuration);
    assert.throws(() => validateSeriesConfiguration(configuration), /Series configuration rejected:/);
  }
});

test('series preflight rejects a reused session identifier and records pinned inputs', () => {
  const configuration = validConfiguration();
  const known = new Set();
  const snapshot = validateSeriesConfiguration(configuration, known);

  assert.equal(snapshot.buildSha, configuration.buildSha);
  assert.equal(snapshot.model, configuration.model);
  assert.equal(snapshot.clientVersion, configuration.clientVersion);
  assert.equal(snapshot.evaluator.orderDisagreementRate, 1 / 3);
  assert.match(snapshot.limitsSha256, /^[0-9a-f]{64}$/);
  assert.throws(() => validateSeriesConfiguration(configuration, known), /session identifier was already used/);
});

function validPreflight() {
  return validateSeriesConfiguration(validConfiguration());
}

function validConfiguration() {
  return {
    mcpConfig: { mcpServers: { compared: {} } },
    recorderConnected: true,
    sessionId: '7d444840-9dc0-11d1-b245-5ffdce74fad2',
    sessionState: { previousTurns: 0, storedPacks: 0 },
    buildSha: '35a8f7a9b660a96074aa6d7ececf38cf95a7f3aa',
    limits: { maxTurns: 40, timeoutMs: 900000, tasks: ['T1'] },
    model: 'model-1',
    expectedModel: 'model-1',
    clientVersion: '2.1.261',
    expectedClientVersion: '2.1.261',
    toolLoadingMode: 'dynamic',
    evaluator: { enabled: true, evaluatedPairs: 18, orderDisagreementRate: 1 / 3 },
  };
}

const oracleFixture = {
  id: 'sample',
  oracleCoverage: 'complete',
  pathExtensions: ['cs', 'md'],
  requiredPaths: ['src/core.cs', 'tests/core.test.cs'],
  optionalPaths: ['docs/guide.md'],
  forbiddenPaths: ['src/unrelated.cs'],
  requiredClaims: [
    { id: 'behavior', terms: [['retries'], ['three times', '3 times']] },
  ],
  requiredSymbols: [
    { id: 'retry-method', terms: ['RetryAsync'] },
  ],
  forbiddenClaims: [
    { id: 'wrong-limit', terms: [['five times', '5 times']] },
  ],
};

test('task oracle accepts a complete correct answer', () => {
  const result = evaluateTaskAnswer(oracleFixture,
    'The `RetryAsync` implementation in `src/core.cs` retries three times; `tests/core.test.cs` verifies it.');

  assert.equal(result.classification, 'complete');
  assert.equal(result.complete, true);
});

test('task oracle rejects a contradicted answer', () => {
  const result = evaluateTaskAnswer(oracleFixture,
    '`RetryAsync` in `src/core.cs` retries five times and `tests/core.test.cs` verifies it.');

  assert.equal(result.classification, 'incorrect');
  assert.deepEqual(result.contradictedClaims, ['wrong-limit']);
});

test('task oracle distinguishes a correct but incomplete answer', () => {
  const result = evaluateTaskAnswer(oracleFixture, '`RetryAsync` in `src/core.cs` retries three times.');

  assert.equal(result.classification, 'incomplete');
  assert.deepEqual(result.missingPaths, ['tests/core.test.cs']);
});

test('task oracle rejects an answer that names an unrelated file', () => {
  const result = evaluateTaskAnswer(oracleFixture,
    '`RetryAsync` in `src/core.cs` retries three times; `tests/core.test.cs` and `src/unrelated.cs` verify it.');

  assert.equal(result.classification, 'incorrect');
  assert.deepEqual(result.unexpectedPaths, ['src/unrelated.cs']);
});

test('task oracle reports an empty answer separately', () => {
  const result = evaluateTaskAnswer(oracleFixture, '');

  assert.equal(result.classification, 'empty');
  assert.equal(result.supported, false);
});

test('task oracle comparison favors the answer covering more required criteria', () => {
  const complete = evaluateTaskAnswer(oracleFixture,
    '`RetryAsync` in `src/core.cs` retries three times; `tests/core.test.cs` verifies it.');
  const incomplete = evaluateTaskAnswer(oracleFixture,
    '`RetryAsync` in `src/core.cs` retries three times.');

  assert.deepEqual(compareTaskAnswers(complete, incomplete), {
    choice: 'A',
    basis: 'classification',
  });
  assert.deepEqual(compareTaskAnswers(incomplete, incomplete), {
    choice: 'tie',
    basis: 'equal-criteria',
  });
});

for (const extension of ['go', 'js', 'jsx', 'tsx', 'rs', 'java', 'kt', 'rb', 'php', 'c', 'h', 'cpp', 'hpp']) {
  test(`task oracle evaluates declared ${extension} paths`, () => {
    const task = pathOracleFixture(extension);
    const complete = evaluateTaskAnswer(task,
      `The implementation is in \`src/main.${extension}\` and satisfies the expected behavior.`);
    const incorrect = evaluateTaskAnswer(task,
      `Use src/main.${extension}; src/forbidden.${extension} also satisfies the expected behavior.`);
    const incomplete = evaluateTaskAnswer(task, 'The expected behavior is implemented.');

    assert.equal(complete.classification, 'complete');
    assert.deepEqual(complete.namedPaths, [`src/main.${extension}`]);
    assert.equal(incorrect.classification, 'incorrect');
    assert.deepEqual(incorrect.unexpectedPaths, [`src/forbidden.${extension}`]);
    assert.equal(incomplete.classification, 'incomplete');
    assert.deepEqual(incomplete.missingPaths, [`src/main.${extension}`]);
  });
}

test('task oracle registry rejects any declared path whose extension is not listed', () => {
  const directory = mkdtempSync(join(tmpdir(), 'mcp-oracle-'));
  const registryPath = join(directory, 'tasks.json');
  try {
    for (const paths of [
      { requiredPaths: ['src/main.zig'] },
      { optionalPaths: ['src/optional.zig'] },
      { forbiddenPaths: ['src/forbidden.zig'] },
    ]) {
      writeFileSync(registryPath, JSON.stringify({
        schemaVersion: 2,
        tasks: [{ ...pathOracleFixture('go'), ...paths }],
      }));

      assert.throws(
        () => loadTaskOracleRegistry(registryPath),
        /uses an extension not listed in pathExtensions/);
    }
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test('task oracle ignores undeclared filenames and sample paths', () => {
  const task = pathOracleFixture('go');
  const answer = [
    'The prose mentions package.json and main.go without declaring either path.',
    'Sample code: `load("examples/demo.go")`.',
    'The expected behavior is implemented.',
  ].join('\n');

  const result = evaluateTaskAnswer(task, answer);

  assert.equal(result.classification, 'incomplete');
  assert.deepEqual(result.namedPaths, []);
  assert.deepEqual(extractNamedPaths(answer, task), []);
});

test('task oracle accepts a declared filename at the repository root', () => {
  const task = {
    ...pathOracleFixture('json'),
    requiredPaths: ['package.json'],
    forbiddenPaths: [],
  };

  const result = evaluateTaskAnswer(task, 'The expected behavior is configured in package.json.');

  assert.equal(result.classification, 'complete');
  assert.deepEqual(result.namedPaths, ['package.json']);
});

test('task oracle requires standalone declared paths and normalizes line-qualified separators', () => {
  const task = pathOracleFixture('go');
  const answer = [
    'Ignore prefix/src/main.go and src/main.go.example.',
    'Use `src\\main.go:12-18` for the expected behavior.',
  ].join('\n');

  const result = evaluateTaskAnswer(task, answer);

  assert.equal(result.classification, 'complete');
  assert.deepEqual(result.namedPaths, ['src/main.go']);
});

function pathOracleFixture(extension) {
  return {
    id: `path-${extension}`,
    oracleCoverage: 'complete',
    pathExtensions: [extension],
    requiredPaths: [`src/main.${extension}`],
    optionalPaths: [],
    forbiddenPaths: [`src/forbidden.${extension}`],
    requiredClaims: [{ id: 'behavior', terms: [['expected behavior']] }],
    requiredSymbols: [],
  };
}

test('order-dependent assessment is reported as disagreement instead of a verdict', () => {
  const result = reconcileOrderedAssessments(
    { order: ['left', 'right'], correctness: 'A', preference: 'A' },
    { order: ['right', 'left'], correctness: 'B', preference: 'A' });

  assert.deepEqual(result.correctness, { status: 'verdict', choice: 'left' });
  assert.deepEqual(result.preference, {
    status: 'disagreement',
    forward: 'left',
    reverse: 'right',
  });

  const report = summarizeOrderedAssessments([{
    forward: { order: ['left', 'right'], correctness: 'A', preference: 'A' },
    reverse: { order: ['right', 'left'], correctness: 'B', preference: 'A' },
  }]);
  assert.equal(report.summary.correctness.disagreementRate, 0);
  assert.equal(report.summary.preference.disagreementRate, 1);
  assert.equal(report.summary.anyDisagreement.disagreementRate, 1);
});
