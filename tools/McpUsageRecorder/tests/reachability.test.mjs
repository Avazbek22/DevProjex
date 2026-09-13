import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdtemp, mkdir, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';
import {
  ReachabilityClass,
  analyzeReachability,
  containsPath,
  extractTreePaths,
  extractVisibleRepositoryPaths,
  parseRelatedPaths,
  parseSearchBoundary,
} from '../lib/reachability-analysis.mjs';

test('search boundary keeps inspected retained written and limit counts separate', () => {
  const parsed = parseSearchBoundary(
    '[Search boundary] partial · sources inspected=4/9 · matches retained=5/8 · ' +
    'matches written=2 · declaration files named=1 · limits=inspection-bytes,retained-matches; continue.');
  assert.deepEqual(parsed, {
    complete: false,
    inspectedSources: 4,
    eligibleSources: 9,
    retainedMatches: 5,
    encounteredMatches: 8,
    writtenMatches: 2,
    namedDeclarationFiles: 1,
    limits: ['inspection-bytes', 'retained-matches'],
  });
});

test('related parser admits resolved files and retains unresolved evidence separately', () => {
  const parsed = parseRelatedPaths([
    'Dependencies:',
    'src/Resolved.cs — type reference — resolved — 20 tokens',
    'Missing.Type — type reference — unresolved — 0 tokens',
  ].join('\n'));
  assert.deepEqual(parsed.resolved, ['src/Resolved.cs']);
  assert.deepEqual(parsed.unresolved, [
    { path: 'Missing.Type', status: 'unresolved', reason: 'type reference' },
  ]);
});

test('path detection requires a complete path line', () => {
  assert.equal(containsPath('src/core.cs\n12:needle', 'src/core.cs'), true);
  assert.equal(containsPath('12:load src/core.cs now', 'src/core.cs'), false);
});

test('search positions count file headings rather than matching lines', () => {
  const files = ['src/First.cs', 'src/Second.cs'];
  const text = 'src/First.cs\n9:needle\n10:needle\nsrc/Second.cs\n3:needle';
  assert.deepEqual(extractVisibleRepositoryPaths(text, files), files);
});

test('JSON tree paths are reconstructed from nested directories', () => {
  const text = 'prefix\n<untrusted-data-abc>\n' +
    '{"rootPath":"/repo","tree":{"src":{"nested":{"/":["Needle.cs"]},"/":["Root.cs"]},"/":["README.md"]}}' +
    '\n</untrusted-data-abc>\nsuffix';
  assert.deepEqual(extractTreePaths(text), ['README.md', 'src/Root.cs', 'src/nested/Needle.cs']);
});

test('analysis distinguishes direct chain knowledge-only and unreachable files', async () => {
  const root = await mkdtemp(join(tmpdir(), 'reachability-test-'));
  try {
    await mkdir(join(root, 'src'), { recursive: true });
    await writeFile(join(root, 'src', 'Direct.cs'), 'Needle direct');
    await writeFile(join(root, 'src', 'Linked.cs'), 'No task term');
    await writeFile(join(root, 'src', 'Known.cs'), 'No task term');
    const registry = fixtureRegistry();
    const oracles = {
      tasks: [{
        id: 'T1',
        requiredPaths: ['src/Direct.cs', 'src/Linked.cs', 'src/Known.cs', 'src/Blocked.cs'],
      }],
    };
    const calls = [];
    const client = fakeClient(calls, {
      search_project: result('src/Direct.cs\n1:Needle direct\n' + completeBoundary(1, 1)),
      get_tree: result(''),
      related_files: result('Dependencies:\nsrc/Linked.cs — type reference — resolved — 1 tokens'),
      get_file: argumentsValue => argumentsValue.path === 'src/Blocked.cs'
        ? result('not found', true)
        : result('1:text'),
    });
    const analysis = await analyzeReachability(
      registry,
      oracles,
      new Map([['repo', root]]),
      async () => client);
    assert.deepEqual(analysis.tasks[0].requiredFiles.map(file => file.classification), [
      ReachabilityClass.OneCall,
      ReachabilityClass.ShortChain,
      ReachabilityClass.RequiresAnswerKnowledge,
      ReachabilityClass.Unreachable,
    ]);
    assert.equal(analysis.tasks[0].requiredFiles[1].relatedHop, 1);
    assert.equal(calls.filter(call => call.name === 'related_files').length, 2);
    assert.equal(client.closed, true);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test('matching source hidden by a partial search names the active limit', async () => {
  const root = await mkdtemp(join(tmpdir(), 'reachability-limit-'));
  try {
    await mkdir(join(root, 'src'), { recursive: true });
    await writeFile(join(root, 'src', 'Hidden.cs'), 'Needle hidden');
    const registry = fixtureRegistry({ seeds: [] });
    const oracles = { tasks: [{ id: 'T1', requiredPaths: ['src/Hidden.cs'] }] };
    const client = fakeClient([], {
      search_project: result(
        '[Search boundary] partial · sources inspected=1/2 · matches retained=1/2 · ' +
        'matches written=1 · declaration files named=0 · limits=inspection-bytes,max-results; continue.'),
      get_tree: result(''),
      get_file: result('1:text'),
    });
    const analysis = await analyzeReachability(registry, oracles, new Map([['repo', root]]), async () => client);
    assert.equal(analysis.tasks[0].requiredFiles[0].reason,
      'matching task term was observed only beyond search limits: inspection-bytes, max-results');
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test('real oracle currently contains twelve tasks and reports every declared occurrence', async () => {
  const registry = JSON.parse(await import('node:fs/promises').then(fs =>
    fs.readFile(new URL('../oracles/reachability.json', import.meta.url), 'utf8')));
  const oracles = JSON.parse(await import('node:fs/promises').then(fs =>
    fs.readFile(new URL('../oracles/tasks.json', import.meta.url), 'utf8')));
  assert.equal(registry.tasks.length, 12);
  assert.equal(oracles.tasks.reduce((total, task) => total + task.requiredPaths.length, 0), 65);
});

test('a supplied executable requires an explicit product source identity', () => {
  const script = new URL('../reachability.mjs', import.meta.url);
  const result = spawnSync(process.execPath, [fileURLToPath(script), '--server', 'unused'], {
    cwd: new URL('../../..', import.meta.url),
    encoding: 'utf8',
  });
  assert.equal(result.status, 1);
  assert.match(result.stderr, /--product-sha is required with --server/);
});

function fixtureRegistry(overrides = {}) {
  return {
    schemaVersion: 1,
    repositories: [{ id: 'repo', url: 'https://example.invalid/repo', commit: '0'.repeat(40) }],
    tasks: [{
      id: 'T1',
      repository: 'repo',
      taskSummary: 'Find Needle and its related implementation.',
      queries: [{ pattern: 'Needle', rationale: 'Needle occurs in the task.' }],
      treePatterns: [],
      seeds: [{
        path: 'src/Direct.cs',
        discoveredBy: { surface: 'search_project', input: 'Needle' },
      }],
      maximumRelatedHops: 2,
      ...overrides,
    }],
  };
}

function fakeClient(calls, handlers) {
  return {
    closed: false,
    async call(name, argumentsValue) {
      calls.push({ name, arguments: argumentsValue });
      const handler = handlers[name];
      if (!handler)
        return result('');
      return typeof handler === 'function' ? handler(argumentsValue) : handler;
    },
    async callAndPage(name, argumentsValue) {
      const value = await this.call(name, argumentsValue);
      return { ...value, pages: [], allText: value.text };
    },
    async close() { this.closed = true; },
  };
}

function result(text, isError = false) {
  return { text, allText: text, pages: [], isError };
}

function completeBoundary(matches, files) {
  return `[Search boundary] complete · sources inspected=${files}/${files} · ` +
    `matches retained=${matches}/${matches} · matches written=${matches} · declaration files named=${files}.`;
}
