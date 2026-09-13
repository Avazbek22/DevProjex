import { createHash } from 'node:crypto';
import { readdir, readFile } from 'node:fs/promises';
import { join, resolve } from 'node:path';
import { loadTaskOracleRegistry } from './task-oracle.mjs';
import { evaluateSavedSeries, loadSavedAssessments } from './saved-evaluation.mjs';
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
  await validateRawCaptures(seriesDirectory, records);
  const oraclePath = requiredPath(definition.evaluation?.oracleRegistry, 'evaluation.oracleRegistry', baseDirectory);
  const taskRegistry = loadTaskOracleRegistry(oraclePath);
  const assessments = definition.evaluation?.savedAssessments
    ? await loadSavedAssessments(resolve(baseDirectory, definition.evaluation.savedAssessments))
    : null;
  const evaluation = evaluateSavedSeries(definition, records, taskRegistry, assessments);
  const analysis = analyzeSavedReadings(records, {
    smallFileCharacters: definition.limits.smallFileCharacters,
  });
  return {
    schemaVersion: 1,
    series: manifest.identity,
    accounting,
    evaluation,
    analysis,
  };
}

async function validateRawCaptures(seriesDirectory, records) {
  const directory = join(resolve(seriesDirectory), 'captures');
  let names;
  try {
    names = (await readdir(directory)).filter(name => name.endsWith('.json')).sort();
  } catch (error) {
    if (error?.code === 'ENOENT')
      return;
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
  }
}

function requiredPath(value, label, baseDirectory) {
  if (typeof value !== 'string' || value.length === 0)
    throw new Error(`${label} is required before a report can be emitted.`);
  return resolve(baseDirectory, value);
}
