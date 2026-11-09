// Runs headless Claude sessions per (task, arm, repeat) and keeps stream-json, tool calls, usage and
// the final answer of each. Every session is billed; use --dry-run to check a plan for free.
//
//   node run.mjs --tasks-file tasks/fourteen.json --arms-file my-arms.json [--work DIR]
//                [--arms server_b,server_c] [--tasks T1,T2] [--repeats 2] [--model ID] [--resume] [--dry-run]
import { spawn } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { homedir } from 'node:os';
import { dirname, join, relative, resolve, isAbsolute } from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  DEFAULT_MODEL, armFingerprint, verifyCorpus, buildMcpConfig, dataRoot, fingerprintMismatch, isTransientFailure, listOption,
  parseArgs, quoteForCmd, renderPrompt, resolveArm, summarize, usedArms, validateArms,
} from './session.mjs';

const TOOL_DIR = dirname(fileURLToPath(import.meta.url));
const REPOSITORY_ROOT = resolve(TOOL_DIR, '..', '..');

function fail(message) {
  console.error(`error: ${message}`);
  process.exit(1);
}

// Same rules as benchlib.work_root: never a folder inside the repository except the ignored work/.
function workRoot(argument) {
  const root = resolve(argument || process.env.AGENT_BENCHMARK_WORK || join(homedir(), '.devprojex-agent-benchmark'));
  const inside = (parent) => { const rel = relative(parent, root); return rel === '' || (!rel.startsWith('..') && !isAbsolute(rel)); };
  if (inside(REPOSITORY_ROOT) && !inside(join(TOOL_DIR, 'work'))) fail(`the work folder ${root} is inside the repository`);
  return root;
}

function readJson(path, what) {
  try { return JSON.parse(readFileSync(path, 'utf8')); } catch (error) { fail(`cannot read ${what} ${path}: ${error.message}`); }
}

const options = (() => {
  try {
    return parseArgs(process.argv.slice(2), {
      'tasks-file': '', 'arms-file': '', work: '', arms: '', tasks: '', repeats: 2, parallel: 3, stagger: 20000,
      'max-turns': 40, model: DEFAULT_MODEL, timeout: 900000, resume: false, 'dry-run': false,
    });
  } catch (error) { fail(error.message); }
})();
if (!options['tasks-file'] || !options['arms-file']) fail('--tasks-file and --arms-file are required');

const work = workRoot(options.work);
const tasksData = readJson(resolve(options['tasks-file']), 'task file');
const arms = readJson(resolve(options['arms-file']), 'arms file');
const armErrors = validateArms(arms);
if (armErrors.length) fail(armErrors.join('\n  '));
const promptTemplate = readFileSync(join(TOOL_DIR, 'prompt.txt'), 'utf8');
const series = tasksData.series;
const results = join(work, 'results', series);

const selectedArms = listOption(options.arms).length ? listOption(options.arms) : Object.keys(arms);
for (const name of selectedArms) if (!arms[name]) fail(`arm ${name} is not in the arms file`);
const selectedTasks = tasksData.tasks.filter((task) => !listOption(options.tasks).length || listOption(options.tasks).includes(task.id));
const unknownTasks = listOption(options.tasks).filter((id) => !tasksData.tasks.some((task) => task.id === id));
if (unknownTasks.length) fail(`unknown tasks ${unknownTasks.join(', ')}`);

const resolved = {};
for (const name of selectedArms) {
  try { resolved[name] = resolveArm(work, name, arms[name]); } catch (error) { fail(error.message); }
}

// Fresh arm names, or an explicit resume of the very same arm.
const fingerprints = Object.fromEntries(selectedArms.map((name) =>
  [name, armFingerprint(resolved[name], options.model, options['max-turns'], promptTemplate)]));
const used = usedArms(results, selectedArms);
if (used.length && !options.resume) {
  fail(`arms ${used.join(', ')} already have results in ${results}. Give the new build a fresh arm name, `
    + 'or pass --resume to finish an interrupted run of the same arm.');
}
for (const name of used) {
  const recordPath = join(results, 'arms', `${name}.json`);
  const differs = existsSync(recordPath) ? fingerprintMismatch(readJson(recordPath, 'arm record'), fingerprints[name]) : ['record'];
  if (differs.length) fail(`${name} cannot be resumed: ${differs.join(', ')} differ from its recorded run`);
}

const corpusProblems = [...new Set(selectedTasks.map((task) => task.repo))]
  .map((repo) => verifyCorpus(work, repo)).filter(Boolean);
if (corpusProblems.length) fail(corpusProblems.join('\n  '));

const jobs = [];
for (const task of selectedTasks) {
  for (const server of selectedArms) {
    for (let repeat = 1; repeat <= options.repeats; repeat++) {
      const id = `${task.id}-${server}-r${repeat}`;
      const out = join(results, 'runs', id);
      if (existsSync(join(out, 'summary.json'))) continue;
      jobs.push({ id, task, server, product: arms[server].product, repeat, out });
    }
  }
}
console.log(`series ${series}, model ${options.model}, ${jobs.length} sessions, results in ${results}`);

function prepareJob(job) {
  const repoPath = join(work, 'corpora', job.task.repo);
  const dataPath = dataRoot(work, series, job.server, job.task.repo);
  const config = buildMcpConfig(job.server, resolved[job.server], repoPath, dataPath);
  const configPath = join(results, 'cfg', `${job.server}-${job.task.repo}.json`);
  return { repoPath, dataPath, config, configPath, prompt: renderPrompt(promptTemplate, repoPath, job.server, job.task.text) };
}

if (options['dry-run']) {
  for (const job of jobs) {
    const plan = prepareJob(job);
    console.log(`${job.id}: ${resolved[job.server].kind} ${resolved[job.server].product} on ${plan.repoPath}`);
  }
  if (jobs.length) console.log(JSON.stringify(prepareJob(jobs[0]).config, null, 1));
  console.log('dry run: nothing was started or written');
  process.exit(0);
}

mkdirSync(join(results, 'arms'), { recursive: true });
mkdirSync(join(results, 'cfg'), { recursive: true });
for (const name of selectedArms) {
  if (!used.includes(name)) {
    // A fresh arm starts from an empty index and cache.
    rmSync(join(work, 'data', series, name), { recursive: true, force: true });
    writeFileSync(join(results, 'arms', `${name}.json`), JSON.stringify(fingerprints[name], null, 1));
  }
}

function claudeArgs(plan) {
  const args = ['-p', '--mcp-config', plan.configPath, '--strict-mcp-config', '--tools', '',
    '--allowedTools', `mcp__${plan.server}__*`, '--max-turns', String(options['max-turns']), '--no-session-persistence',
    '--output-format', 'stream-json', '--verbose', '--model', options.model];
  return process.platform === 'win32' ? args.map(quoteForCmd) : args;
}

function runJob(job) {
  return new Promise((done) => {
    const plan = { ...prepareJob(job), server: job.server };
    mkdirSync(job.out, { recursive: true });
    mkdirSync(plan.dataPath, { recursive: true });
    writeFileSync(plan.configPath, JSON.stringify(plan.config, null, 1));
    writeFileSync(join(job.out, 'prompt.txt'), plan.prompt);
    const started = Date.now();
    // cwd = repository: Claude Code advertises its working directory as the MCP root, and a server
    // that prefers client roots would otherwise index the wrong folder.
    const child = spawn('claude', claudeArgs(plan), {
      cwd: plan.repoPath, shell: process.platform === 'win32', windowsHide: true,
      env: { ...process.env, CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC: '1' },
    });
    let stdout = '';
    let stderr = '';
    child.stdout.on('data', (chunk) => { stdout += chunk; });
    child.stderr.on('data', (chunk) => { stderr += chunk; });
    child.on('error', (error) => { stderr += `\nspawn failed: ${error.message}`; });
    child.stdin.on('error', () => {});
    child.stdin.end(plan.prompt);
    const killer = setTimeout(() => { try { child.kill(); } catch { /* already gone */ } }, options.timeout);
    child.on('close', (code) => {
      clearTimeout(killer);
      writeFileSync(join(job.out, 'stream.jsonl'), stdout);
      writeFileSync(join(job.out, 'stderr.txt'), stderr);
      const summary = summarize(job, stdout, code, Date.now() - started);
      const name = isTransientFailure(summary) ? `failed-${Date.now()}.json` : 'summary.json';
      writeFileSync(join(job.out, name), JSON.stringify(summary, null, 2));
      console.log(`${job.id}: exit=${code} turns=${summary.numTurns} calls=${summary.toolCalls.length} `
        + `cost=$${summary.usage.costUsd.toFixed(3)} ms=${summary.durationMs}${summary.error ? ` ERROR ${summary.error}` : ''}`);
      done(summary);
    });
  });
}

const queue = [...jobs];
const pause = (ms) => new Promise((wake) => setTimeout(wake, ms));
await Promise.all(Array.from({ length: Math.min(options.parallel, queue.length) }, async (_, index) => {
  await pause(index * options.stagger);
  while (queue.length) {
    await runJob(queue.shift());
    if (queue.length) await pause(options.stagger);
  }
}));
console.log('done');
