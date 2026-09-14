import assert from 'node:assert/strict';
import test from 'node:test';
import {
  classifyExpectedRelations,
  compareRelated,
  normalizeCliRelated,
  parseMcpRelated,
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
