#!/usr/bin/env node
import { spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { createReadStream } from 'node:fs';
import { mkdtemp, mkdir, readFile, readdir, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { basename, dirname, extname, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { startMcpReachabilityClient } from '../McpUsageRecorder/lib/mcp-reachability-client.mjs';
import {
  classifyExpectedRelations,
  compareRelated,
  mergeRelations,
  normalizeCliRelated,
  parseMcpRelated,
  resolveIncludeCandidates,
} from './lib/live-validation.mjs';

const toolRoot = dirname(fileURLToPath(import.meta.url));
const repositoryRoot = resolve(toolRoot, '..', '..');
const options = parseArguments(process.argv.slice(2));
const registryPath = resolve(options.registry ?? join(toolRoot, 'registry.json'));
const outputPath = resolve(options.output ?? join(toolRoot, 'results', `${new Date().toISOString().slice(0, 10)}.json`));
const externalWorkspace = options.workspace ? resolve(options.workspace) : null;
const workspace = externalWorkspace ?? await mkdtemp(join(tmpdir(), 'devprojex-dependency-live-'));

try {
  const registryText = await readFile(registryPath, 'utf8');
  const registry = JSON.parse(registryText);
  validateRegistry(registry);
  const productSha = (await capture('git', ['rev-parse', 'HEAD'], repositoryRoot)).stdout.trim().toLowerCase();
  const repositories = await prepareRepositories(registry.repositories, workspace, options.repositories);
  const scanner = await publish(
    join(repositoryRoot, 'tools', 'DependencyLiveValidation', 'DependencyLiveValidation.csproj'),
    join(workspace, 'scanner'));
  const server = await publish(
    join(repositoryRoot, 'Apps', 'TerminalHost', 'DevProjex.TerminalHost.csproj'),
    join(workspace, 'server'));

  const reports = [];
  for (const repository of registry.repositories) {
    const root = repositories.get(repository.id);
    const scanPath = join(workspace, `${repository.id}-scan.json`);
    const checkedRepository = repository.category === 'native'
      ? { ...repository, samples: await attachExplicitIncludeRelations(repository, root) }
      : repository;
    const probesPath = join(workspace, `${repository.id}-probes.json`);
    const probes = checkedRepository.samples.flatMap(sample => sample.expectedRelations.map(relation => ({
      seed: sample.path,
      direction: relation.direction,
      source: relation.direction === 'dependencies' ? sample.path : relation.path,
      target: relation.direction === 'dependencies' ? relation.path : sample.path,
      reference: relation.reference,
      line: relation.line ?? 0,
      evidence: relation.evidence,
    })));
    await writeFile(probesPath, `${JSON.stringify(probes, null, 2)}\n`);
    const scannerArguments = [
      '--root', root,
      '--languages', repository.languages.join(','),
      '--samples', repository.samples.map(sample => sample.path).join(';'),
      '--probes', probesPath,
      '--output', scanPath,
    ];
    if (repository.excludeDirectories?.length > 0)
      scannerArguments.push('--exclude-directories', repository.excludeDirectories.join(';'));
    await runExecutable(scanner, scannerArguments, repositoryRoot);
    const scan = JSON.parse(await readFile(scanPath, 'utf8'));
    const samples = repository.category === 'native'
      ? await validateNativeSamples(checkedRepository, root, scan, server, workspace, options.allowUnchecked)
      : [];
    reports.push({
      id: repository.id,
      category: repository.category,
      commit: repository.commit,
      languages: repository.languages,
      scan: repository.category === 'native' ? scan : compactScan(scan),
      samples,
    });
  }

  const result = {
    schemaVersion: 1,
    productSha,
    inputs: {
      registrySha256: sha256Text(registryText),
      scannerSha256: await sha256File(scanner.path),
      serverSha256: await sha256File(server.path),
    },
    repositories: reports,
    summary: summarize(reports),
  };
  await mkdir(dirname(outputPath), { recursive: true });
  await writeFile(outputPath, `${JSON.stringify(result, null, 2)}\n`);
  process.stdout.write(`${outputPath}\n`);
} catch (error) {
  process.stderr.write(`${error.stack ?? error.message}\n`);
  process.exitCode = 1;
} finally {
  if (!externalWorkspace && options.keepWorkspace !== true)
    await rm(workspace, { recursive: true, force: true, maxRetries: 3 });
}

async function validateNativeSamples(repository, root, scan, server, workspaceRoot, allowUnchecked) {
  const dataRoot = join(workspaceRoot, 'data', repository.id);
  const mcp = await startMcpReachabilityClient(serverCommand(server, root, dataRoot), 300_000);
  try {
    const reports = [];
    for (const declared of repository.samples) {
      const cliProcess = await captureExecutable(server, [
        'related', declared.path,
        '--project', root,
        '--direction', 'both',
        '--format', 'json',
        '--progress', 'never',
        '--color', 'never',
        '--plain',
      ], root, { DEVPROJEX_INTERNAL_DATA_ROOT: join(dataRoot, 'cli') });
      const cli = normalizeCliRelated(JSON.parse(cliProcess.stdout))[0];
      const mcpCall = await mcp.callAndPage('related_files', { path: declared.path, direction: 'both' });
      if (mcpCall.isError)
        throw new Error(`${repository.id}:${declared.path}: MCP related_files returned an error.`);
      const mcpResult = parseMcpRelated(mcpCall.allText);
      mcpResult.seed ??= declared.path;
      const parity = compareRelated(cli, mcpResult);
      if (!parity.equal)
        throw new Error(`${repository.id}:${declared.path}: CLI and MCP related results differ.\n${JSON.stringify(parity, null, 2)}`);
      const scanSample = scan.samples.find(sample => sample.seed === declared.path);
      if (!scanSample)
        throw new Error(`${repository.id}:${declared.path}: scanner did not return the declared sample.`);
      const expectedRelations = scan.relationProbes.filter(probe => probe.seed === declared.path);
      const sourceCheck = classifyExpectedRelations({ expectedRelations, evidence: scanSample.evidence }, cli);
      if (!allowUnchecked && sourceCheck.falseEdges.length > 0)
        throw new Error(`${repository.id}:${declared.path}: resolved edges are not present in the checked relation list: ${sourceCheck.falseEdges.join(', ')}`);
      reports.push({ path: declared.path, cliMcpEqual: true, related: cli, sourceCheck, facts: scanSample.facts });
    }
    return reports;
  } finally {
    await mcp.close();
  }
}

async function attachExplicitIncludeRelations(repository, root) {
  const excluded = new Set(repository.excludeDirectories ?? ['node_modules', 'bin', 'obj', 'artifacts', 'build', '.venv', 'venv']);
  const files = await enumerateNativeFiles(root, excluded);
  const paths = new Set(files.map(file => file.relativePath));
  const samples = new Set(repository.samples.map(sample => sample.path));
  const relations = new Map(repository.samples.map(sample => [sample.path, []]));
  for (const file of files) {
    const source = await readFile(file.fullPath, 'utf8');
    const lines = source.split(/\r\n|\r|\n/);
    for (let index = 0; index < lines.length; index++) {
      const match = /^\s*#\s*include\s*([<"])([^>"]+)[>"]/.exec(lines[index]);
      if (!match) continue;
      const specifier = match[2].replaceAll('\\', '/');
      const candidates = resolveIncludeCandidates(file.relativePath, specifier, match[1] === '"', paths);
      if (candidates.length !== 1) continue;
      const target = candidates[0];
      const evidence = `${file.relativePath}:${index + 1}: ${lines[index].trim()}`;
      if (samples.has(file.relativePath)) relations.get(file.relativePath).push({
        direction: 'dependencies', path: target, reference: specifier, line: index + 1, evidence,
      });
      if (samples.has(target)) relations.get(target).push({
        direction: 'dependents', path: file.relativePath, reference: specifier, line: index + 1, evidence,
      });
    }
  }
  return repository.samples.map(sample => ({
    ...sample,
    expectedRelations: mergeRelations(sample.expectedRelations ?? [], relations.get(sample.path) ?? []),
  }));
}

async function enumerateNativeFiles(root, excluded) {
  const result = [];
  const pending = [root];
  const extensions = new Set(['.c', '.h', '.cc', '.cpp', '.cxx', '.hh', '.hpp', '.hxx']);
  while (pending.length > 0) {
    const directory = pending.pop();
    const entries = await readdir(directory, { withFileTypes: true });
    entries.sort((left, right) => right.name.localeCompare(left.name, 'en'));
    for (const entry of entries) {
      const fullPath = join(directory, entry.name);
      if (entry.isDirectory()) {
        if (entry.name !== '.git' && !excluded.has(entry.name)) pending.push(fullPath);
      } else if (entry.isFile() && extensions.has(extname(entry.name).toLowerCase())) {
        result.push({ fullPath, relativePath: relative(root, fullPath).replaceAll('\\', '/') });
      }
    }
  }
  result.sort((left, right) => left.relativePath.localeCompare(right.relativePath, 'en'));
  return result;
}

function compactScan(scan) {
  return {
    manifestFiles: scan.manifestFiles,
    languages: scan.languages,
    files: scan.files,
    edges: scan.edges,
    failures: scan.failures,
  };
}

function summarize(reports) {
  const native = reports.filter(report => report.category === 'native');
  const samples = native.flatMap(report => report.samples);
  return {
    repositories: reports.length,
    nativeRepositories: native.length,
    sourceFiles: reports.reduce((sum, report) => sum + report.scan.files.total, 0),
    supportedFiles: reports.reduce((sum, report) => sum + report.scan.files.supported, 0),
    failedFiles: reports.reduce((sum, report) => sum + report.scan.files.failed, 0),
    partialFiles: reports.reduce((sum, report) => sum + report.scan.files.partial, 0),
    checkedSamples: samples.length,
    checkedRelations: samples.reduce((sum, sample) => sum + sample.sourceCheck.relations.length, 0),
    confirmedRelations: samples.reduce((sum, sample) => sum + sample.sourceCheck.confirmed, 0),
    missedHonestly: samples.reduce((sum, sample) => sum + sample.sourceCheck.missedHonestly, 0),
    missedSilently: samples.reduce((sum, sample) => sum + sample.sourceCheck.missedSilently, 0),
    falseEdges: samples.reduce((sum, sample) => sum + sample.sourceCheck.falseEdges.length, 0),
  };
}

async function prepareRepositories(declarations, workspaceRoot, suppliedRoot) {
  const root = suppliedRoot ? resolve(suppliedRoot) : join(workspaceRoot, 'repositories');
  await mkdir(root, { recursive: true });
  const result = new Map();
  for (const repository of declarations) {
    const target = join(root, repository.id);
    if (!suppliedRoot) {
      await run('git', ['clone', '--filter=blob:none', '--no-checkout', repository.url, target], repositoryRoot);
      await run('git', ['checkout', '--detach', repository.commit], target);
    }
    const actual = (await capture('git', ['rev-parse', 'HEAD'], target)).stdout.trim().toLowerCase();
    if (actual !== repository.commit.toLowerCase())
      throw new Error(`${repository.id} is at ${actual}, expected ${repository.commit}.`);
    for (const sample of repository.samples) {
      const fullPath = resolve(target, ...sample.path.split('/'));
      const inside = fullPath.startsWith(`${resolve(target)}${process.platform === 'win32' ? '\\' : '/'}`);
      if (!inside || !(await exists(fullPath)))
        throw new Error(`${repository.id}:${sample.path} is not a file at the pinned commit.`);
    }
    result.set(repository.id, target);
  }
  return result;
}

async function publish(project, output) {
  await mkdir(output, { recursive: true });
  await run('dotnet', ['publish', project, '-c', 'Release', '-m:1', '-o', output, '--nologo'], repositoryRoot);
  const name = project.endsWith('TerminalHost.csproj') ? 'devprojex' : 'DependencyLiveValidation';
  const exe = join(output, process.platform === 'win32' ? `${name}.exe` : name);
  const dll = join(output, `${name}.dll`);
  return await exists(exe) ? { path: exe, command: exe, prefix: [] } : { path: dll, command: 'dotnet', prefix: [dll] };
}

function serverCommand(server, root, dataRoot) {
  return {
    command: server.command,
    args: [...server.prefix, 'mcp', '--root', root],
    cwd: root,
    env: { DEVPROJEX_INTERNAL_DATA_ROOT: dataRoot },
  };
}

async function runExecutable(executable, args, cwd) {
  await run(executable.command, [...executable.prefix, ...args], cwd);
}

async function captureExecutable(executable, args, cwd, environment) {
  const result = await capture(executable.command, [...executable.prefix, ...args], cwd, environment);
  if (result.code !== 0)
    throw new Error(`${basename(executable.path)} exited with code ${result.code}: ${result.stderr.trim()}`);
  return result;
}

function validateRegistry(registry) {
  if (registry?.schemaVersion !== 1 || !Array.isArray(registry.repositories) || registry.repositories.length === 0)
    throw new Error('Registry must contain repositories under schema version 1.');
  const ids = new Set();
  for (const repository of registry.repositories) {
    if (!repository.id || ids.has(repository.id)) throw new Error('Repository ids must be present and unique.');
    ids.add(repository.id);
    if (!/^[0-9a-f]{40}$/i.test(repository.commit)) throw new Error(`${repository.id} needs a full commit SHA.`);
    if (!Array.isArray(repository.languages) || repository.languages.length === 0) throw new Error(`${repository.id} needs languages.`);
    if (!Array.isArray(repository.samples) || repository.samples.length === 0) throw new Error(`${repository.id} needs samples.`);
  }
}

function parseArguments(values) {
  const parsed = {};
  for (let index = 0; index < values.length; index++) {
    const name = values[index];
    if (name === '--keep-workspace' || name === '--allow-unchecked') {
      parsed[name.slice(2).replace(/-([a-z])/g, (_, letter) => letter.toUpperCase())] = true;
      continue;
    }
    if (!['--registry', '--output', '--workspace', '--repositories'].includes(name))
      throw new Error(`Unknown option '${name}'.`);
    const value = values[++index];
    if (!value) throw new Error(`${name} requires a value.`);
    parsed[name.slice(2)] = value;
  }
  return parsed;
}

async function run(command, args, cwd) {
  const result = await capture(command, args, cwd, {}, true);
  if (result.code !== 0)
    throw new Error(`${basename(command)} exited with code ${result.code}: ${result.stderr.trim()}`);
}

async function capture(command, args, cwd, environment = {}, inherit = false) {
  return new Promise((resolvePromise, reject) => {
    const child = spawn(command, args, {
      cwd,
      env: { ...process.env, ...environment },
      stdio: inherit ? ['ignore', 'inherit', 'pipe'] : ['ignore', 'pipe', 'pipe'],
      windowsHide: true,
    });
    let stdout = '';
    let stderr = '';
    if (child.stdout) {
      child.stdout.setEncoding('utf8');
      child.stdout.on('data', chunk => { stdout += chunk; });
    }
    child.stderr.setEncoding('utf8');
    child.stderr.on('data', chunk => { stderr += chunk; });
    child.once('error', reject);
    child.once('exit', code => resolvePromise({ code, stdout, stderr }));
  });
}

async function exists(path) {
  try {
    const { stat } = await import('node:fs/promises');
    return (await stat(path)).isFile();
  } catch {
    return false;
  }
}

function sha256Text(value) {
  return createHash('sha256').update(value, 'utf8').digest('hex');
}

async function sha256File(path) {
  const hash = createHash('sha256');
  for await (const chunk of createReadStream(path)) hash.update(chunk);
  return hash.digest('hex');
}
