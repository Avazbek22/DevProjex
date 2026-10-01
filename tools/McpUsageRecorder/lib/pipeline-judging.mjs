import { createHash, randomUUID } from 'node:crypto';
import { mkdir, mkdtemp, readFile, writeFile, rm } from 'node:fs/promises';
import { join, resolve } from 'node:path';
import { tmpdir } from 'node:os';
import { runProcess } from './pipeline-runner.mjs';
import { readSeriesRecords } from './series-store.mjs';
import { loadTaskOracleRegistry, evaluateTaskAnswer, compareTaskAnswers } from './task-oracle.mjs';
import { answerFingerprint, stripExperience, loadSavedAssessments, requiresOrderedAssessment } from './saved-evaluation.mjs';

export async function preparePipelineJudgments(directory, definition, baseDirectory) {
  if (definition.evaluation.savedAssessments)
    return;
  const records = await readSeriesRecords(directory);
  const registry = loadTaskOracleRegistry(resolve(baseDirectory, definition.evaluation.oracleRegistry));
  const candidates = definition.arms.map(arm => arm.id);
  const destination = join(directory, 'judgments');
  await mkdir(destination, { recursive: true });
  const pairs = [];
  for (const taskDefinition of definition.tasks) {
    const task = registry.get(taskDefinition.oracle ?? taskDefinition.id);
    for (let repetition = 1; repetition <= definition.repetitions; repetition++) {
      const sessions = candidates.map(arm => {
        const matching = records.filter(record => record.identity.task === taskDefinition.id &&
          record.identity.repetition === repetition && record.identity.arm === arm && record.measurement.outcome === 'success');
        if (matching.length !== 1)
          throw new Error('Judgment requires exactly one successful immutable session per assignment.');
        return matching[0];
      });
      const answers = sessions.map(record => stripExperience(record.measurement.finalAnswer));
      const evaluations = answers.map(answer => evaluateTaskAnswer(task, answer));
      if (!requiresOrderedAssessment(task, compareTaskAnswers(...evaluations), evaluations))
        continue;
      const pair = { task: taskDefinition.id, repetition, candidates };
      for (const presentation of ['forward', 'reverse']) {
        const indexes = presentation === 'forward' ? [0, 1] : [1, 0];
        const input = {
          presentation,
          task: taskDefinition.prompt,
          criteria: task,
          dimensions: ['correctness', 'preference'],
          candidates: indexes.map((index, position) => ({ id: position === 0 ? 'A' : 'B', answer: answers[index] })),
        };
        const key = digest(JSON.stringify({ task: pair.task, repetition, presentation }));
        const response = await executeJudgment(destination, key, input, definition, baseDirectory);
        const order = indexes.map(index => candidates[index]);
        pair[presentation] = {
          order, answerSha256: indexes.map(index => answerFingerprint(answers[index])),
          response: { order, correctness: response.correctness, preference: response.preference },
        };
      }
      pairs.push(pair);
    }
  }
  const source = { schemaVersion: 1, pairs };
  await immutableJson(join(destination, 'assessments.json'), source);
  await loadSavedAssessments(join(destination, 'assessments.json'));
}

export async function readPipelineAssessments(directory, definition, baseDirectory) {
  if (definition.evaluation.savedAssessments)
    return loadSavedAssessments(resolve(baseDirectory, definition.evaluation.savedAssessments));
  const destination = join(directory, 'judgments');
  const source = await loadSavedAssessments(join(destination, 'assessments.json'));
  for (const pair of source.pairs) {
    for (const presentation of ['forward', 'reverse']) {
      const key = digest(JSON.stringify({ task: pair.task, repetition: pair.repetition, presentation }));
      const receipt = JSON.parse(await readFile(join(destination, `${key}.json`), 'utf8'));
      const captured = await readFile(join(destination, 'attempts', receipt.captureName), 'utf8');
      if (digest(captured) !== receipt.captureSha256)
        throw new Error('Judgment raw capture does not match its immutable receipt.');
      const attempt = JSON.parse(captured);
      if (digest(JSON.stringify(receipt.input)) !== receipt.inputSha256 ||
          attempt.inputSha256 !== receipt.inputSha256 || receipt.input.presentation !== presentation)
        throw new Error('Judgment input identity differs from its immutable capture.');
      if (attempt.exitCode !== 0 || attempt.timedOut || attempt.processError)
        throw new Error('Failed judgment cannot be released as an assessment.');
      const response = parseResponse(attempt.stdout);
      if (JSON.stringify(response) !== JSON.stringify(receipt.response) ||
          pair[presentation].response.correctness !== response.correctness ||
          pair[presentation].response.preference !== response.preference ||
          JSON.stringify(receipt.input.candidates.map(candidate => answerFingerprint(candidate.answer))) !==
            JSON.stringify(pair[presentation].answerSha256))
        throw new Error('Judgment response or answer fingerprints differ from their immutable capture.');
    }
  }
  return source;
}

async function executeJudgment(destination, key, input, definition, baseDirectory) {
  const path = join(destination, `${key}.json`);
  const inputSha256 = digest(JSON.stringify(input));
  try {
    const saved = JSON.parse(await readFile(path, 'utf8'));
    if (saved.inputSha256 !== inputSha256)
      throw new Error('Judgment identity mismatch; refusing to reuse an answer from another pair.');
    return saved.response;
  } catch (error) { if (error.code !== 'ENOENT') throw error; }
  const temporary = await mkdtemp(join(tmpdir(), 'mcp-ordered-judgment-'));
  try {
    const inputPath = join(temporary, 'input.json');
    await writeFile(inputPath, JSON.stringify(input), { flag: 'wx' });
    const command = definition.evaluation.judge;
    const result = await runProcess({
      ...command, cwd: command.cwd ? resolve(baseDirectory, command.cwd) : baseDirectory,
      args: (command.args ?? []).map(argument => argument.replaceAll('{assessmentInputPath}', inputPath)),
    }, definition.limits.judgmentTimeoutMs ?? definition.limits.sessionTimeoutMs);
    const attempts = join(destination, 'attempts');
    await mkdir(attempts, { recursive: true });
    const captureName = `${key}-${randomUUID()}.json`;
    const captured = JSON.stringify({ ...result, inputSha256 });
    await writeFile(join(attempts, captureName), captured, { flag: 'wx' });
    if (result.exitCode !== 0 || result.timedOut || result.processError)
      throw new Error('Judgment failed; its immutable attempt was retained and no table was emitted.');
    const response = parseResponse(result.stdout);
    await immutableJson(path, { input, inputSha256, response, captureName, captureSha256: digest(captured) });
    return response;
  } finally { await rm(temporary, { recursive: true, force: true }); }
}

function parseResponse(text) {
  const response = JSON.parse(text);
  if (!['A', 'B', 'tie'].includes(response.correctness) || !['A', 'B', 'tie'].includes(response.preference))
    throw new Error('Judgment requires separate correctness and preference choices.');
  return { correctness: response.correctness, preference: response.preference };
}

async function immutableJson(path, value) {
  const text = `${JSON.stringify(value, null, 2)}\n`;
  try { await writeFile(path, text, { flag: 'wx' }); }
  catch (error) {
    if (error.code !== 'EEXIST') throw error;
    if (await readFile(path, 'utf8') !== text)
      throw new Error('Immutable judgment already exists with different contents.');
  }
}

function digest(text) { return createHash('sha256').update(text).digest('hex'); }
