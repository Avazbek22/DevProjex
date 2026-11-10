import { spawn, spawnSync } from 'node:child_process';
import { copyFileSync, existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { homedir } from 'node:os';
import { dirname, join, resolve } from 'node:path';

export const CODEX_MODEL = 'gpt-6-luna';
export const CODEX_PRICING = Object.freeze({
  model: CODEX_MODEL, input: 0.10, cacheRead: 0.01, cacheCreation: 0.125, output: 0.50,
  unit: 'USD per million tokens', source: 'https://developers.openai.com/api/docs/models/gpt-6-luna',
  checked: '2026-11-10',
});

export function codexPricing(model, configured) {
  const pricing = configured ?? (model === CODEX_MODEL ? CODEX_PRICING : null);
  if (!pricing || pricing.model !== model || !pricing.source ||
      !['input', 'cacheRead', 'output'].every((key) => Number.isFinite(pricing[key]) && pricing[key] >= 0) ||
      (pricing.cacheCreation !== undefined && (!Number.isFinite(pricing.cacheCreation) || pricing.cacheCreation < 0))) {
    throw new Error(`Codex model ${model} needs matching pricing with input, cacheRead, output and source`);
  }
  return { ...pricing };
}

export function budgetStop(summary, spent, limit, firstSession, plannedSessions) {
  if (summary.isolation?.status !== 'passed') return 'Codex isolation failed';
  if (summary.error) return 'Codex session failed';
  if (!limit) return null;
  if (!summary.usage.costComplete) return 'Codex usage is incomplete; remaining cost cannot be established';
  if (spent >= limit || (firstSession && spent + summary.usage.costUsd * (plannedSessions - 1) > limit)) return 'Codex budget reached or projected to exceed the limit';
  return null;
}

export function restrictedCatalog(catalog, model) {
  const selected = catalog.models?.find((entry) => entry.slug === model);
  if (!selected) throw new Error(`Codex does not list model ${model}`);
  // Model metadata overrides feature flags; native execution stays absent while MCP calls remain direct.
  return { models: [{ ...selected, tool_mode: 'direct', shell_type: 'disabled', apply_patch_tool_type: null,
    node_repl_disabled: true, experimental_supported_tools: [], supports_search_tool: false, include_skills_usage_instructions: false,
    include_plugin_usage_instructions: false, include_apps_usage_instructions: false }] };
}

function toml(value) {
  if (typeof value === 'string' || typeof value === 'boolean' || typeof value === 'number') return JSON.stringify(value);
  if (Array.isArray(value)) return `[${value.map(toml).join(', ')}]`;
  return `{ ${Object.entries(value).map(([key, item]) => `${JSON.stringify(key)} = ${toml(item)}`).join(', ')} }`;
}

export function codexConfiguration(mcp, repoPath, catalogPath) {
  const disabled = ['shell_tool', 'shell_snapshot', 'apps', 'plugins', 'remote_plugin', 'multi_agent', 'multi_agent_v2',
    'hooks', 'skill_search', 'skill_mcp_dependency_install', 'view_image', 'browser_use', 'browser_use_external',
    'in_app_browser', 'computer_use', 'image_generation', 'goals', 'memories', 'code_mode', 'code_mode_host',
    'sleep_tool', 'tool_suggest'];
  const values = {
    approval_policy: 'never', web_search: 'disabled', project_doc_max_bytes: 0, suppress_unstable_features_warning: true,
    cli_auth_credentials_store: 'file', model_catalog_json: catalogPath, model_reasoning_effort: 'low',
    tools: { update_plan: { enabled: false } },
    features: { ...Object.fromEntries(disabled.map((name) => [name, false])), skip_host_skill_discovery: true },
    mcp_servers: Object.fromEntries(Object.entries(mcp.mcpServers).map(([name, server]) => [name,
    { ...server, cwd: repoPath, required: true, startup_timeout_sec: 60, default_tools_approval_mode: 'approve' }])),
  };
  return Object.entries(values).map(([key, value]) => `${key} = ${toml(value)}`).join('\n') + '\n';
}

export function codexArguments(model) {
  return ['exec', '--json', '--ephemeral', '--ignore-rules', '--strict-config', '--skip-git-repo-check',
    '--sandbox', 'read-only', '--color', 'never', '--model', model, '-'];
}

export function codexLauncher(executable) {
  if (/\.[cm]?js$/i.test(executable)) return { command: process.execPath, prefix: [executable] };
  if (process.platform !== 'win32' || /\.exe$/i.test(executable)) return { command: executable, prefix: [] };
  const paths = spawnSync('where.exe', [executable], { encoding: 'utf8', windowsHide: true });
  for (const entry of (paths.stdout ?? '').trim().split(/\r?\n/)) {
    if (/\.exe$/i.test(entry) && existsSync(entry)) return { command: entry, prefix: [] };
    const script = join(dirname(entry), 'node_modules', '@openai', 'codex', 'bin', 'codex.js');
    if (existsSync(script)) return { command: process.execPath, prefix: [script] };
  }
  throw new Error('Cannot resolve the Codex npm launcher; pass --codex-bin with the native executable path');
}

export function summarizeCodex(job, stdout, exitCode, wallMs, pricing, stderr = '') {
  const calls = new Map();
  const violations = new Set();
  for (const match of stderr.matchAll(/unsupported call:\s*([^\r\n]+)/g)) violations.add(`unsupported tool: ${match[1]}`);
  const usage = { input: 0, output: 0, cacheRead: 0, cacheCreation: 0, costUsd: 0, model: job.model,
    costSource: 'calculated', pricing };
  let answer = '';
  let turns = 0;
  let completed = false;
  let error = null;
  for (const line of stdout.split(/\r?\n/).filter(Boolean)) {
    let event;
    try { event = JSON.parse(line); } catch { violations.add('unparseable event'); continue; }
    if (event.type === 'turn.completed') {
      const tokens = event.usage;
      const cacheWrite = tokens?.cache_write_input_tokens ?? 0;
      if (!tokens || !['input_tokens', 'output_tokens', 'cached_input_tokens'].every((key) => Number.isFinite(tokens[key]) && tokens[key] >= 0) ||
          !Number.isFinite(cacheWrite) || cacheWrite < 0 || tokens.cached_input_tokens + cacheWrite > tokens.input_tokens ||
          (cacheWrite > 0 && !Number.isFinite(pricing.cacheCreation))) {
        error = 'missing or invalid Codex usage';
        continue;
      }
      // The CLI's input total already includes cached reads and writes.
      usage.input += tokens.input_tokens - tokens.cached_input_tokens - cacheWrite;
      usage.cacheRead += tokens.cached_input_tokens;
      usage.cacheCreation += cacheWrite;
      usage.output += tokens.output_tokens;
      turns++;
      completed = true;
    } else if (event.type === 'turn.failed' || event.type === 'error') {
      error = event.error?.message ?? event.message ?? 'Codex turn failed';
    } else if (['item.started', 'item.updated', 'item.completed'].includes(event.type)) {
      const item = event.item ?? {};
      if (item.type === 'mcp_tool_call') {
        if (typeof item.id !== 'string' || !item.id || typeof item.tool !== 'string' || !item.tool) {
          violations.add('invalid MCP item');
          continue;
        }
        if (item.server !== job.server) violations.add(`foreign MCP server: ${item.server ?? 'unknown'}`);
        const previous = calls.get(item.id);
        const call = previous ?? { id: item.id, name: `mcp__${item.server}__${item.tool}`,
          input: item.arguments ?? {}, responseChars: 0, isError: false };
        if (event.type === 'item.completed') {
          const content = item.result?.content;
          call.responseChars = Array.isArray(content) ? content.map((part) => part.text ?? JSON.stringify(part)).join('').length : 0;
          call.isError = item.status === 'failed' || Boolean(item.error) || Boolean(item.result?.isError);
          call.completed = true;
        }
        calls.set(item.id, call);
      } else if (item.type === 'agent_message') {
        if (event.type === 'item.completed') answer = item.text ?? '';
      } else if (item.type !== 'reasoning') {
        violations.add(`non-MCP item: ${item.type ?? 'unknown'}`);
      }
    } else if (!['thread.started', 'turn.started'].includes(event.type)) {
      violations.add(`unknown event: ${event.type ?? 'unknown'}`);
    }
  }
  const toolCalls = [...calls.values()];
  if (toolCalls.some((call) => !call.completed)) error ??= 'unfinished MCP call';
  if (!completed) error ??= 'no completed Codex turn';
  if (!answer) error ??= 'no final Codex answer';
  if (exitCode !== 0) error ??= `Codex exit ${exitCode}`;
  usage.totalContextTokens = usage.input + usage.cacheCreation + usage.output;
  usage.costUsd = (usage.input * pricing.input + usage.cacheRead * pricing.cacheRead +
    usage.cacheCreation * (pricing.cacheCreation ?? 0) + usage.output * pricing.output) / 1e6;
  usage.costComplete = completed && exitCode === 0 && !error;
  const isolation = { status: violations.size ? 'violated' : 'passed', violations: [...violations] };
  return { id: job.id, task: job.task.id, repo: job.task.repo, server: job.server, product: job.product, repeat: job.repeat,
    agent: 'codex', exitCode, durationMs: wallMs, wallMs, numTurns: turns, error, apiErrorStatus: null, usage,
    toolCalls, toolResponseChars: toolCalls.reduce((sum, call) => sum + call.responseChars, 0), answer, isolation,
    eligibleForMetrics: isolation.status === 'passed' && !error };
}

export function saveCodexSummary(out, job, stdout, exitCode, wallMs, pricing, stderr = '') {
  const summary = summarizeCodex(job, stdout, exitCode, wallMs, pricing, stderr);
  writeFileSync(join(out, 'summary.json'), JSON.stringify(summary, null, 2));
  return summary;
}

export function prepareCodexHome(folder, executable, model, mcp, repoPath, environment = process.env) {
  mkdirSync(folder, { recursive: true, mode: 0o700 });
  const source = join(environment.CODEX_HOME || join(homedir(), '.codex'), 'auth.json');
  if (!existsSync(source)) throw new Error('Codex needs an existing file-based sign-in; auth.json is not available');
  copyFileSync(source, join(folder, 'auth.json'));
  const env = { ...environment, CODEX_HOME: folder };
  for (const key of Object.keys(env)) {
    if (/^(OPENAI_API_KEY|OPENAI_BASE_URL|OPENAI_API_BASE|ANTHROPIC_|AZURE_OPENAI_)/.test(key)) delete env[key];
  }
  const launcher = codexLauncher(executable);
  const result = spawnSync(launcher.command, [...launcher.prefix, 'debug', 'models', '--bundled'], { env, windowsHide: true,
    encoding: 'utf8', timeout: 30000 });
  if (result.status !== 0) throw new Error(`Cannot read Codex model catalog: ${result.stderr || result.error?.message}`);
  const catalog = restrictedCatalog(JSON.parse(result.stdout), model);
  const catalogPath = join(folder, 'models.json');
  writeFileSync(catalogPath, JSON.stringify(catalog));
  writeFileSync(join(folder, 'config.toml'), codexConfiguration(mcp, repoPath, catalogPath));
  return env;
}

export async function executeCodex({ job, executable, out, mcp, repoPath, prompt, timeout, maxCalls, pricing, environment = process.env }) {
  const profile = resolve(out, 'codex-home');
  const cwd = resolve(out, 'workspace');
  mkdirSync(cwd, { recursive: true });
  const started = Date.now();
  let stdout = '';
  let stderr = '';
  try {
    const env = prepareCodexHome(profile, executable, job.model, mcp, repoPath, environment);
    return await new Promise((done, reject) => {
      const launcher = codexLauncher(executable);
      const child = spawn(launcher.command, [...launcher.prefix, ...codexArguments(job.model)], { cwd, env,
        windowsHide: true, detached: process.platform !== 'win32' });
      child.stdout.setEncoding('utf8');
      child.stderr.setEncoding('utf8');
      let pending = '';
      const calls = new Set();
      let stopped = false;
      let stopReason = null;
      function stop(reason) {
        if (stopped || !child.pid) return;
        stopped = true;
        stopReason = reason;
        stderr += `\n${reason}`;
        if (process.platform === 'win32') spawnSync('taskkill', ['/pid', String(child.pid), '/T', '/F'], { windowsHide: true });
        else { try { process.kill(-child.pid, 'SIGKILL'); } catch { /* already stopped */ } }
      }
      child.stdout.on('data', (chunk) => {
        stdout += chunk;
        pending += chunk;
        const lines = pending.split('\n');
        pending = lines.pop();
        for (const line of lines) {
          let event;
          try { event = JSON.parse(line); } catch { continue; }
          if (event.item?.type === 'mcp_tool_call') calls.add(event.item.id);
          if (calls.size > maxCalls) stop('Codex MCP call limit exceeded');
        }
      });
      child.stderr.on('data', (chunk) => { stderr += chunk; });
      child.on('error', (error) => { stderr += `\nspawn failed: ${error.message}`; });
      child.stdin.on('error', () => {});
      child.stdin.end(prompt);
      const killer = setTimeout(() => stop('Codex session timeout'), timeout);
      child.on('close', (code) => {
        clearTimeout(killer);
        try {
          writeFileSync(join(out, 'stream.jsonl'), stdout);
          writeFileSync(join(out, 'stderr.txt'), stderr);
          const summary = summarizeCodex(job, stdout, code, Date.now() - started, pricing, stderr);
          if (stopReason) {
            summary.error = stopReason;
            summary.eligibleForMetrics = false;
          }
          writeFileSync(join(out, 'summary.json'), JSON.stringify(summary, null, 2));
          done(summary);
        } catch (error) { reject(error); }
      });
    });
  } finally {
    rmSync(profile, { recursive: true, force: true });
  }
}
