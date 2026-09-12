import { readFileSync } from 'node:fs';

const pathPattern = /(?:^|[\s`"'(\[])(((?:[A-Za-z0-9_.@+\-]+\/)*[A-Za-z0-9_.@+\-]+\.(?:cs|csproj|py|ts|md|json))(?::\d+(?:-\d+)?)?)/gim;

export function loadTaskOracleRegistry(path) {
  const source = JSON.parse(readFileSync(path, 'utf8'));
  if (source.schemaVersion !== 1 || !Array.isArray(source.tasks))
    throw new Error('Task oracle registry must use schema version 1 and contain a tasks array.');
  return new Map(source.tasks.map(task => [task.id, validateTask(task)]));
}

export function evaluateTaskAnswer(task, answer) {
  validateTask(task);
  const text = typeof answer === 'string' ? answer.trim() : '';
  if (text.length === 0) {
    return result(task, 'empty', [], task.requiredPaths, task.requiredClaims.map(claim => claim.id),
      (task.requiredSymbols ?? []).map(symbol => symbol.id), []);
  }

  const required = new Set(task.requiredPaths.map(normalizePath));
  const recognizedRootPaths = new Set([
    ...task.requiredPaths,
    ...(task.optionalPaths ?? []),
    ...(task.forbiddenPaths ?? []),
  ].map(normalizePath));
  const namedPaths = extractNamedPaths(text).filter(path => path.includes('/') || recognizedRootPaths.has(path));
  const missingPaths = [...required].filter(path => !namedPaths.includes(path));
  const forbiddenPaths = new Set((task.forbiddenPaths ?? []).map(normalizePath));
  const unexpectedPaths = namedPaths.filter(path => forbiddenPaths.has(path));
  const missingClaims = task.requiredClaims
    .filter(claim => !claim.terms.every(group => group.some(term => includesText(text, term))))
    .map(claim => claim.id);
  const missingSymbols = (task.requiredSymbols ?? [])
    .filter(symbol => !symbol.terms.some(term => includesText(text, term)))
    .map(symbol => symbol.id);
  const contradictedClaims = (task.forbiddenClaims ?? [])
    .filter(claim => claim.terms.every(group => group.some(term => includesText(text, term))))
    .map(claim => claim.id);
  const classification = unexpectedPaths.length > 0 || contradictedClaims.length > 0
    ? 'incorrect'
    : missingPaths.length > 0 || missingClaims.length > 0 || missingSymbols.length > 0
      ? 'incomplete'
      : 'complete';
  return result(
    task,
    classification,
    namedPaths,
    missingPaths,
    missingClaims,
    missingSymbols,
    unexpectedPaths,
    contradictedClaims);
}

export function splitComparedAnswers(text) {
  if (typeof text !== 'string')
    return { A: '', B: '' };
  const first = /^## Answer A\s*$/im.exec(text);
  const second = /^## Answer B\s*$/im.exec(text);
  const end = /^## (?:Verdict|Experience)\s*$/im.exec(text);
  if (!first || !second || second.index <= first.index)
    return { A: text.trim(), B: '' };
  return {
    A: text.slice(first.index + first[0].length, second.index).trim(),
    B: text.slice(second.index + second[0].length, end?.index ?? text.length).trim(),
  };
}

export function extractNamedPaths(text) {
  const paths = new Set();
  for (const match of text.matchAll(pathPattern))
    paths.add(normalizePath(match[1]));
  return [...paths].sort((left, right) => left.localeCompare(right, 'en'));
}

function validateTask(task) {
  if (!task || typeof task.id !== 'string' || task.id.length === 0)
    throw new Error('Every task oracle requires an id.');
  if (!Array.isArray(task.requiredPaths) || !Array.isArray(task.requiredClaims))
    throw new Error(`Task oracle '${task.id}' requires path and claim arrays.`);
  if (task.requiredSymbols !== undefined && !Array.isArray(task.requiredSymbols))
    throw new Error(`Task oracle '${task.id}' has an invalid symbol array.`);
  for (const claim of [...task.requiredClaims, ...(task.forbiddenClaims ?? [])]) {
    if (typeof claim.id !== 'string' || !Array.isArray(claim.terms) ||
        claim.terms.some(group => !Array.isArray(group) || group.length === 0))
      throw new Error(`Task oracle '${task.id}' has an invalid claim.`);
  }
  for (const symbol of task.requiredSymbols ?? []) {
    if (typeof symbol.id !== 'string' || !Array.isArray(symbol.terms) || symbol.terms.length === 0)
      throw new Error(`Task oracle '${task.id}' has an invalid symbol.`);
  }
  return task;
}

function result(task, classification, namedPaths, missingPaths, missingClaims, missingSymbols,
  unexpectedPaths, contradictedClaims = []) {
  return {
    taskId: task.id,
    oracleCoverage: task.oracleCoverage,
    classification,
    complete: classification === 'complete',
    supported: classification === 'complete' || classification === 'incomplete',
    namedPaths,
    missingPaths,
    missingClaims,
    missingSymbols,
    unexpectedPaths,
    contradictedClaims,
    manuallyCheckedCriteria: task.manuallyCheckedCriteria ?? [],
  };
}

function normalizePath(path) {
  return path.replaceAll('\\', '/').replace(/:\d+(?:-\d+)?$/, '');
}

function includesText(text, term) {
  return text.toLocaleLowerCase('en-US').includes(term.toLocaleLowerCase('en-US'));
}
