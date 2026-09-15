import test from 'node:test';
import assert from 'node:assert/strict';
import { evaluateTaskAnswer, loadTaskOracleRegistry } from '../lib/task-oracle.mjs';
import { fileURLToPath } from 'node:url';

const task = {
  id: 'boundary', oracleCoverage: 'partial', pathExtensions: ['py'],
  requiredPaths: ['src/url.py', 'tests/url.py'], requiredSymbols: [],
  requiredClaims: [{ id: 'boundary', terms: [['65536 is accepted']] }],
  forbiddenClaims: [{ id: 'reversed-boundary', match: 'affirmative-phrase',
    terms: [['65536 is rejected']], evidence: [{ commit: 'a'.repeat(40), path: 'src/url.py', lines: '1-3' }] }],
};

test('a contradictory assertion overrides otherwise correct paths', () => {
  const answer = 'src/url.py tests/url.py. 65536 is accepted. 65536 is rejected.';
  assert.equal(evaluateTaskAnswer(task, answer).classification, 'incorrect');
});

test('an omitted assertion remains incomplete rather than incorrect', () => {
  assert.equal(evaluateTaskAnswer(task, 'src/url.py').classification, 'incomplete');
});

test('contradiction normalization preserves the existing required term spelling', () => {
  const exactTask = { ...task, requiredSymbols: [{ id: 'method', terms: ['Exact_Name'] }] };
  const prefix = 'src/url.py tests/url.py. ';
  assert.equal(evaluateTaskAnswer(exactTask, `${prefix}65536 is accepted. ExactName.`).classification, 'incomplete');
  assert.equal(evaluateTaskAnswer(exactTask, `${prefix}65,536 is accepted. Exact_Name.`).classification, 'incomplete');
  assert.equal(evaluateTaskAnswer(exactTask, `${prefix}65536 is accepted. Exact_Name.`).classification, 'complete');
  assert.equal(evaluateTaskAnswer(exactTask, `${prefix}65536 is accepted. Exact_Name. 65,536 is rejected.`).classification, 'incorrect');
});

test('negation and rejection of a contradictory assertion do not assert it', () => {
  for (const denial of [
    'It is not true that 65536 is rejected.',
    'The assertion "65536 is rejected" is false.',
    '65536 is not rejected.',
    'Неверно, что 65536 is rejected.',
  ]) {
    const result = evaluateTaskAnswer(task, `src/url.py tests/url.py. 65536 is accepted. ${denial}`);
    assert.equal(result.classification, 'complete', denial);
    assert.deepEqual(result.contradictedClaims, [], denial);
  }
});

test('all real tasks pin source evidence and explicit contradictory assertions', () => {
  const registry = loadTaskOracleRegistry(fileURLToPath(new URL('../oracles/tasks.json', import.meta.url)));
  assert.equal(registry.size, 12);
  for (const task of registry.values()) {
    assert.ok(task.forbiddenClaims?.length > 0, task.id);
    for (const claim of task.forbiddenClaims) {
      assert.equal(claim.match, 'affirmative-phrase', task.id);
      assert.ok(claim.evidence?.every(item => /^[0-9a-f]{40}$/.test(item.commit) && item.path && item.lines), task.id);
      assert.equal(evaluateTaskAnswer(task, claim.terms[0][0]).classification, 'incorrect', task.id);
      assert.notEqual(evaluateTaskAnswer(task, `It is not true that ${claim.terms[0][0]}.`).classification, 'incorrect', task.id);
    }
  }
});

test('the real URL task rejects a reversed boundary despite all required evidence', () => {
  const registry = loadTaskOracleRegistry(fileURLToPath(new URL('../oracles/tasks.json', import.meta.url)));
  const task = registry.get('N2');
  const complete = [...task.requiredPaths, ...task.requiredSymbols.flatMap(symbol => symbol.terms),
    ...task.requiredClaims.flatMap(claim => claim.terms.map(group => group[0]))].join('\n');
  assert.equal(evaluateTaskAnswer(task, complete).classification, 'complete');
  assert.equal(evaluateTaskAnswer(task, `${complete}\n65,536 is rejected.`).classification, 'incorrect');
  assert.equal(evaluateTaskAnswer(task, task.requiredPaths.join('\n')).classification, 'incomplete');
  assert.equal(evaluateTaskAnswer(task, `${complete}\nIt is not true that 65,536 is rejected.`).classification, 'complete');
});
