import test from 'node:test';
import assert from 'node:assert/strict';
import { fileURLToPath } from 'node:url';
import { evaluateTaskAnswer, loadTaskOracleRegistry } from '../lib/task-oracle.mjs';
import { formatPipelineTable } from '../lib/pipeline-report.mjs';

const task = loadTaskOracleRegistry(
  fileURLToPath(new URL('../oracles/tasks.json', import.meta.url))).get('N2');
const evidence = [...task.requiredPaths, ...task.requiredSymbols.flatMap(symbol => symbol.terms),
  ...task.requiredClaims.flatMap(claim => claim.terms.map(group => group[0]))].join('\n');

for (const [name, answer, detected] of [
  ['direct contradiction', '65536 is rejected.', true],
  ['unrelated preceding negation', 'The request does not reach the network and 65536 is rejected.', true],
  ['explicit denial', 'The claim that 65536 is rejected is false.', false],
  ['unlisted paraphrase', 'A URL of length 65536 fails validation.', false],
  ['quoted proposition', 'The example says "65536 is rejected".', false],
  ['multi-sentence quotation', 'The example says "65536 is rejected. The request fails."', false],
  ['typographic quotation', 'The example says “65536 is rejected”.', false],
  ['fenced quotation', '```text\n65536 is rejected.\n```', false],
  ['block quotation', '> 65536 is rejected.', false],
  ['direct refutation', 'We refute the claim that 65536 is rejected.', false],
  ['Russian refutation', 'Неверно, что 65536 is rejected.', false],
  ['unrelated following denial', '65536 is rejected and the network claim is false.', true],
  ['denial followed by assertion', 'The claim that 65536 is rejected is false and 65536 is rejected.', true],
  ['quotation followed by assertion', 'The example says "65536 is rejected" and 65536 is rejected.', true],
  ['assertion followed by denial', '65536 is rejected and the claim that 65536 is rejected is false.', true],
  ['independent boundary assertions', 'The claim that 65536 is rejected is false and 65537 is accepted.', true],
  ['endorsed quotation', '"65536 is rejected" is true.', true],
  ['not only qualifier', 'Not only is the network blocked, 65536 is rejected.', true],
]) {
  test(`oracle scopes ${name} to the matched proposition`, () => {
    const result = evaluateTaskAnswer(task, `${evidence}\n${answer}`);
    assert.equal(result.contradictedClaims.length > 0, detected, answer);
  });
}

test('oracle states evidence coverage separately from semantic correctness', () => {
  const found = evaluateTaskAnswer(task, evidence);
  const contradicted = evaluateTaskAnswer(task, `${evidence}\n65536 is rejected.`);
  const missing = evaluateTaskAnswer(task, 'An answer without the required evidence.');
  const empty = evaluateTaskAnswer(task, '');
  for (const result of [found, contradicted, missing, empty]) {
    assert.equal(result.semanticCorrectness, 'requires-separate-check');
    assert.ok(result.outcomeStatements.includes('содержательная правильность требует отдельной проверки'));
  }
  assert.equal(found.requiredEvidence, 'found');
  assert.equal(contradicted.requiredEvidence, 'found');
  assert.equal(missing.requiredEvidence, 'missing');
  assert.equal(empty.requiredEvidence, 'missing');
  assert.equal(contradicted.knownContradictions, 'detected');
  assert.equal(found.knownContradictions, 'not-detected');
  assert.ok(found.outcomeStatements.includes('обязательные основания найдены'));
  assert.ok(contradicted.outcomeStatements.includes('известное противоречие обнаружено'));
  assert.ok(found.outcomeStatements.includes('ни одно из заданных запрещённых утверждений не обнаружено'));
});

test('oracle report prints the finite contradiction check and separate correctness requirement', () => {
  const result = evaluateTaskAnswer(task, evidence);
  const report = {
    table: [{ arm: 'baseline', modelTurns: 0, toolCalls: 0,
      usage: { inputTokens: 0, cacheWriteTokens: 0, cacheReadTokens: 0, outputTokens: 0 },
      cost: { amount: 0, currency: 'USD' }, wallDurationMs: 0,
      oracle: [{ task: task.id, repetition: 1, ...result }], judging: [] }],
    orderDisagreement: {
      correctness: { disagreements: 0, assessedPairs: 0, disagreementRate: null },
      preference: { disagreements: 0, assessedPairs: 0, disagreementRate: null },
    },
  };
  const text = formatPipelineTable(report);
  assert.ok(text.includes('обязательные основания найдены'));
  assert.ok(text.includes('ни одно из заданных запрещённых утверждений не обнаружено'));
  assert.ok(text.includes('содержательная правильность требует отдельной проверки'));
  assert.ok(!text.includes('ответ верен'));
});
