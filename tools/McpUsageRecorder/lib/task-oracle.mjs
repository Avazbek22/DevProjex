import { readFileSync } from 'node:fs';

export function loadTaskOracleRegistry(path) {
  const source = JSON.parse(readFileSync(path, 'utf8'));
  if (source.schemaVersion !== 2 || !Array.isArray(source.tasks))
    throw new Error('Task oracle registry must use schema version 2 and contain a tasks array.');
  if (source.criteriaDefinition !== undefined &&
      (!/^\d{4}-\d{2}-\d{2}$/.test(source.criteriaDefinition.date ?? '') ||
       !/^[0-9a-f]{40}$/.test(source.criteriaDefinition.productBuildSha ?? '')))
    throw new Error('Task oracle criteriaDefinition requires a date and a full lowercase product SHA.');
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
  const namedPaths = extractDeclaredPaths(text, task);
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
    .filter(claim => isForbiddenClaimAsserted(text, claim))
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

export function compareTaskAnswers(left, right) {
  const leftRank = classificationRank(left.classification);
  const rightRank = classificationRank(right.classification);
  if (leftRank !== rightRank) {
    return {
      choice: leftRank > rightRank ? 'A' : 'B',
      basis: 'classification',
    };
  }
  if (left.classification === 'incomplete') {
    const leftGaps = gapCount(left);
    const rightGaps = gapCount(right);
    if (leftGaps !== rightGaps) {
      return {
        choice: leftGaps < rightGaps ? 'A' : 'B',
        basis: 'covered-criteria',
      };
    }
  }
  return { choice: 'tie', basis: 'equal-criteria' };
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

export function extractNamedPaths(text, task) {
  validateTask(task);
  return extractDeclaredPaths(text, task);
}

function extractDeclaredPaths(text, task) {
  const declaredPaths = [...new Set([
    ...task.requiredPaths,
    ...(task.optionalPaths ?? []),
    ...(task.forbiddenPaths ?? []),
  ].map(normalizePath))];
  if (declaredPaths.length === 0)
    return [];

  const alternatives = declaredPaths
    .sort((left, right) => right.length - left.length || left.localeCompare(right, 'en'))
    .map(pathPatternFor)
    .join('|');
  const selector = '(?:(?::\\d+(?:-\\d+)?)|(?:::[A-Za-z_][A-Za-z0-9_.+\\-]*(?:::[A-Za-z_][A-Za-z0-9_.+\\-]*)*)|(?::[A-Za-z_][A-Za-z0-9_.+\\-]*)|(?:#[A-Za-z0-9_][A-Za-z0-9_.:+\\-]*))?';
  const pathPattern = new RegExp(
    `(?:^|[\\s\x60"'(\\[])(${alternatives})${selector}(?=$|[\\s\x60"',.;!?)}\\]])`,
    'gm');
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
  if (!Array.isArray(task.pathExtensions) || task.pathExtensions.length === 0 ||
      task.pathExtensions.some(extension => typeof extension !== 'string' ||
        !/^[A-Za-z0-9][A-Za-z0-9+_-]*$/.test(extension)))
    throw new Error(`Task oracle '${task.id}' requires valid pathExtensions without leading dots.`);
  const pathExtensions = new Set(task.pathExtensions.map(extension => extension.toLocaleLowerCase('en-US')));
  if (pathExtensions.size !== task.pathExtensions.length)
    throw new Error(`Task oracle '${task.id}' has duplicate pathExtensions.`);
  const pathGroups = [task.requiredPaths, task.optionalPaths ?? [], task.forbiddenPaths ?? []];
  if (pathGroups.some(paths => !Array.isArray(paths) ||
      paths.some(path => typeof path !== 'string' || path.length === 0)))
    throw new Error(`Task oracle '${task.id}' has an invalid path array.`);
  for (const path of pathGroups.flat()) {
    const normalized = normalizePath(path).toLocaleLowerCase('en-US');
    if (![...pathExtensions].some(extension => normalized.endsWith(`.${extension}`)))
      throw new Error(
        `Task oracle '${task.id}' path '${path}' uses an extension not listed in pathExtensions.`);
  }
  if (task.requiredSymbols !== undefined && !Array.isArray(task.requiredSymbols))
    throw new Error(`Task oracle '${task.id}' has an invalid symbol array.`);
  for (const claim of [...task.requiredClaims, ...(task.forbiddenClaims ?? [])]) {
    if (typeof claim.id !== 'string' || !Array.isArray(claim.terms) ||
        claim.terms.length === 0 ||
        claim.terms.some(group => !Array.isArray(group) || group.length === 0 ||
          group.some(term => typeof term !== 'string' || term.trim().length === 0)))
      throw new Error(`Task oracle '${task.id}' has an invalid claim.`);
    if (claim.match !== undefined && claim.match !== 'affirmative-phrase')
      throw new Error(`Task oracle '${task.id}' has an unsupported claim matching mode.`);
    if (claim.match === 'affirmative-phrase' && (!Array.isArray(claim.evidence) ||
        claim.evidence.length === 0 || claim.evidence.some(item =>
          !/^[0-9a-f]{40}$/.test(item.commit ?? '') || !item.path || !item.lines)))
      throw new Error(`Task oracle '${task.id}' requires pinned source evidence for affirmative phrases.`);
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

function pathPatternFor(path) {
  return path
    .split('/')
    .map(component => component.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'))
    .join('[\\\\/]');
}

function includesText(text, term) {
  return normalizeClaimText(text).includes(normalizeClaimText(term));
}

function normalizeClaimText(text) {
  return text.toLocaleLowerCase('en-US').replace(/(?<=\d)[, \u00a0](?=\d{3}\b)/g, '')
    .replace(/[`*_]/g, '').replace(/\s+/g, ' ').trim();
}

function isForbiddenClaimAsserted(text, claim) {
  if (claim.match !== 'affirmative-phrase')
    return claim.terms.every(group => group.some(term => includesText(text, term)));
  const clauses = text.split(/(?<=[.!?])\s+|[\r\n;]+|\b(?:but|however|но|однако)\b/iu);
  return clauses.some(clause => {
    const normalized = normalizeClaimText(clause);
    const positions = claim.terms.map(group => group.map(term => {
      const phrase = normalizeClaimText(term);
      return { start: normalized.indexOf(phrase), length: phrase.length };
    }).filter(position => position.start >= 0));
    if (positions.some(group => group.length === 0))
      return false;
    const first = Math.min(...positions.flat().map(position => position.start));
    const last = Math.max(...positions.flat().map(position => position.start + position.length));
    const before = normalized.slice(0, first);
    const after = normalized.slice(last);
    const deniedBefore = /\b(?:not(?! only\b)|never|false|incorrect|wrong|deny|reject|refute|contrary to)\b|(?:^|\s)(?:не|неверно|ложно|ошибочно|неправда)(?:\s|,|$)/iu.test(before);
    const deniedAfter = /^["'”’\s,:-]*(?:is|was|would be|это)?\s*(?:false|incorrect|wrong|untrue|неверно|ложно|неправда)\b/iu.test(after);
    return !deniedBefore && !deniedAfter;
  });
}

function classificationRank(classification) {
  return { incorrect: 0, empty: 1, incomplete: 2, complete: 3 }[classification] ?? -1;
}

function gapCount(evaluation) {
  return evaluation.missingPaths.length + evaluation.missingClaims.length +
    evaluation.missingSymbols.length;
}
