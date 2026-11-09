// Runner pieces without the claude CLI: summaries, arm rules, MCP configs and corpus checks.
import assert from 'node:assert/strict';
import { mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';
import {
  DEFAULT_MODEL, armFingerprint, buildMcpConfig, countWorktreeFiles, dataRoot, fingerprintMismatch,
  isTransientFailure, parseArgs, quoteForCmd, renderPrompt, resolveArm, summarize, usedArms, validateArms,
} from '../session.mjs';

const HERE = dirname(fileURLToPath(import.meta.url));
const TOOL_DIR = join(HERE, '..');
const job = { id: 'T1-server_b-r1', task: { id: 'T1', repo: 'httpx' }, server: 'server_b', product: 'v5.2.2', repeat: 1 };

function scratch() {
  return mkdtempSync(join(tmpdir(), 'agent-benchmark-'));
}

test('the default model is Haiku 4.5', () => {
  assert.equal(DEFAULT_MODEL, 'claude-haiku-4-5-20251001');
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
