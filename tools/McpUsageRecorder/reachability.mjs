#!/usr/bin/env node
import { spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { createReadStream } from 'node:fs';
import { mkdtemp, mkdir, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { basename, dirname, extname, isAbsolute, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { analyzeReachability } from './lib/reachability-analysis.mjs';
import { startMcpReachabilityClient } from './lib/mcp-reachability-client.mjs';

const toolRoot = dirname(fileURLToPath(import.meta.url));
const repositoryRoot = resolve(toolRoot, '..', '..');
const options = parseArguments(process.argv.slice(2));
const registryPath = resolve(options.registry ?? join(toolRoot, 'oracles', 'reachability.json'));
const oraclePath = resolve(options.oracles ?? join(toolRoot, 'oracles', 'tasks.json'));
const outputPath = options.output ? resolve(options.output) : null;
const externalWorkspace = options.workspace ? resolve(options.workspace) : null;
const workspace = externalWorkspace ?? await mkdtemp(join(tmpdir(), 'devprojex-reachability-'));

try {
  const registryText = await readFile(registryPath, 'utf8');
  const oracleText = await readFile(oraclePath, 'utf8');
  const registry = JSON.parse(registryText);
  const oracles = JSON.parse(oracleText);
  const repositories = await prepareRepositories(registry.repositories, workspace, options.repositories);
  const serverPath = options.server
    ? resolve(options.server)
    : await publishServer(workspace);
  const result = await analyzeReachability(
    registry,
    oracles,
    repositories,
    (repository, root) => startMcpReachabilityClient(serverCommand(serverPath, root, workspace, repository.id)));
  result.productSha = (await capture('git', ['rev-parse', 'HEAD'], repositoryRoot)).trim();
  result.inputs = {
    registrySha256: sha256Text(registryText),
    oraclesSha256: sha256Text(oracleText),
    serverSha256: await sha256File(serverPath),
  };
  const json = `${JSON.stringify(result, null, 2)}\n`;
  if (outputPath) {
    await mkdir(dirname(outputPath), { recursive: true });
    await writeFile(outputPath, json, { encoding: 'utf8', flag: 'w' });
  } else {
    process.stdout.write(json);
  }
} catch (error) {
  process.stderr.write(`${error.message}\n`);
  process.exitCode = 1;
} finally {
  if (!externalWorkspace && options.keepWorkspace !== true)
    await rm(workspace, { recursive: true, force: true });
}

async function prepareRepositories(repositories, workspaceRoot, suppliedRoot) {
  const root = suppliedRoot ? resolve(suppliedRoot) : join(workspaceRoot, 'repositories');
  await mkdir(root, { recursive: true });
  const prepared = new Map();
  for (const repository of repositories) {
    const target = join(root, repository.id);
    if (!suppliedRoot) {
      await run('git', ['clone', '--filter=blob:none', '--no-checkout', repository.url, target], repositoryRoot);
      await run('git', ['checkout', '--detach', repository.commit], target);
    }
    const actual = (await capture('git', ['rev-parse', 'HEAD'], target)).trim();
    if (actual !== repository.commit)
      throw new Error(`Repository '${repository.id}' is at ${actual}, expected ${repository.commit}.`);
    prepared.set(repository.id, target);
  }
  return prepared;
}

async function publishServer(workspaceRoot) {
  const output = join(workspaceRoot, 'server');
  await mkdir(output, { recursive: true });
  await run('dotnet', [
    'publish',
    join(repositoryRoot, 'Apps', 'TerminalHost', 'DevProjex.TerminalHost.csproj'),
    '-c', 'Release', '-o', output, '--nologo',
  ], repositoryRoot);
  return join(output, process.platform === 'win32' ? 'devprojex.exe' : 'devprojex');
}

function serverCommand(serverPath, projectRoot, workspaceRoot, repositoryId) {
  const isDll = extname(serverPath).toLowerCase() === '.dll';
  return {
    command: isDll ? 'dotnet' : serverPath,
    args: [...(isDll ? [serverPath] : []), 'mcp', '--root', projectRoot],
    cwd: projectRoot,
    env: { DEVPROJEX_INTERNAL_DATA_ROOT: join(workspaceRoot, 'data', repositoryId) },
  };
}

function parseArguments(values) {
  const parsed = {};
  for (let index = 0; index < values.length; index++) {
    const name = values[index];
    if (name === '--keep-workspace') {
      parsed.keepWorkspace = true;
      continue;
    }
    if (!['--registry', '--oracles', '--output', '--workspace', '--repositories', '--server'].includes(name))
      throw new Error(`Unknown option '${name}'.`);
    const value = values[++index];
    if (!value)
      throw new Error(`${name} requires a value.`);
    parsed[name.slice(2).replace(/-([a-z])/g, (_, letter) => letter.toUpperCase())] = value;
  }
  return parsed;
}

async function run(command, args, cwd) {
  await new Promise((resolvePromise, reject) => {
    const child = spawn(command, args, { cwd, stdio: 'inherit', windowsHide: true });
    child.once('error', reject);
    child.once('exit', code => code === 0
      ? resolvePromise()
      : reject(new Error(`${basename(command)} exited with code ${code}.`)));
  });
}

async function capture(command, args, cwd) {
  return new Promise((resolvePromise, reject) => {
    const child = spawn(command, args, { cwd, stdio: ['ignore', 'pipe', 'pipe'], windowsHide: true });
    let stdout = '';
    let stderr = '';
    child.stdout.setEncoding('utf8');
    child.stderr.setEncoding('utf8');
    child.stdout.on('data', chunk => { stdout += chunk; });
    child.stderr.on('data', chunk => { stderr += chunk; });
    child.once('error', reject);
    child.once('exit', code => code === 0
      ? resolvePromise(stdout)
      : reject(new Error(`${basename(command)} exited with code ${code}: ${stderr.trim()}`)));
  });
}

function sha256Text(value) {
  return createHash('sha256').update(value, 'utf8').digest('hex');
}

async function sha256File(path) {
  const hash = createHash('sha256');
  for await (const chunk of createReadStream(path))
    hash.update(chunk);
  return hash.digest('hex');
}
