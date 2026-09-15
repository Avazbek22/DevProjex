import { createHash } from 'node:crypto';
import { readdir, readFile } from 'node:fs/promises';
import { join, resolve } from 'node:path';
import { loadTaskOracleRegistry } from './task-oracle.mjs';
import { evaluateSavedSeries } from './saved-evaluation.mjs';
import { readPipelineAssessments } from './pipeline-judging.mjs';
import { recordStreamJson } from './stream-json.mjs';
import { recordEvents } from './recorder.mjs';
import { validateSavedPipeline } from './pipeline-runner.mjs';
import { analyzeSavedReadings } from './session-analysis.mjs';
import {
  readSeriesManifest,
  readSeriesRecords,
  summarizeSeriesRecords,
} from './series-store.mjs';

export async function buildPipelineReport(seriesDirectory, definition, baseDirectory) {
  const manifest = await readSeriesManifest(seriesDirectory);
  const records = await readSeriesRecords(seriesDirectory);
  const accounting = summarizeSeriesRecords(manifest, records);
  await validateRawCaptures(
    seriesDirectory,
    records,
    manifest.identity.seriesDefinitionSha256 !== undefined);
  if (manifest.identity.seriesDefinitionSha256 !== undefined)
    await validateSavedPipeline(seriesDirectory, definition, baseDirectory);
  const oraclePath = requiredPath(definition.evaluation?.oracleRegistry, 'evaluation.oracleRegistry', baseDirectory);
  const taskRegistry = loadTaskOracleRegistry(oraclePath);
  const assessments = await readPipelineAssessments(seriesDirectory, definition, baseDirectory);
  const evaluation = evaluateSavedSeries(definition, records, taskRegistry, assessments);
  const analysis = analyzeSavedReadings(records, {
    smallFileCharacters: definition.limits.smallFileCharacters,
  });
  const table = accounting.arms.map(arm => {
    const sessions = records.filter(record => record.identity.arm === arm.arm);
    const classifications = evaluation.rows.map(row => ({ task: row.task, repetition: row.repetition,
      classification: row.answers[arm.arm].classification,
      requiredEvidence: row.answers[arm.arm].requiredEvidence,
      knownContradictions: row.answers[arm.arm].knownContradictions,
      semanticCorrectness: row.answers[arm.arm].semanticCorrectness,
      outcomeStatements: row.answers[arm.arm].outcomeStatements }));
    return {
      arm: arm.arm,
      productBuildSha: manifest.arms?.[arm.arm]?.productBuildSha ?? manifest.identity.productBuildSha,
      sessions: arm.sessions, outcomes: arm.outcomes,
      modelTurns: sessions.reduce((sum, record) => sum + record.measurement.modelTurns, 0),
      toolCalls: sessions.reduce((sum, record) => sum + record.measurement.toolCalls, 0),
      usage: arm.usage, cost: arm.cost,
      wallDurationMs: sessions.some(record => record.measurement.wallDurationMs === null) ? null :
        sessions.reduce((sum, record) => sum + record.measurement.wallDurationMs, 0),
      oracle: classifications,
      judging: evaluation.rows.map(row => ({ task: row.task, repetition: row.repetition,
        correctness: row.ordered?.correctness ?? null, preference: row.ordered?.preference ?? null })),
    };
  });
  return {
    schemaVersion: 1,
    series: manifest.identity,
    accounting,
    evaluation,
    analysis,
    table,
    orderDisagreement: evaluation.orderConsistency,
  };
}

async function validateRawCaptures(seriesDirectory, records, required) {
  const directory = join(resolve(seriesDirectory), 'captures');
  let names;
  try {
    names = (await readdir(directory)).filter(name => name.endsWith('.json')).sort();
  } catch (error) {
    if (error?.code === 'ENOENT' && !required)
      return;
    if (error?.code === 'ENOENT')
      throw new Error('Pipeline report rejected: raw captures are missing.');
    throw error;
  }
  const bySession = new Map(records.map(record => [record.identity.sessionId, record]));
  if (names.length !== records.length)
    throw new Error('Pipeline report rejected: raw captures do not map one-to-one to session records.');
  for (const name of names) {
    const text = await readFile(join(directory, name), 'utf8');
    const capture = JSON.parse(text);
    const expectedName = `${capture.sessionId}.json`;
    const record = bySession.get(capture.sessionId);
    if (name !== expectedName || !record)
      throw new Error('Pipeline report rejected: a raw capture has no matching session record.');
    const fingerprint = createHash('sha256').update(text).digest('hex');
    if (record.measurement.capture?.rawCaptureSha256 !== fingerprint)
      throw new Error(`Pipeline report rejected: raw capture '${name}' does not match its session record.`);
    const identity = record.identity;
    const pinned = { ...identity, buildSha: identity.productBuildSha };
    const lines = capture.stdout.split(/\r?\n/).filter(line => line.trim()).flatMap(line => {
      try { return [JSON.parse(line)]; } catch { return []; }
    });
    const launchFailed = capture.processError && capture.stdout.trim().length === 0;
    const replayed = launchFailed
      ? recordEvents([{ type: 'session.end', status: 'error' }], pinned)
      : recordStreamJson(lines, pinned);
    if (!launchFailed && (!replayed.capture.actualUsageObserved ||
        !replayed.capture.completeOutputUsageObserved))
      throw new Error('Pipeline report rejected: API usage is missing or incomplete; zero cost cannot be inferred.');
    if (JSON.stringify(replayed.totals.usage) !== JSON.stringify(record.measurement.usage) ||
        replayed.totals.modelTurns !== record.measurement.modelTurns ||
        replayed.totals.toolCalls !== record.measurement.toolCalls ||
        (replayed.finalAnswer ?? null) !== record.measurement.finalAnswer ||
        capture.durationMs !== record.measurement.wallDurationMs)
      throw new Error('Pipeline report rejected: replayed raw counters or answer differ from the immutable session record.');
  }
}

export function formatPipelineTable(report) {
  const rows = [
    '| Arm | Model turns | Tool calls | Input | Cache write | Cache read | Output | Cost | Wall ms | Oracle |',
    '| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |',
    ...report.table.map(row => `| ${row.arm} | ${row.modelTurns} | ${row.toolCalls} | ${row.usage.inputTokens} | ` +
      `${row.usage.cacheWriteTokens} | ${row.usage.cacheReadTokens} | ${row.usage.outputTokens} | ` +
      `${row.cost.amount} ${row.cost.currency} | ${row.wallDurationMs ?? 'unknown'} | ` +
      `${row.oracle.map(item => `${item.task}/${item.repetition}: ${item.outcomeStatements.join('; ')}`).join('<br>')} |`),
    '',
  ];
  for (const dimension of ['correctness', 'preference']) {
    const summary = report.orderDisagreement[dimension];
    rows.push(`${dimension} order disagreements: ${summary.disagreements}/${summary.assessedPairs}; ` +
      `rate=${summary.disagreementRate ?? 'not assessed'}.`);
  }
  rows.push('', 'Separate ordered choices:', JSON.stringify(report.table.map(row => ({ arm: row.arm, judging: row.judging }))));
  return rows.join('\n') + '\n';
}

function requiredPath(value, label, baseDirectory) {
  if (typeof value !== 'string' || value.length === 0)
    throw new Error(`${label} is required before a report can be emitted.`);
  return resolve(baseDirectory, value);
}
