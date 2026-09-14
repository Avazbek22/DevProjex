import assert from 'node:assert/strict';
import test from 'node:test';
import {
  classifyExpectedRelations,
  compareRelated,
  mergeRelations,
  normalizeCliRelated,
  parseMcpRelated,
  resolveIncludeCandidates,
} from '../lib/live-validation.mjs';

test('CLI and MCP related results normalize to the same evidence', () => {
  const cli = normalizeCliRelated({
    kind: 'devprojex-related-files',
    seeds: [{
      seed: 'src/a.c',
      dependencies: [{ path: 'include/a.h', status: 'resolved', reasons: ['one repository C header'], candidates: [] }],
      dependents: [{ path: 'test/a.c', status: 'resolved', reasons: ['one visible declaration identity'], candidates: [] }],
    }],
  })[0];
  const mcp = parseMcpRelated(`before\n<untrusted-data>\nDependencies:\ninclude/a.h — one repository C header — resolved — 3 tokens\nDependents:\ntest/a.c — one visible declaration identity — resolved — 8 tokens\n</untrusted-data>`);
  mcp.seed = 'src/a.c';

  assert.equal(compareRelated(cli, mcp).equal, true);
});

test('expected relations distinguish confirmed explicit and silent omissions', () => {
  const result = classifyExpectedRelations({
    expectedRelations: [
      { direction: 'dependencies', path: 'include/a.h' },
      { direction: 'dependencies', path: 'include/macro.h', reference: 'MACRO_TYPE' },
      { direction: 'dependents', path: 'src/caller.c' },
    ],
    evidence: [{ status: 'unresolved', reference: 'MACRO_TYPE' }],
  }, {
    dependencies: [{ path: 'include/a.h', status: 'resolved' }],
    dependents: [],
  });

  assert.equal(result.confirmed, 1);
  assert.equal(result.missedHonestly, 1);
  assert.equal(result.missedSilently, 1);
  assert.deepEqual(result.falseEdges, []);
});

test('a resolved edge absent from the checked source relation list is rejected', () => {
  const result = classifyExpectedRelations({ expectedRelations: [], evidence: [] }, {
    dependencies: [{ path: 'include/unrelated.h', status: 'resolved' }],
    dependents: [],
  });

  assert.deepEqual(result.falseEdges, ['dependencies:include/unrelated.h']);
});

test('engine classification preserves evidence from a related source file', () => {
  const result = classifyExpectedRelations({
    expectedRelations: [{
      direction: 'dependents',
      path: 'src/caller.c',
      reference: 'api.h',
      engineState: 'missed-honestly',
    }],
  }, { dependencies: [], dependents: [] });

  assert.equal(result.missedHonestly, 1);
  assert.equal(result.missedSilently, 0);
});

test('quoted includes prefer the exact relative file while system includes require a unique suffix', () => {
  const paths = new Set(['src/local.h', 'include/local.h', 'include/library/api.h']);

  assert.deepEqual(resolveIncludeCandidates('src/main.c', 'local.h', true, paths), ['src/local.h']);
  assert.deepEqual(resolveIncludeCandidates('src/main.c', 'library/api.h', false, paths), ['include/library/api.h']);
  assert.deepEqual(resolveIncludeCandidates('src/main.c', 'local.h', false, paths), ['include/local.h', 'src/local.h']);
});

test('declared relation evidence takes precedence over discovered include evidence', () => {
  const result = mergeRelations(
    [{ direction: 'dependencies', path: 'include/api.h', reference: 'ApiType', evidence: 'checked type use' }],
    [{ direction: 'dependencies', path: 'include/api.h', reference: 'api.h', evidence: '#include "api.h"' }]);

  assert.deepEqual(result, [
    { direction: 'dependencies', path: 'include/api.h', reference: 'ApiType', evidence: 'checked type use' },
  ]);
});
