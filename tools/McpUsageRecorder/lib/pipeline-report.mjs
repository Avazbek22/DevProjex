import { resolve } from 'node:path';
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

function requiredPath(value, label, baseDirectory) {
  if (typeof value !== 'string' || value.length === 0)
    throw new Error(`${label} is required before a report can be emitted.`);
  return resolve(baseDirectory, value);
}
