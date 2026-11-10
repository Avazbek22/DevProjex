// Runner pieces without the claude CLI: summaries, arm rules, MCP configs and corpus checks.
import assert from 'node:assert/strict';
import { existsSync, mkdirSync, mkdtempSync, readFileSync, readdirSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';
import {
  DEFAULT_MODEL, armFingerprint, buildMcpConfig, countWorktreeFiles, dataRoot, fingerprintMismatch,
  isTransientFailure, parseArgs, quoteForCmd, renderPrompt, resolveArm, summarize, usedArms, validateArms,
  armAgent, armModel, agentSeries,
} from '../session.mjs';
import { CODEX_MODEL, CODEX_PRICING, budgetStop, codexArguments, codexConfiguration, codexPricing, executeCodex,
  restrictedCatalog, saveCodexSummary, summarizeCodex } from '../codex.mjs';

const HERE = dirname(fileURLToPath(import.meta.url));
const TOOL_DIR = join(HERE, '..');
const job = { id: 'T1-server_b-r1', task: { id: 'T1', repo: 'httpx' }, server: 'server_b', product: 'v5.2.2', repeat: 1 };

function scratch() {
  return mkdtempSync(join(tmpdir(), 'agent-benchmark-'));
}

test('the default model is Haiku 4.5', () => {
  assert.equal(DEFAULT_MODEL, 'claude-haiku-4-5-20251001');
});

test('Claude summary bytes and arm records match the unchanged base', () => {
  const stream = readFileSync(join(HERE, 'fixtures', 'stream.jsonl'), 'utf8');
  assert.equal(JSON.stringify(summarize(job, stream, 0, 5000), null, 2) + '\n', readFileSync(join(HERE, 'fixtures', 'claude-summary.json'), 'utf8').replaceAll('\r\n', '\n'));
  const resolved = { kind: 'devprojex', product: 'v5.2.2', host: 'v522', commit: 'a'.repeat(40) };
  assert.equal(JSON.stringify(armFingerprint(resolved, DEFAULT_MODEL, 40, 'prompt'), null, 1), readFileSync(join(HERE, 'fixtures', 'claude-arm.json'), 'utf8').replaceAll('\r\n', '\n'));
});

test('summarize collects calls, errors, usage and the final answer', () => {
  const stream = readFileSync(join(HERE, 'fixtures', 'stream.jsonl'), 'utf8');
  const summary = summarize(job, stream, 0, 5000);
  assert.deepEqual(summary.toolCalls.map((call) => [call.name, call.isError]),
    [['mcp__server_b__get_tree', false], ['mcp__server_b__get_file', true]]);
  assert.equal(summary.toolResponseChars, 'src/a.py\nsrc/b.py'.length + 'not found'.length);
  assert.equal(summary.usage.input, 15);
  assert.equal(summary.usage.cacheRead, 300);
  assert.equal(summary.usage.model, 'claude-haiku-4-5-20251001');
  assert.ok(Math.abs(summary.usage.costUsd - 0.011) < 1e-9);
  assert.equal(summary.usage.totalContextTokens, 15 + 40 + 123);
  assert.equal(summary.numTurns, 3);
  assert.equal(summary.durationMs, 1234);
  assert.equal(summary.error, null);
  assert.match(summary.answer, /src\/a\.py/);
  assert.equal(isTransientFailure(summary), false);
});

test('a session without a result event is an error, and an empty one is transient', () => {
  const summary = summarize(job, '', 1, 10);
  assert.equal(summary.error, 'no result event');
  assert.equal(isTransientFailure(summary), true);
  const limited = summarize(job, JSON.stringify({ type: 'result', is_error: true, api_error_status: 529, result: 'overloaded' }), 1, 10);
  assert.match(limited.error, /^api 529/);
  assert.equal(isTransientFailure(limited), true);
});

test('arm names must be neutral and each arm names one product source', () => {
  assert.deepEqual(validateArms(JSON.parse(readFileSync(join(TOOL_DIR, 'arms.example.json'), 'utf8'))), []);
  const errors = validateArms({ devprojex: { product: 'x', host: 'a' }, server_d: { product: 'y', host: 'a', repomix: '1' }, server_e: { host: 'a' } });
  assert.equal(errors.length, 3);
});

test('a used arm name is detected from results and from arm records', () => {
  const results = scratch();
  try {
    assert.deepEqual(usedArms(results, ['server_b']), []);
    mkdirSync(join(results, 'runs', 'T1-server_b-r1'), { recursive: true });
    mkdirSync(join(results, 'arms'), { recursive: true });
    writeFileSync(join(results, 'arms', 'server_c.json'), '{}');
    assert.deepEqual(usedArms(results, ['server_b', 'server_c', 'server_d']), ['server_b', 'server_c']);
    // server_b must not match server_b2.
    assert.deepEqual(usedArms(results, ['server_b2']), []);
  } finally {
    rmSync(results, { recursive: true, force: true });
  }
});

test('a resumed arm must match its recorded fingerprint', () => {
  const resolved = { kind: 'devprojex', product: 'v5.2.2', host: 'v522', commit: 'a'.repeat(40) };
  const recorded = armFingerprint(resolved, DEFAULT_MODEL, 40, 'prompt');
  assert.deepEqual(fingerprintMismatch(recorded, armFingerprint(resolved, DEFAULT_MODEL, '40', 'prompt')), []);
  assert.deepEqual(fingerprintMismatch(recorded, armFingerprint({ ...resolved, commit: 'b'.repeat(40) }, DEFAULT_MODEL, 40, 'prompt')), ['commit']);
  assert.deepEqual(fingerprintMismatch(recorded, armFingerprint(resolved, 'other-model', 40, 'prompt')), ['model']);
});

test('every arm gets its own DevProjex data root', () => {
  const resolved = { kind: 'devprojex', executable: '/w/hosts/v522/devprojex' };
  const first = buildMcpConfig('server_b', resolved, '/w/corpora/httpx', dataRoot('/w', 'fourteen', 'server_b', 'httpx'));
  const second = buildMcpConfig('server_d', resolved, '/w/corpora/httpx', dataRoot('/w', 'fourteen', 'server_d', 'httpx'));
  assert.deepEqual(first.mcpServers.server_b.args, ['mcp', '--root', '/w/corpora/httpx']);
  assert.notEqual(first.mcpServers.server_b.env.DEVPROJEX_INTERNAL_DATA_ROOT, second.mcpServers.server_d.env.DEVPROJEX_INTERNAL_DATA_ROOT);
  const repomix = buildMcpConfig('server_c', { kind: 'repomix', executable: '/w/repomix.cjs' }, '/w/corpora/httpx', 'unused');
  assert.deepEqual(repomix.mcpServers.server_c, { command: 'node', args: ['/w/repomix.cjs', '--mcp'] });
});

test('an arm whose host is not built is refused', () => {
  const work = scratch();
  try {
    assert.throws(() => resolveArm(work, 'server_b', { product: 'x', host: 'v522' }), /not built/);
    assert.throws(() => resolveArm(work, 'server_c', { product: 'x', repomix: '1.18.1' }), /not installed/);
    mkdirSync(join(work, 'hosts', 'v522'), { recursive: true });
    writeFileSync(join(work, 'hosts', 'v522', 'host.json'), JSON.stringify({ commit: 'c'.repeat(40), executable: 'devprojex' }));
    writeFileSync(join(work, 'hosts', 'v522', 'devprojex'), '');
    assert.equal(resolveArm(work, 'server_b', { product: 'x', host: 'v522' }).commit, 'c'.repeat(40));
  } finally {
    rmSync(work, { recursive: true, force: true });
  }
});

test('the prompt template fills path, server and task', () => {
  const template = readFileSync(join(TOOL_DIR, 'prompt.txt'), 'utf8');
  const prompt = renderPrompt(template, '/w/corpora/httpx', 'server_b', 'Find the rule.');
  assert.doesNotMatch(prompt, /<REPO_PATH>|<SERVER>|<TASK_TEXT>/);
  assert.match(prompt, /"server_b"/);
  assert.match(prompt, /## Experience/);
});

test('worktree files are counted without .git', () => {
  const root = scratch();
  try {
    mkdirSync(join(root, '.git', 'objects'), { recursive: true });
    mkdirSync(join(root, 'src'), { recursive: true });
    writeFileSync(join(root, '.git', 'objects', 'x'), '');
    writeFileSync(join(root, 'a.py'), '');
    writeFileSync(join(root, 'src', 'b.py'), '');
    assert.equal(countWorktreeFiles(root), 2);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test('arguments parse with types and reject unknown options', () => {
  const defaults = { repeats: 2, model: DEFAULT_MODEL, resume: false };
  assert.deepEqual(parseArgs(['--repeats', '3', '--resume'], defaults), { repeats: 3, model: DEFAULT_MODEL, resume: true });
  assert.throws(() => parseArgs(['--nope', '1'], defaults), /unknown option/);
  assert.throws(() => parseArgs(['--repeats', 'x'], defaults), /number/);
  assert.throws(() => parseArgs(['--model'], defaults), /needs a value/);
});

test('cmd quoting keeps empty values and paths with spaces intact', () => {
  assert.equal(quoteForCmd(''), '""');
  assert.equal(quoteForCmd('C:/a b/c.json'), '"C:/a b/c.json"');
  assert.equal(quoteForCmd('mcp__server_b__*'), 'mcp__server_b__*');
});

const codexJob = { ...job, server: 'server_probe', agent: 'codex', model: CODEX_MODEL };
const codexStream = readFileSync(join(HERE, 'fixtures', 'codex-stream.jsonl'), 'utf8');

test('Codex opts into a separate series and model while Claude defaults stay unchanged', () => {
  assert.equal(armAgent({}), 'claude');
  assert.equal(armModel({}), DEFAULT_MODEL);
  assert.equal(armModel({ agent: 'codex' }), CODEX_MODEL);
  assert.equal(armModel({ agent: 'codex' }, 'custom'), 'custom');
  assert.equal(agentSeries('fourteen', 'claude'), 'fourteen');
  assert.equal(agentSeries('fourteen', 'codex'), 'fourteen-codex');
  assert.match(validateArms({ server_b: { product: 'x', host: 'a', agent: 'other' } })[0], /agent/);
  assert.throws(() => codexPricing('custom'), /matching pricing/);
});

test('the real Codex stream preserves usage, tool output and the final answer', () => {
  const summary = summarizeCodex(codexJob, codexStream, 0, 1234, CODEX_PRICING);
  assert.equal(summary.eligibleForMetrics, true);
  assert.deepEqual(summary.isolation, { status: 'passed', violations: [] });
  assert.equal(summary.toolCalls.length, 1);
  assert.equal(summary.toolCalls[0].name, 'mcp__server_probe__confirm');
  assert.equal(summary.toolResponseChars, 'MCP_PROBE_OK'.length);
  assert.equal(summary.toolCalls[0].isError, false);
  assert.match(summary.answer, /NO_DIRECT_ACCESS/);
  assert.equal(summary.usage.costSource, 'calculated');
  assert.equal(summary.usage.costComplete, true);
  assert.equal(summary.durationMs, 1234);
  assert.ok(Math.abs(summary.usage.costUsd - (summary.usage.input * 0.1 + summary.usage.cacheRead * 0.01 + summary.usage.output * 0.5) / 1e6) < 1e-12);
  assert.deepEqual(summarizeCodex(codexJob, codexStream.replaceAll('\n', '\r\n'), 0, 1234, CODEX_PRICING), summary);
});

for (const type of ['command_execution', 'file_change', 'web_search', 'todo_list', 'dynamic_tool_call', 'new_tool_type']) {
  test(`a ${type} item makes Codex ineligible even when the turn succeeds`, () => {
    const event = JSON.stringify({ type: 'item.completed', item: { id: 'forbidden', type } });
    const summary = summarizeCodex(codexJob, event + '\n' + codexStream, 0, 10, CODEX_PRICING);
    assert.equal(summary.isolation.status, 'violated');
    assert.equal(summary.eligibleForMetrics, false);
    assert.equal(summary.usage.costComplete, true);
  });
}

test('foreign MCP servers, malformed streams, missing completion and bad usage cannot enter metrics', () => {
  const foreign = summarizeCodex(codexJob, codexStream.replaceAll('server_probe', 'server_other'), 0, 10, CODEX_PRICING);
  assert.equal(foreign.eligibleForMetrics, false);
  assert.match(foreign.isolation.violations[0], /foreign/);
  assert.equal(summarizeCodex(codexJob, codexStream + '{truncated', 0, 10, CODEX_PRICING).eligibleForMetrics, false);
  const unfinished = codexStream.split('\n').filter((line) => !line.includes('turn.completed')).join('\n');
  assert.match(summarizeCodex(codexJob, unfinished, 0, 10, CODEX_PRICING).error, /no completed/);
  const invalid = codexStream.replace(/"cached_input_tokens":\d+/, '"cached_input_tokens":999999999');
  assert.match(summarizeCodex(codexJob, invalid, 0, 10, CODEX_PRICING).error, /invalid Codex usage/);
  assert.equal(summarizeCodex(codexJob, codexStream, 1, 10, CODEX_PRICING).eligibleForMetrics, false);
  const unsupported = summarizeCodex(codexJob, codexStream, 0, 10, CODEX_PRICING, 'error=unsupported call: exec_command\n');
  assert.equal(unsupported.eligibleForMetrics, false);
  assert.match(unsupported.isolation.violations[0], /unsupported tool/);
});

test('cache writes are separated from ordinary input and billed at the documented rate', () => {
  const events = codexStream.split('\n').filter(Boolean).map(JSON.parse);
  const usage = events.find((event) => event.type === 'turn.completed').usage;
  Object.assign(usage, { input_tokens: 100, cached_input_tokens: 20, cache_write_input_tokens: 30, output_tokens: 10 });
  const summary = summarizeCodex(codexJob, events.map(JSON.stringify).join('\n'), 0, 1, CODEX_PRICING);
  assert.equal(summary.usage.input, 50);
  assert.equal(summary.usage.cacheCreation, 30);
  assert.ok(Math.abs(summary.usage.costUsd - (50 * 0.1 + 20 * 0.01 + 30 * 0.125 + 10 * 0.5) / 1e6) < 1e-12);
});

test('Codex summary.json round-trips all fields consumed by key metrics', () => {
  const out = scratch();
  try {
    const summary = saveCodexSummary(out, codexJob, codexStream, 0, 10, CODEX_PRICING);
    assert.deepEqual(JSON.parse(readFileSync(join(out, 'summary.json'), 'utf8')), summary);
    for (const field of ['input', 'cacheRead', 'cacheCreation', 'output', 'costUsd', 'totalContextTokens']) {
      assert.equal(typeof summary.usage[field], 'number');
    }
  } finally { rmSync(out, { recursive: true, force: true }); }
});

test('the first-session projection, completed spend and unknown usage stop a paid series', () => {
  const summary = summarizeCodex(codexJob, codexStream, 0, 10, CODEX_PRICING);
  assert.equal(budgetStop(summary, 0.5, 3, true, 4), null);
  assert.match(budgetStop({ ...summary, usage: { ...summary.usage, costUsd: 0.8 } }, 0.8, 3, true, 4), /budget/);
  assert.match(budgetStop(summary, 3, 3, false, 4), /budget/);
  assert.match(budgetStop({ ...summary, usage: { ...summary.usage, costComplete: false } }, 0, 3, false, 4), /incomplete/);
  assert.match(budgetStop({ ...summary, isolation: { status: 'violated' } }, 0, 3, true, 4), /isolation/);
});

test('Codex configuration exposes one server, disables native readers and keeps paths literal', () => {
  const model = { slug: CODEX_MODEL, tool_mode: 'code_mode_only', apply_patch_tool_type: 'freeform', shell_type: 'unified_exec' };
  const catalog = restrictedCatalog({ models: [model] }, CODEX_MODEL);
  assert.equal(model.shell_type, 'unified_exec');
  assert.equal(catalog.models[0].shell_type, 'disabled');
  assert.equal(catalog.models[0].apply_patch_tool_type, null);
  assert.equal(catalog.models[0].tool_mode, 'direct');
  assert.equal(catalog.models[0].supports_search_tool, false);
  assert.equal(catalog.models[0].node_repl_disabled, true);
  assert.throws(() => restrictedCatalog({ models: [] }, CODEX_MODEL), /does not list/);
  const config = codexConfiguration({ mcpServers: { server_probe: { command: '/app/cli', args: ['mcp'] } } },
    '/corpus/Путь с пробелами', '/private/models.json');
  assert.match(config, /"shell_tool" = false/);
  assert.match(config, /web_search = "disabled"/);
  assert.match(config, /project_doc_max_bytes = 0/);
  assert.match(config, /Путь с пробелами/);
  assert.deepEqual(codexArguments(CODEX_MODEL).slice(0, 3), ['exec', '--json', '--ephemeral']);
  assert.ok(codexArguments(CODEX_MODEL).includes('read-only'));
});

test('a fake Codex executable runs in an empty workspace and credentials are removed afterwards', async () => {
  const root = scratch();
  try {
    const source = join(root, 'source-home');
    const out = join(root, 'out');
    mkdirSync(source);
    writeFileSync(join(source, 'auth.json'), '{}');
    const script = join(root, 'fake-codex.mjs');
    writeFileSync(script, `import { readFileSync, writeFileSync } from 'node:fs';
      if (process.argv.includes('debug')) console.log(JSON.stringify({models:[{slug:${JSON.stringify(CODEX_MODEL)}}]}));
      else { writeFileSync(${JSON.stringify(join(root, 'launch.json'))},JSON.stringify({cwd:process.cwd(),argv:process.argv,config:readFileSync(process.env.CODEX_HOME+'/config.toml','utf8')}));
      process.stdout.write(${JSON.stringify(codexStream)}); }`);
    const summary = await executeCodex({ job: codexJob, executable: script, out,
      mcp: { mcpServers: { server_probe: { command: 'unused', args: [] } } }, repoPath: join(root, 'corpus'),
      prompt: 'same prompt', timeout: 30000, maxCalls: 4, pricing: CODEX_PRICING,
      environment: { ...process.env, CODEX_HOME: source } });
    const launch = JSON.parse(readFileSync(join(root, 'launch.json'), 'utf8'));
    assert.equal(launch.cwd, join(out, 'workspace'));
    assert.deepEqual(readdirSync(launch.cwd), []);
    assert.match(launch.config, /server_probe/);
    assert.equal(summary.eligibleForMetrics, true);
    assert.equal(existsSync(join(out, 'codex-home')), false);
    assert.equal(readFileSync(join(source, 'auth.json'), 'utf8'), '{}');
    assert.equal(readFileSync(join(out, 'stream.jsonl'), 'utf8'), codexStream);
    const limitedOut = join(root, 'limited');
    const limited = await executeCodex({ job: codexJob, executable: script, out: limitedOut,
      mcp: { mcpServers: { server_probe: { command: 'unused', args: [] } } }, repoPath: join(root, 'corpus'),
      prompt: 'same prompt', timeout: 30000, maxCalls: 0, pricing: CODEX_PRICING,
      environment: { ...process.env, CODEX_HOME: source } });
    assert.equal(limited.eligibleForMetrics, false);
    assert.match(limited.error, /call limit exceeded/);
    assert.equal(existsSync(join(limitedOut, 'codex-home')), false);
  } finally { rmSync(root, { recursive: true, force: true }); }
});


test('explicit task key versions survive both summary formats without changing legacy summaries', () => {
  const stream = readFileSync(join(HERE, 'fixtures', 'stream.jsonl'), 'utf8');
  assert.equal(summarize({ ...job, keyVersion: 2 }, stream, 0, 5000).keyVersion, 2);
  assert.equal(summarizeCodex({ ...codexJob, keyVersion: 2 }, codexStream, 0, 10, CODEX_PRICING).keyVersion, 2);
  assert.equal(summarize({ ...job, task: { ...job.task, keyVersion: 2 } }, stream, 0, 5000).keyVersion, 2);
  assert.equal(summarize(job, stream, 0, 5000).keyVersion, undefined);
  assert.equal(summarizeCodex(codexJob, codexStream, 0, 10, CODEX_PRICING).keyVersion, undefined);
});
