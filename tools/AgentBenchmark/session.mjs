// Pure parts of the runner: argument parsing, arm checks, MCP configs, prompts and session summaries.
// Nothing here starts a process, so tests can cover it without the claude CLI.
import { spawnSync } from 'node:child_process';
import { existsSync, readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { CODEX_MODEL } from './codex.mjs';

export const DEFAULT_MODEL = 'claude-haiku-4-5-20251001';
export const ARM_NAME = /^server_[a-z0-9]+$/;

export function armAgent(arm) {
  return arm.agent ?? 'claude';
}

export function armModel(arm, override = '') {
  return override || arm.model || (armAgent(arm) === 'codex' ? CODEX_MODEL : DEFAULT_MODEL);
}

export function agentSeries(series, agent) {
  return agent === 'codex' ? `${series}-codex` : series;
}

export function countWorktreeFiles(root) {
  let total = 0;
  const pending = [root];
  while (pending.length) {
    const folder = pending.pop();
    for (const entry of readdirSync(folder, { withFileTypes: true })) {
      if (entry.isDirectory()) {
        if (entry.name !== '.git') pending.push(join(folder, entry.name));
      } else {
        total++; // files and symbolic links alike, as git tracks both
      }
    }
  }
  return total;
}

// Checks a prepared clone against its stand manifest, as benchlib.verify_corpus does. A clone
// emptied by a temp-folder cleanup once ran a whole series on an empty project without an error.
// git log reads HEAD without refreshing the index of the stale-index fixture.
export function verifyCorpus(work, repo) {
  const manifestPath = join(work, 'corpora', `${repo}.stand.json`);
  if (!existsSync(manifestPath)) return `${repo}: not prepared; run prepare_corpora.py`;
  const manifest = JSON.parse(readFileSync(manifestPath, 'utf8'));
  const root = join(work, 'corpora', repo);
  const log = spawnSync('git', ['-C', root, 'log', '-1', '--format=%H'], { encoding: 'utf8' });
  const head = log.status === 0 ? log.stdout.trim() : null;
  if (head !== manifest.head) return `${repo}: git log -1 gives ${head || 'nothing'}, expected ${manifest.head}`;
  const files = existsSync(root) ? countWorktreeFiles(root) : 0;
  if (files !== manifest.files) return `${repo}: ${files} files on disk, expected ${manifest.files}; the clone was changed or emptied`;
  return null;
}

export function parseArgs(argv, defaults) {
  const options = { ...defaults };
  for (let i = 0; i < argv.length; i++) {
    const token = argv[i];
    if (!token.startsWith('--')) throw new Error(`unexpected argument ${token}`);
    const name = token.slice(2);
    if (!(name in defaults)) throw new Error(`unknown option --${name}`);
    if (typeof defaults[name] === 'boolean') { options[name] = true; continue; }
    const value = argv[++i];
    if (value === undefined || value.startsWith('--')) throw new Error(`--${name} needs a value`);
    options[name] = typeof defaults[name] === 'number' ? Number(value) : value;
    if (Number.isNaN(options[name])) throw new Error(`--${name} must be a number`);
  }
  return options;
}

export function listOption(value) {
  return String(value ?? '').split(',').map((s) => s.trim()).filter(Boolean);
}

// Arms file: { "server_b": { "product": "v5.2.2 a194709a", "host": "v522" },
//              "server_c": { "product": "Repomix 1.18.1", "repomix": "1.18.1" } }
export function validateArms(arms) {
  const errors = [];
  if (!arms || typeof arms !== 'object' || Array.isArray(arms)) return ['the arms file must hold one JSON object'];
  for (const [name, arm] of Object.entries(arms)) {
    if (!ARM_NAME.test(name)) errors.push(`${name}: arm names must look like server_b, so the agent and the judge cannot tell the product`);
    if (!arm?.product) errors.push(`${name}: product is missing`);
    if (Boolean(arm?.host) === Boolean(arm?.repomix)) errors.push(`${name}: set exactly one of host or repomix`);
    if (arm && !['claude', 'codex'].includes(armAgent(arm))) errors.push(`${name}: agent must be claude or codex`);
  }
  return errors;
}

// Resolves an arm to the command it starts; throws when the host or Repomix is not installed.
export function resolveArm(work, name, arm) {
  if (arm.host) {
    const recordPath = join(work, 'hosts', arm.host, 'host.json');
    if (!existsSync(recordPath)) throw new Error(`${name}: host ${arm.host} is not built; run build_hosts.py`);
    const record = JSON.parse(readFileSync(recordPath, 'utf8'));
    const executable = join(work, 'hosts', arm.host, record.executable);
    if (!existsSync(executable)) throw new Error(`${name}: ${executable} is missing; build the host again`);
    return { kind: 'devprojex', product: arm.product, host: arm.host, commit: record.commit, executable };
  }
  const script = join(work, 'repomix', arm.repomix, 'node_modules', 'repomix', 'bin', 'repomix.cjs');
  if (!existsSync(script)) throw new Error(`${name}: Repomix ${arm.repomix} is not installed; run build_hosts.py --repomix ${arm.repomix}`);
  return { kind: 'repomix', product: arm.product, version: arm.repomix, executable: script };
}

// One data root per arm and repository: two arms never share an index or a cache.
export function dataRoot(work, series, armName, repo) {
  return join(work, 'data', series, armName, repo);
}

export function buildMcpConfig(armName, resolved, repoPath, dataPath) {
  if (resolved.kind === 'repomix') {
    return { mcpServers: { [armName]: { command: 'node', args: [resolved.executable, '--mcp'] } } };
  }
  return {
    mcpServers: {
      [armName]: {
        command: resolved.executable,
        args: ['mcp', '--root', repoPath],
        env: { DEVPROJEX_INTERNAL_DATA_ROOT: dataPath },
      },
    },
  };
}

export function renderPrompt(template, repoPath, armName, taskText) {
  return template.replaceAll('<REPO_PATH>', repoPath).replaceAll('<SERVER>', armName).replaceAll('<TASK_TEXT>', taskText);
}

// What identifies an arm's results. A resumed arm must match it exactly.
export function armFingerprint(resolved, model, maxTurns, promptTemplate) {
  return {
    product: resolved.product, kind: resolved.kind, host: resolved.host ?? null, commit: resolved.commit ?? null,
    repomix: resolved.version ?? null, model, maxTurns: String(maxTurns), prompt: promptTemplate,
  };
}

// Returns the arms that already have results in this series folder. Reusing an arm name once let
// one build's sessions be counted as another's, so a used name is an error unless --resume
// continues the very same arm.
export function usedArms(resultsDir, armNames) {
  const runs = join(resultsDir, 'runs');
  const records = join(resultsDir, 'arms');
  const used = new Set();
  for (const name of armNames) {
    if (existsSync(join(records, `${name}.json`))) used.add(name);
  }
  if (existsSync(runs)) {
    for (const entry of readdirSync(runs)) {
      for (const name of armNames) {
        if (entry.includes(`-${name}-r`)) used.add(name);
      }
    }
  }
  return [...used].sort();
}

export function fingerprintMismatch(recorded, current) {
  return Object.keys(current).filter((key) => JSON.stringify(recorded?.[key]) !== JSON.stringify(current[key]));
}

export function summarize(job, stdout, exitCode, wallMs) {
  const lines = stdout.split(/\r?\n/).filter((line) => line.startsWith('{'));
  const toolCalls = [];
  const pendingById = new Map();
  let result = null;
  let finalText = '';
  for (const line of lines) {
    let event;
    try { event = JSON.parse(line); } catch { continue; }
    if (event.type === 'assistant' && event.message?.content) {
      for (const block of event.message.content) {
        if (block.type === 'tool_use') {
          const call = { name: block.name, input: block.input, id: block.id, responseChars: 0, isError: false };
          toolCalls.push(call);
          pendingById.set(block.id, call);
        } else if (block.type === 'text') {
          finalText = block.text;
        }
      }
    } else if (event.type === 'user' && Array.isArray(event.message?.content)) {
      for (const block of event.message.content) {
        if (block.type !== 'tool_result') continue;
        const call = pendingById.get(block.tool_use_id);
        if (!call) continue;
        const text = typeof block.content === 'string' ? block.content
          : Array.isArray(block.content) ? block.content.map((c) => c.text ?? '').join('') : JSON.stringify(block.content ?? '');
        call.responseChars = text.length;
        call.isError = Boolean(block.is_error);
      }
    } else if (event.type === 'result') {
      result = event;
    }
  }
  const usage = { input: 0, output: 0, cacheRead: 0, cacheCreation: 0, costUsd: 0, model: '' };
  for (const [model, u] of Object.entries(result?.modelUsage ?? {})) {
    usage.input += u.inputTokens ?? 0;
    usage.output += u.outputTokens ?? 0;
    usage.cacheRead += u.cacheReadInputTokens ?? 0;
    usage.cacheCreation += u.cacheCreationInputTokens ?? 0;
    usage.costUsd += u.costUSD ?? 0;
    if ((u.outputTokens ?? 0) > 50) usage.model = model;
  }
  // New tokens entering the window; the reports use input + cacheRead + cacheCreation instead.
  usage.totalContextTokens = usage.input + usage.cacheCreation + usage.output;
  let error = null;
  if (!result) error = 'no result event';
  else if (result.is_error) error = result.api_error_status ? `api ${result.api_error_status}: ${String(result.result).slice(0, 120)}` : (result.subtype || 'error');
  return {
    id: job.id, task: job.task.id, repo: job.task.repo, server: job.server, product: job.product, repeat: job.repeat,
    exitCode, durationMs: result?.duration_ms ?? wallMs, wallMs, numTurns: result?.num_turns ?? null,
    error, apiErrorStatus: result?.api_error_status ?? null,
    usage, toolCalls, toolResponseChars: toolCalls.reduce((sum, call) => sum + call.responseChars, 0),
    answer: result?.result ?? finalText,
  };
}

// An API-level failure (rate limit, network) is not a benchmark result: it is kept aside and retried.
export function isTransientFailure(summary) {
  return Boolean(summary.error) && Boolean(summary.apiErrorStatus || (summary.toolCalls.length === 0 && summary.usage.totalContextTokens === 0));
}

// Quotes one argument for cmd.exe, which is needed to start claude.cmd on Windows.
export function quoteForCmd(value) {
  const text = String(value);
  if (text === '') return '""';
  return /[\s"&|<>^()%!]/.test(text) ? `"${text.replaceAll('"', '\\"')}"` : text;
}
