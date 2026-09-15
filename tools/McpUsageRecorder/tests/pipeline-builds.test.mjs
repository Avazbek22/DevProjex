import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, writeFileSync, readdirSync, rmSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { tmpdir } from 'node:os';
import { fileURLToPath } from 'node:url';
import { runPipeline, loadPipelineDefinition } from '../lib/pipeline-runner.mjs';
import { readSeriesRecords } from '../lib/series-store.mjs';
import { buildPipelineReport } from '../lib/pipeline-report.mjs';
import { spawnSync } from 'node:child_process';
import { preparePipelineJudgments } from '../lib/pipeline-judging.mjs';

function fixture() {
  const directory = mkdtempSync(join(tmpdir(), 'mcp-build-comparison-'));
  const source = fileURLToPath(new URL('./fixtures/pipeline-definition.json', import.meta.url));
  const base = dirname(source);
  const definition = JSON.parse(readFileSync(source, 'utf8'));
  for (const process of [definition.client, ...definition.arms.map(arm => arm.server)])
    process.cwd = base;
  definition.evaluation.oracleRegistry = join(base, 'task-oracles.json');
  definition.evaluation.savedAssessments = join(base, 'saved-assessments.json');
  definition.evaluation.orderCalibration = { evaluatedPairs: 18, orderDisagreementRate: 1 / 3 };
  definition.arms.forEach((arm, index) => {
    arm.productBuildSha = (index === 0 ? 'a' : 'b').repeat(40);
    arm.limits = { maximumResults: index === 0 ? 100 : 200 };
  });
  const definitionPath = join(directory, 'definition.json');
  const save = () => writeFileSync(definitionPath, JSON.stringify(definition));
  save();
  return { directory, definitionPath, definition, save, cleanup: () => rmSync(directory, { recursive: true, force: true }) };
}

test('each build keeps its own SHA instructions catalog and limits', async () => {
  const f = fixture();
  try {
    const result = await runPipeline({ mode: 'new', rootDirectory: f.directory, definitionPath: f.definitionPath });
    const records = await readSeriesRecords(result.directory);
    for (const arm of f.definition.arms) {
      const record = records.find(record => record.identity.arm === arm.id);
      assert.equal(record.identity.productBuildSha, arm.productBuildSha);
      assert.equal(record.identity.serverInstructionsSha256, result.manifest.arms[arm.id].serverInstructionsSha256);
    }
    assert.notEqual(records[0].identity.serverInstructionsSha256, records[1].identity.serverInstructionsSha256);
    assert.notEqual(records[0].identity.limitsSha256, records[1].identity.limitsSha256);
    const report = await buildPipelineReport(result.directory, f.definition, f.directory);
    assert.equal(report.table.length, 2);
    assert.equal(report.table[0].modelTurns, 11);
    assert.equal(report.table[0].toolCalls, 10);
    assert.ok(report.table[0].wallDurationMs >= 0);
    assert.equal(report.orderDisagreement.preference.disagreementRate, 1);
  } finally { f.cleanup(); }
});

test('a build without its SHA or measured order calibration cannot start', async () => {
  const f = fixture();
  try {
    delete f.definition.arms[1].productBuildSha; f.save();
    await assert.rejects(() => loadPipelineDefinition(f.definitionPath), /arm 'right'.*productBuildSha/);
    f.definition.arms[1].productBuildSha = 'b'.repeat(40);
    delete f.definition.evaluation.orderCalibration; f.save();
    await assert.rejects(() => loadPipelineDefinition(f.definitionPath), /order.*calibration/i);
  } finally { f.cleanup(); }
});

test('identity drift of either build is refused before a session can be skipped', async () => {
  const f = fixture();
  try {
    const result = await runPipeline({ mode: 'new', rootDirectory: f.directory, definitionPath: f.definitionPath });
    for (const index of [0, 1]) {
      const original = f.definition.arms[index].productBuildSha;
      f.definition.arms[index].productBuildSha = 'c'.repeat(40); f.save();
      await assert.rejects(() => runPipeline({ mode: 'resume', seriesDirectory: result.directory, definitionPath: f.definitionPath }), /identity mismatch/);
      f.definition.arms[index].productBuildSha = original; f.save();
      const limit = f.definition.arms[index].limits.maximumResults;
      f.definition.arms[index].limits.maximumResults = limit + 1; f.save();
      await assert.rejects(() => runPipeline({ mode: 'resume', seriesDirectory: result.directory, definitionPath: f.definitionPath }), /identity mismatch/);
      f.definition.arms[index].limits.maximumResults = limit; f.save();
      f.definition.arms[index].toolConfiguration.changed = true; f.save();
      await assert.rejects(() => runPipeline({ mode: 'resume', seriesDirectory: result.directory, definitionPath: f.definitionPath }), /identity mismatch/);
      delete f.definition.arms[index].toolConfiguration.changed; f.save();
    }
    await assert.rejects(() => runPipeline({ mode: 'new', rootDirectory: f.directory, definitionPath: f.definitionPath }), /already exists/);
  } finally { f.cleanup(); }
});

test('a table cannot release counters that differ from the raw client transcript', async () => {
  const f = fixture();
  try {
    const result = await runPipeline({ mode: 'new', rootDirectory: f.directory, definitionPath: f.definitionPath });
    const directory = join(result.directory, 'records');
    const path = join(directory, readdirSync(directory)[0]);
    const record = JSON.parse(readFileSync(path, 'utf8'));
    record.measurement.toolCalls++;
    writeFileSync(path, JSON.stringify(record));
    await assert.rejects(() => buildPipelineReport(result.directory, f.definition, f.directory), /replayed raw counters/);
  } finally { f.cleanup(); }
});

test('a failed client launch is saved as an immutable attempt', async () => {
  const f = fixture();
  try {
    f.definition.client.command = join(f.directory, 'absent-client'); f.save();
    const result = await runPipeline({ mode: 'new', rootDirectory: f.directory, definitionPath: f.definitionPath });
    const records = await readSeriesRecords(result.directory);
    assert.equal(records.length, 4);
    assert.ok(records.every(record => record.measurement.outcome !== 'success'));
    assert.equal(new Set(records.map(record => record.identity.sessionId)).size, 4);
    assert.equal(readdirSync(join(result.directory, 'captures')).length, 4);
  } finally { f.cleanup(); }
});

test('one command judges anonymous answers twice and refuses order-dependent verdicts', () => {
  const f = fixture();
  try {
    delete f.definition.evaluation.savedAssessments;
    f.definition.evaluation.judge = { command: process.execPath,
      args: [fileURLToPath(new URL('./fixtures/fake-judge.mjs', import.meta.url)), '{assessmentInputPath}'] };
    f.save();
    const script = fileURLToPath(new URL('../pipeline.mjs', import.meta.url));
    const output = join(f.directory, 'report.json');
    const result = spawnSync(process.execPath, [script, '--mode', 'new', '--definition', f.definitionPath,
      '--root', f.directory, '--output', output], { encoding: 'utf8', timeout: 20_000 });
    assert.equal(result.status, 0, result.stderr);
    const report = JSON.parse(readFileSync(output, 'utf8')).report;
    assert.equal(report.orderDisagreement.correctness.disagreements, 1);
    assert.equal(report.orderDisagreement.correctness.disagreementRate, 1);
    assert.equal(report.evaluation.rows[0].ordered.correctness.status, 'disagreement');
    const rebuilt = spawnSync(process.execPath, [script, '--mode', 'report', '--definition', f.definitionPath,
      '--series', join(f.directory, f.definition.seriesId)], { encoding: 'utf8', timeout: 20_000 });
    assert.equal(rebuilt.status, 0, rebuilt.stderr);
    assert.deepEqual(JSON.parse(rebuilt.stdout).report, report);
  } finally { f.cleanup(); }
});

test('a failed judgment stays stored when the same series resumes successfully', async () => {
  const f = fixture();
  try {
    delete f.definition.evaluation.savedAssessments;
    f.definition.evaluation.judge = { command: process.execPath,
      args: [fileURLToPath(new URL('./fixtures/fake-judge.mjs', import.meta.url)),
        '{assessmentInputPath}', '--fail-once', join(f.directory, 'attempt-marker')] };
    f.save();
    const result = await runPipeline({ mode: 'new', rootDirectory: f.directory, definitionPath: f.definitionPath });
    await assert.rejects(() => preparePipelineJudgments(result.directory, f.definition, f.directory), /Judgment failed/);
    const attempts = join(result.directory, 'judgments', 'attempts');
    const first = readdirSync(attempts)[0];
    const original = readFileSync(join(attempts, first), 'utf8');
    const recordNames = readdirSync(join(result.directory, 'records'));
    await runPipeline({ mode: 'resume', seriesDirectory: result.directory, definitionPath: f.definitionPath });
    await preparePipelineJudgments(result.directory, f.definition, f.directory);
    assert.equal(readFileSync(join(attempts, first), 'utf8'), original);
    assert.equal(readdirSync(attempts).length, 3);
    assert.deepEqual(readdirSync(join(result.directory, 'records')), recordNames);
    const report = await buildPipelineReport(result.directory, f.definition, f.directory);
    assert.equal(report.table[0].sessions, 1);
    const successName = readdirSync(attempts).find(name => name !== first);
    const capture = JSON.parse(readFileSync(join(attempts, successName), 'utf8'));
    capture.stdout = '{"correctness":"B","preference":"B"}';
    writeFileSync(join(attempts, successName), JSON.stringify(capture));
    await assert.rejects(() => buildPipelineReport(result.directory, f.definition, f.directory), /raw capture.*immutable receipt/);
  } finally { f.cleanup(); }
});
