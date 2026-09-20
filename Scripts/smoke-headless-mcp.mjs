import { spawn } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { mkdirSync, rmSync, writeFileSync } from 'node:fs';
import { basename, dirname, join } from 'node:path';

const [command, root, ...rawPrefixArguments] = process.argv.slice(2);
if (!command || !root) {
  throw new Error('Usage: node smoke-headless-mcp.mjs <command> <root> [--live] [prefix arguments...]');
}

const liveIndex = rawPrefixArguments.indexOf('--live');
const live = liveIndex >= 0;
const prefixArguments = rawPrefixArguments.filter((_, index) => index !== liveIndex);
const probeRoot = join(root, 'McpSmoke');
const processTemp = join(dirname(root), `.devprojex-mcp-smoke-${randomUUID()}`);
mkdirSync(probeRoot, { recursive: true });
mkdirSync(processTemp, { recursive: true });
writeFileSync(join(probeRoot, 'Probe.txt'), 'artifactNeedle\n', 'utf8');
writeFileSync(join(probeRoot, 'Large.txt'), `large-marker\n${'x'.repeat(70_000)}`, 'utf8');

const child = spawn(
  command,
  [...prefixArguments, 'mcp', '--root', root, ...(live ? ['--live'] : [])],
  {
    shell: false,
    windowsHide: true,
    stdio: ['pipe', 'pipe', 'pipe'],
    env: {
      ...process.env,
      DEVPROJEX_INTERNAL_DATA_ROOT: processTemp,
      TEMP: processTemp,
      TMP: processTemp,
      TMPDIR: processTemp,
    },
  });
const childExited = new Promise((resolve) => child.once('exit', resolve));
let stderr = '';
let stdoutBuffer = '';
let nextId = 1;
const pending = new Map();

child.stderr.setEncoding('utf8');
child.stderr.on('data', (chunk) => { stderr += chunk; });
child.stdout.setEncoding('utf8');
child.stdout.on('data', (chunk) => {
  stdoutBuffer += chunk;
  while (true) {
    const newline = stdoutBuffer.indexOf('\n');
    if (newline < 0) break;
    const line = stdoutBuffer.slice(0, newline).trim();
    stdoutBuffer = stdoutBuffer.slice(newline + 1);
    if (!line) continue;
    const message = JSON.parse(line);
    if (message.id !== undefined && pending.has(message.id)) {
      const request = pending.get(message.id);
      pending.delete(message.id);
      if (message.error) request.reject(new Error(JSON.stringify(message.error)));
      else request.resolve(message.result);
    }
  }
});
child.on('error', (error) => {
  for (const request of pending.values()) request.reject(error);
  pending.clear();
});
child.on('exit', (code) => {
  if (pending.size === 0) return;
  const error = new Error(`MCP server exited ${code}. stderr: ${stderr}`);
  for (const request of pending.values()) request.reject(error);
  pending.clear();
});

function send(method, params) {
  const id = nextId++;
  const result = new Promise((resolve, reject) => pending.set(id, { resolve, reject }));
  child.stdin.write(`${JSON.stringify({ jsonrpc: '2.0', id, method, params })}\n`);
  return result;
}

function notify(method, params = {}) {
  child.stdin.write(`${JSON.stringify({ jsonrpc: '2.0', method, params })}\n`);
}

async function callTool(name, args = {}) {
  const result = await send('tools/call', { name, arguments: args });
  if (result?.isError) throw new Error(`${name} failed: ${toolText(result)}`);
  return result;
}

function toolText(result) {
  return (result?.content ?? [])
    .filter((item) => item.type === 'text')
    .map((item) => item.text)
    .join('\n');
}

const timeout = setTimeout(() => {
  child.kill();
}, 60_000);

try {
  const initialized = await send('initialize', {
    protocolVersion: '2025-06-18',
    capabilities: {},
    clientInfo: { name: 'devprojex-headless-smoke', version: '1.0.0' },
  });
  if (!initialized?.protocolVersion) throw new Error('MCP initialize response has no protocolVersion.');
  notify('notifications/initialized');

  const listed = toolText(await callTool('list_projects'));
  if (!listed.includes(basename(root))) {
    throw new Error(`list_projects did not return the configured root: ${listed}`);
  }
  const tree = toolText(await callTool('get_tree', { format: 'text' }));
  if (!tree.includes('Probe.txt')) throw new Error(`get_tree omitted the probe file: ${tree}`);
  const search = toolText(await callTool('search_project', {
    pattern: 'artifactNeedle',
    context_lines: 0,
    ignore_case: false,
  }));
  if (!search.includes('McpSmoke/Probe.txt')) throw new Error(`search_project missed the probe: ${search}`);
  const file = toolText(await callTool('get_file', { path: 'McpSmoke/Probe.txt' }));
  if (!file.includes('artifactNeedle')) throw new Error(`get_file missed the probe content: ${file}`);
  const packed = toolText(await callTool('pack_context', {
    paths: ['McpSmoke/Large.txt'],
    view: 'content',
    format: 'text',
  }));
  const packId = /Pack stored as '([^']+)'/.exec(packed)?.[1];
  if (!packId) throw new Error(`pack_context did not store the large pack: ${packed}`);
  const page = toolText(await callTool('read_pack', { pack_id: packId }));
  if (!page.includes('large-marker')) throw new Error(`read_pack omitted the marker: ${page}`);

  process.stdout.write(`MCP read workflow OK (${live ? 'live' : 'snapshot'}): ${initialized.serverInfo.name}\n`);
} finally {
  clearTimeout(timeout);
  child.stdin.end();
  const stopped = await Promise.race([
    childExited.then(() => true),
    new Promise((resolve) => setTimeout(() => resolve(false), 3_000)),
  ]);
  if (!stopped) {
    child.kill();
    await childExited;
  }
  rmSync(processTemp, { recursive: true, force: true });
}
