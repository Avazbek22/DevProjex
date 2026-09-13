import { createHash, randomUUID } from 'node:crypto';
import { mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { tmpdir } from 'node:os';
import { spawn } from 'node:child_process';
import { probeMcpServer } from './mcp-probe.mjs';
import { loadSavedAssessments } from './saved-evaluation.mjs';
import { recordStreamJson } from './stream-json.mjs';
import { loadTaskOracleRegistry } from './task-oracle.mjs';
import {
  createRunRecord,
  createSeries,
  inspectRunAssignment,
  readSeriesManifest,
  readSeriesRecords,
  resumeSeries,
  storeRunRecord,
} from './series-store.mjs';

export async function loadPipelineDefinition(path) {
  const absolutePath = resolve(path);
  const definition = JSON.parse(await readFile(absolutePath, 'utf8'));
  validateDefinition(definition);
  return { definition, baseDirectory: dirname(absolutePath) };
}

export async function preparePipelineSeries(definition, baseDirectory, probe = probeMcpServer) {
  validateDefinition(definition);
  const oraclePath = resolve(baseDirectory, definition.evaluation.oracleRegistry);
  const assessmentPath = resolve(baseDirectory, definition.evaluation.savedAssessments);
  const oracleRegistry = await readPinnedJson(oraclePath, 'task oracle registry');
  const taskOracles = loadTaskOracleRegistry(oraclePath);
  for (const task of definition.tasks) {
    if (!taskOracles.has(task.oracle ?? task.id))
      throw new Error(`Task '${task.id}' has no pinned oracle; no session was started.`);
  }
  await loadSavedAssessments(assessmentPath);
  const observations = [];
  for (const arm of definition.arms) {
    const observed = await probe(resolveCommand(arm.server, baseDirectory), {
      name: 'mcp-usage-recorder',
      version: definition.clientVersion,
      protocolVersion: definition.protocolVersion,
    }, definition.limits.probeTimeoutMs);
    validateObservation(observed, arm.id);
    observations.push({ arm: arm.id, ...observed });
  }
  return {
    seriesId: definition.seriesId,
    productBuildSha: definition.productBuildSha,
    model: definition.model,
    clientVersion: definition.clientVersion,
    toolLoadingMode: definition.toolLoadingMode,
    serverInstructions: JSON.stringify(observations.map(value => ({
      arm: value.arm,
      instructions: value.instructions,
    }))),
    toolsList: observations.map(value => ({ arm: value.arm, response: value.toolsList })),
    toolConfiguration: {
      arms: definition.arms.map(arm => ({
        arm: arm.id,
        configuration: arm.toolConfiguration,
      })),
    },
    limits: definition.limits,
    pricing: definition.pricing,
    seriesDefinition: { definition: identityDefinition(definition), oracleRegistry },
  };
}

export async function runPipeline(options) {
  const loaded = await loadPipelineDefinition(options.definitionPath);
  const configuration = await preparePipelineSeries(
    loaded.definition,
    loaded.baseDirectory,
    options.probe);
  const series = options.mode === 'new'
    ? await createSeries(options.rootDirectory, configuration)
    : await resumeSeries(options.seriesDirectory, configuration);
  const directory = series.directory;
  const definition = loaded.definition;
  await persistObservedConfiguration(directory, configuration);
  const results = [];

  for (const task of definition.tasks) {
    for (let repetition = 1; repetition <= definition.repetitions; repetition++) {
      for (const arm of definition.arms) {
        while (true) {
          const records = await readSeriesRecords(directory);
          const state = inspectRunAssignment(series.manifest, records, {
            task: task.id,
            repetition,
            arm: arm.id,
          });
          if (state.completed) {
            results.push({ task: task.id, repetition, arm: arm.id, status: 'reused' });
            break;
          }
          if (state.nextAttempt > definition.limits.maxAttemptsPerAssignment) {
            results.push({ task: task.id, repetition, arm: arm.id, status: 'attempt-limit' });
            break;
          }
          const sessionId = randomUUID();
          const report = await executeSession({
            definition,
            task,
            arm,
            baseDirectory: loaded.baseDirectory,
            sessionId,
            manifest: series.manifest,
            seriesDirectory: directory,
            execute: options.execute,
          });
          const record = createRunRecord(series.manifest, {
            task: task.id,
            repetition,
            arm: arm.id,
            attempt: state.nextAttempt,
          }, report);
          const stored = await storeRunRecord(directory, record);
          results.push({
            task: task.id,
            repetition,
            arm: arm.id,
            attempt: state.nextAttempt,
            status: stored.status,
            outcome: record.measurement.outcome,
          });
          if (record.measurement.outcome === 'success')
            break;
        }
      }
    }
  }
  return { directory, manifest: series.manifest, results };
}

export async function validateSavedPipeline(seriesDirectory, definition, baseDirectory) {
  validateDefinition(definition);
  const observedPath = join(resolve(seriesDirectory), 'observed.json');
  let observed;
  try {
    observed = JSON.parse(await readFile(observedPath, 'utf8'));
  } catch (error) {
    throw new Error(`Unable to validate saved observations: ${error.message}`);
  }
  const oracleRegistry = await readPinnedJson(
    resolve(baseDirectory, definition.evaluation.oracleRegistry),
    'task oracle registry');
  return resumeSeries(seriesDirectory, {
    seriesId: definition.seriesId,
    productBuildSha: definition.productBuildSha,
    model: definition.model,
    clientVersion: definition.clientVersion,
    toolLoadingMode: definition.toolLoadingMode,
    serverInstructions: observed.serverInstructions,
    toolsList: observed.toolsList,
    toolConfiguration: {
      arms: definition.arms.map(arm => ({ arm: arm.id, configuration: arm.toolConfiguration })),
    },
    limits: definition.limits,
    pricing: definition.pricing,
    seriesDefinition: { definition: identityDefinition(definition), oracleRegistry },
  });
}

async function executeSession(context) {
  if (context.execute)
    return context.execute(context);
  const mcpConfiguration = await writeSessionConfiguration(context);
  const pinned = {
    sessionId: context.sessionId,
    model: context.definition.model,
    clientVersion: context.definition.clientVersion,
    toolLoadingMode: context.definition.toolLoadingMode,
    buildSha: context.definition.productBuildSha,
    limits: context.definition.limits,
    limitsSha256: context.manifest.identity.limitsSha256,
    serverInstructionsSha256: context.manifest.identity.serverInstructionsSha256,
    toolConfigurationSha256: context.manifest.identity.toolConfigurationSha256,
    pricingSha256: context.manifest.identity.pricingSha256,
    toolsListSha256: context.manifest.identity.toolsListSha256,
    seriesDefinitionSha256: context.manifest.identity.seriesDefinitionSha256,
  };
  const command = resolveCommand(context.definition.client, context.baseDirectory);
  const replacements = {
    prompt: context.task.prompt,
    sessionId: context.sessionId,
    model: context.definition.model,
    mcpConfigPath: mcpConfiguration.path,
    toolConfigPath: mcpConfiguration.toolConfigurationPath,
    arm: context.arm.id,
  };
  let result;
  try {
    result = await runProcess({
      ...command,
      args: (command.args ?? []).map(argument => replacePlaceholders(argument, replacements)),
    }, context.definition.limits.sessionTimeoutMs);
    const capture = await storeRawCapture(context.seriesDirectory, context.sessionId, result);
    const parsed = parseCapturedLines(result.stdout);
    let report = recordStreamJson(parsed.lines, pinned);
    const failed = result.exitCode !== 0 || result.timedOut || parsed.invalidLines > 0;
    if (failed && report.session.status === 'success') {
      report = {
        ...report,
        session: { ...report.session, status: result.timedOut ? 'aborted' : 'error', successful: false },
      };
    }
    return {
      ...report,
      session: { ...report.session, wallDurationMs: result.durationMs },
      capture: {
        ...report.capture,
        rawCaptureSha256: capture.sha256,
        invalidLines: parsed.invalidLines,
        processExitCode: result.exitCode,
        processSignal: result.signal,
        timedOut: result.timedOut,
      },
    };
  } finally {
    await rm(mcpConfiguration.directory, { recursive: true, force: true });
  }
}

async function writeSessionConfiguration(context) {
  const directory = await mkdtemp(join(tmpdir(), 'mcp-usage-session-'));
  const path = join(directory, 'mcp.json');
  const toolConfigurationPath = join(directory, 'tools.json');
  const server = resolveCommand(context.arm.server, context.baseDirectory);
  const value = {
    mcpServers: {
      [context.arm.id]: {
        command: server.command,
        args: server.args ?? [],
        env: server.env ?? {},
      },
    },
  };
  await writeFile(path, `${JSON.stringify(value, null, 2)}\n`, { encoding: 'utf8', flag: 'wx' });
  await writeFile(
    toolConfigurationPath,
    `${JSON.stringify(context.arm.toolConfiguration, null, 2)}\n`,
    { encoding: 'utf8', flag: 'wx' });
  return { path, toolConfigurationPath, directory };
}

async function persistObservedConfiguration(directory, configuration) {
  const path = join(directory, 'observed.json');
  const value = {
    serverInstructions: configuration.serverInstructions,
    toolsList: configuration.toolsList,
  };
  try {
    await writeFile(path, `${JSON.stringify(value, null, 2)}\n`, { encoding: 'utf8', flag: 'wx' });
  } catch (error) {
    if (error?.code !== 'EEXIST')
      throw error;
  }
}

async function storeRawCapture(seriesDirectory, sessionId, result) {
  const directory = join(seriesDirectory, 'captures');
  await mkdir(directory, { recursive: true });
  const value = {
    schemaVersion: 1,
    sessionId,
    stdout: result.stdout,
    stderr: result.stderr,
    exitCode: result.exitCode,
    signal: result.signal,
    timedOut: result.timedOut,
    durationMs: result.durationMs,
  };
  const json = `${JSON.stringify(value, null, 2)}\n`;
  await writeFile(join(directory, `${sessionId}.json`), json, { encoding: 'utf8', flag: 'wx' });
  return { sha256: createHash('sha256').update(json).digest('hex') };
}

function parseCapturedLines(stdout) {
  const lines = [];
  let invalidLines = 0;
  for (const line of stdout.split(/\r?\n/)) {
    if (line.trim().length === 0)
      continue;
    try {
      lines.push(JSON.parse(line));
    } catch {
      invalidLines++;
    }
  }
  return { lines, invalidLines };
}

function validateDefinition(definition) {
  if (!definition || typeof definition !== 'object' || Array.isArray(definition))
    throw new Error('Pipeline definition must be an object.');
  for (const name of ['seriesId', 'productBuildSha', 'model', 'clientVersion', 'toolLoadingMode'])
    requireText(definition[name], name);
  if (!/^[0-9a-f]{40}$/.test(definition.productBuildSha))
    throw new Error('productBuildSha must be a full lowercase Git SHA.');
  if (!Number.isSafeInteger(definition.repetitions) || definition.repetitions <= 0)
    throw new Error('repetitions must be a positive safe integer.');
  if (!Array.isArray(definition.tasks) || definition.tasks.length === 0)
    throw new Error('At least one task is required.');
  if (!Array.isArray(definition.arms) || definition.arms.length < 2)
    throw new Error('At least two comparison arms are required.');
  ensureUnique(definition.tasks.map(task => requireText(task.id, 'task id')), 'task id');
  ensureUnique(definition.arms.map(arm => requireText(arm.id, 'arm id')), 'arm id');
  for (const task of definition.tasks)
    requireText(task.prompt, `task '${task.id}' prompt`);
  for (const arm of definition.arms) {
    validateCommand(arm.server, `arm '${arm.id}' server`);
    if (!arm.toolConfiguration || typeof arm.toolConfiguration !== 'object' || Array.isArray(arm.toolConfiguration))
      throw new Error(`arm '${arm.id}' toolConfiguration must be an object.`);
  }
  validateCommand(definition.client, 'client');
  const clientArguments = definition.client.args ?? [];
  for (const placeholder of ['prompt', 'sessionId', 'model', 'mcpConfigPath', 'toolConfigPath']) {
    if (!clientArguments.some(argument => argument.includes(`{${placeholder}}`)))
      throw new Error(`client arguments must consume the {${placeholder}} placeholder.`);
  }
  if (!definition.limits || !Number.isSafeInteger(definition.limits.maxAttemptsPerAssignment) ||
      definition.limits.maxAttemptsPerAssignment <= 0 ||
      !Number.isSafeInteger(definition.limits.probeTimeoutMs) || definition.limits.probeTimeoutMs <= 0 ||
      !Number.isSafeInteger(definition.limits.sessionTimeoutMs) || definition.limits.sessionTimeoutMs <= 0)
    throw new Error('limits must pin positive attempt, probe, and session bounds.');
  if (!definition.pricing || typeof definition.pricing !== 'object')
    throw new Error('pricing is required.');
  if (!definition.evaluation || typeof definition.evaluation !== 'object')
    throw new Error('evaluation configuration is required.');
  requireText(definition.evaluation.oracleRegistry, 'evaluation.oracleRegistry');
  requireText(definition.evaluation.savedAssessments, 'evaluation.savedAssessments');
}

function validateObservation(observed, arm) {
  if (!observed || typeof observed.instructions !== 'string' || observed.instructions.length === 0)
    throw new Error(`arm '${arm}' did not expose server instructions; no session was started.`);
  if (!observed.toolsList || !Array.isArray(observed.toolsList.tools))
    throw new Error(`arm '${arm}' did not expose a complete tools/list response; no session was started.`);
}

function resolveCommand(command, baseDirectory) {
  validateCommand(command, 'process');
  return {
    ...command,
    cwd: command.cwd ? resolve(baseDirectory, command.cwd) : baseDirectory,
  };
}

function validateCommand(command, label) {
  if (!command || typeof command !== 'object' || typeof command.command !== 'string' || command.command.length === 0)
    throw new Error(`${label} command is required.`);
  if (command.args !== undefined && (!Array.isArray(command.args) || command.args.some(value => typeof value !== 'string')))
    throw new Error(`${label} arguments must be strings.`);
}

function requireText(value, label) {
  if (typeof value !== 'string' || value.length === 0)
    throw new Error(`${label} must be a non-empty string.`);
  return value;
}

async function readPinnedJson(path, label) {
  try {
    return JSON.parse(await readFile(path, 'utf8'));
  } catch (error) {
    throw new Error(`Unable to pin ${label}: ${error.message}`);
  }
}

function identityDefinition(definition) {
  const copy = structuredClone(definition);
  delete copy.evaluation.savedAssessments;
  return copy;
}

function ensureUnique(values, label) {
  if (new Set(values).size !== values.length)
    throw new Error(`${label} values must be unique.`);
}

function replacePlaceholders(value, replacements) {
  return value.replace(
    /\{(prompt|sessionId|model|mcpConfigPath|toolConfigPath|arm)\}/g,
    (_, name) => replacements[name]);
}

function runProcess(command, timeoutMs) {
  return new Promise((resolveRun, rejectRun) => {
    const started = Date.now();
    const child = spawn(command.command, command.args ?? [], {
      cwd: command.cwd,
      env: { ...process.env, ...(command.env ?? {}) },
      stdio: ['ignore', 'pipe', 'pipe'],
      windowsHide: true,
      detached: process.platform !== 'win32',
    });
    let stdout = '';
    let stderr = '';
    let timedOut = false;
    child.stdout.setEncoding('utf8');
    child.stderr.setEncoding('utf8');
    child.stdout.on('data', chunk => { stdout += chunk; });
    child.stderr.on('data', chunk => { stderr += chunk; });
    let settled = false;
    let timer;
    child.once('error', error => {
      if (settled)
        return;
      settled = true;
      clearTimeout(timer);
      rejectRun(error);
    });
    timer = setTimeout(() => {
      timedOut = true;
      terminateProcessTree(child);
    }, timeoutMs);
    child.once('exit', (exitCode, signal) => {
      if (settled)
        return;
      settled = true;
      clearTimeout(timer);
      resolveRun({ stdout, stderr, exitCode, signal, timedOut, durationMs: Date.now() - started });
    });
  });
}

function terminateProcessTree(child) {
  if (!child.pid)
    return;
  if (process.platform === 'win32') {
    spawn('taskkill', ['/pid', String(child.pid), '/t', '/f'], {
      stdio: 'ignore',
      windowsHide: true,
    }).unref();
  } else {
    try {
      process.kill(-child.pid, 'SIGKILL');
    } catch {
    }
  }
  try {
    child.kill('SIGKILL');
  } catch {
  }
}
