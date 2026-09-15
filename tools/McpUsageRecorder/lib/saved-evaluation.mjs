import { createHash } from 'node:crypto';
import { readFile } from 'node:fs/promises';
import { reconcileOrderedAssessments } from './order-consistency.mjs';
import { compareTaskAnswers, evaluateTaskAnswer } from './task-oracle.mjs';

export async function loadSavedAssessments(path) {
  const source = JSON.parse(await readFile(path, 'utf8'));
  if (source.schemaVersion !== 1 || !Array.isArray(source.pairs))
    throw new Error('Saved assessments must use schema version 1 and contain a pairs array.');
  const keys = new Set();
  for (const pair of source.pairs) {
    const key = assessmentKey(pair.task, pair.repetition, pair.candidates);
    if (keys.has(key))
      throw new Error(`Saved assessment '${key}' appears more than once.`);
    keys.add(key);
    validateOrderedResponse(pair.forward, pair.candidates);
    validateOrderedResponse(pair.reverse, [...pair.candidates].reverse());
  }
  return source;
}

export function evaluateSavedSeries(definition, records, taskRegistry, assessments) {
  const successful = selectSuccessfulRecords(records);
  const assessmentByKey = new Map((assessments?.pairs ?? []).map(pair => [
    assessmentKey(pair.task, pair.repetition, pair.candidates),
    pair,
  ]));
  const armPairs = comparisonPairs(definition);
  const rows = [];
  const usedAssessments = new Set();
  for (const taskDefinition of definition.tasks) {
    const task = taskRegistry.get(taskDefinition.oracle ?? taskDefinition.id);
    if (!task)
      throw new Error(`No task oracle is registered for '${taskDefinition.id}'.`);
    for (let repetition = 1; repetition <= definition.repetitions; repetition++) {
      for (const candidates of armPairs) {
        const left = requireSuccessful(successful, taskDefinition.id, repetition, candidates[0]);
        const right = requireSuccessful(successful, taskDefinition.id, repetition, candidates[1]);
        const answers = {
          [candidates[0]]: stripExperience(left.measurement.finalAnswer ?? ''),
          [candidates[1]]: stripExperience(right.measurement.finalAnswer ?? ''),
        };
        const oracle = {
          [candidates[0]]: evaluateTaskAnswer(task, answers[candidates[0]]),
          [candidates[1]]: evaluateTaskAnswer(task, answers[candidates[1]]),
        };
        const oracleComparison = compareTaskAnswers(oracle[candidates[0]], oracle[candidates[1]]);
        let ordered = null;
        if (requiresOrderedAssessment(task, oracleComparison, Object.values(oracle))) {
          const key = assessmentKey(taskDefinition.id, repetition, candidates);
          const saved = assessmentByKey.get(key);
          if (!saved)
            throw new Error(`Saved assessment '${key}' is required because the deterministic oracle tied.`);
          verifyAnswerFingerprints(saved, answers);
          usedAssessments.add(key);
          ordered = reconcileOrderedAssessments(saved.forward.response, saved.reverse.response);
        }
        rows.push({
          task: taskDefinition.id,
          repetition,
          candidates,
          answers: Object.fromEntries(candidates.map(arm => [arm, oracle[arm]])),
          oracleComparison,
          ordered,
        });
      }
    }
  }
  const unused = [...assessmentByKey.keys()].filter(key => !usedAssessments.has(key));
  if (unused.length > 0)
    throw new Error(`Saved assessment '${unused[0]}' does not belong to an unresolved series pair.`);
  return {
    rows,
    orderConsistency: summarizeDisagreements(rows),
    assessmentsSha256: assessments ? digest(canonicalJson(assessments)) : null,
  };
}

export function requiresOrderedAssessment(task, comparison, answers) {
  return comparison.choice === 'tie' ||
    (task.oracleCoverage === 'partial' && answers.every(answer => answer.supported));
}

export function stripExperience(answer) {
  if (typeof answer !== 'string')
    return '';
  const match = /^## Experience\s*$/im.exec(answer);
  return (match ? answer.slice(0, match.index) : answer).trim();
}

export function answerFingerprint(answer) {
  return digest(stripExperience(answer));
}

function selectSuccessfulRecords(records) {
  const map = new Map();
  for (const record of records) {
    if (record.measurement.outcome !== 'success')
      continue;
    const key = runKey(record.identity.task, record.identity.repetition, record.identity.arm);
    if (map.has(key))
      throw new Error(`More than one successful session exists for '${key}'.`);
    map.set(key, record);
  }
  return map;
}

function requireSuccessful(records, task, repetition, arm) {
  const key = runKey(task, repetition, arm);
  const record = records.get(key);
  if (!record)
    throw new Error(`A successful immutable record is required for '${key}'.`);
  return record;
}

function comparisonPairs(definition) {
  if (Array.isArray(definition.evaluation?.pairs) && definition.evaluation.pairs.length > 0)
    return definition.evaluation.pairs.map(pair => validateCandidates(pair));
  if (definition.arms.length !== 2)
    throw new Error('evaluation.pairs is required when a series has more than two arms.');
  return [[definition.arms[0].id, definition.arms[1].id]];
}

function validateCandidates(candidates) {
  if (!Array.isArray(candidates) || candidates.length !== 2 ||
      candidates.some(value => typeof value !== 'string' || value.length === 0) ||
      candidates[0] === candidates[1])
    throw new Error('Every evaluation pair requires two distinct arm identifiers.');
  return candidates;
}

function validateOrderedResponse(value, expectedOrder) {
  if (!value || typeof value !== 'object')
    throw new Error('Every saved assessment order requires a response.');
  if (JSON.stringify(value.order) !== JSON.stringify(expectedOrder))
    throw new Error('Saved assessment orders must follow the declared candidate order and its reverse.');
  if (!Array.isArray(value.answerSha256) || value.answerSha256.length !== 2 ||
      value.answerSha256.some(fingerprint => !/^[0-9a-f]{64}$/.test(fingerprint)))
    throw new Error('Every saved assessment order requires two answer fingerprints.');
  if (!value.response || JSON.stringify(value.response.order) !== JSON.stringify(value.order))
    throw new Error('Saved assessment response order must match its presented order.');
  for (const dimension of ['correctness', 'preference'])
    if (!['A', 'B', 'tie'].includes(value.response[dimension]))
      throw new Error(`Saved assessment ${dimension} must be A, B, or tie.`);
}

function verifyAnswerFingerprints(saved, answers) {
  for (const ordered of [saved.forward, saved.reverse]) {
    const expected = ordered.order.map(arm => digest(answers[arm]));
    if (JSON.stringify(expected) !== JSON.stringify(ordered.answerSha256))
      throw new Error('Saved assessment answer fingerprint does not match the immutable session answer.');
  }
}

function summarizeDisagreements(rows) {
  const summary = {};
  for (const dimension of ['correctness', 'preference']) {
    const assessed = rows.filter(row => row.ordered !== null);
    const disagreements = assessed.filter(row => row.ordered[dimension].status === 'disagreement').length;
    summary[dimension] = {
      assessedPairs: assessed.length,
      disagreements,
      disagreementRate: assessed.length === 0 ? null : disagreements / assessed.length,
    };
  }
  return summary;
}

function assessmentKey(task, repetition, candidates) {
  validateCandidates(candidates);
  return `${task}/${repetition}/${[...candidates].sort().join('+')}`;
}

function runKey(task, repetition, arm) {
  return `${task}/${repetition}/${arm}`;
}

function digest(value) {
  return createHash('sha256').update(value).digest('hex');
}

function canonicalJson(value) {
  return JSON.stringify(canonicalize(value));
}

function canonicalize(value) {
  if (Array.isArray(value))
    return value.map(canonicalize);
  if (!value || typeof value !== 'object')
    return value;
  return Object.fromEntries(Object.keys(value).sort().map(key => [key, canonicalize(value[key])]));
}
