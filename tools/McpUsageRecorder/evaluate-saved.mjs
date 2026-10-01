#!/usr/bin/env node
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import {
  loadTaskOracleRegistry,
  evaluateTaskAnswer,
  compareTaskAnswers,
  splitComparedAnswers,
} from './lib/task-oracle.mjs';
import { summarizeOrderedAssessments } from './lib/order-consistency.mjs';

const argumentsList = process.argv.slice(2);
const oraclePath = option('--oracles');
const assessmentPath = option('--assessments', false);
const summaryPaths = argumentsList.filter(value => !value.startsWith('--') && value !== oraclePath &&
  value !== assessmentPath);
if (!oraclePath || summaryPaths.length === 0) {
  process.stderr.write('Usage: node evaluate-saved.mjs --oracles <file> [--assessments <file>] <summary.json>...\n');
  process.exitCode = 2;
} else {
  const registry = loadTaskOracleRegistry(resolve(oraclePath));
  const runs = summaryPaths.map(path => evaluateSummary(resolve(path), registry));
  const orderConsistency = assessmentPath
    ? summarizeOrderedAssessments(JSON.parse(readFileSync(resolve(assessmentPath), 'utf8')))
    : null;
  process.stdout.write(`${JSON.stringify({ schemaVersion: 1, runs, orderConsistency }, null, 2)}\n`);
}

function evaluateSummary(path, registry) {
  const summary = JSON.parse(readFileSync(path, 'utf8'));
  const task = registry.get(summary.task);
  if (!task)
    throw new Error(`No task oracle is registered for '${summary.task}'.`);
  const answers = splitComparedAnswers(summary.answer ?? '');
  const evaluatedA = evaluateTaskAnswer(task, answers.A);
  const evaluatedB = evaluateTaskAnswer(task, answers.B);
  return {
    id: summary.id,
    task: summary.task,
    server: summary.server,
    answers: {
      A: evaluatedA,
      B: evaluatedB,
    },
    oracleComparison: compareTaskAnswers(evaluatedA, evaluatedB),
  };
}

function option(name, required = true) {
  const index = argumentsList.indexOf(name);
  if (index < 0) {
    if (required)
      return null;
    return null;
  }
  if (index === argumentsList.length - 1)
    throw new Error(`${name} requires a value.`);
  return argumentsList[index + 1];
}
