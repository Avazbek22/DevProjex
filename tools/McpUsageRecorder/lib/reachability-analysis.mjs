import { readdir, readFile } from 'node:fs/promises';
import { resolve, sep } from 'node:path';

export const ReachabilityClass = Object.freeze({
  OneCall: 'one-call',
  ShortChain: 'two-or-three-call-chain',
  RequiresAnswerKnowledge: 'requires-knowledge-absent-from-task',
  Unreachable: 'unreachable',
});

export async function analyzeReachability(registry, oracles, repositories, clientFactory) {
  validateRegistry(registry);
  const oracleById = new Map(oracles.tasks.map(task => [task.id, task]));
  const repositoryById = new Map(registry.repositories.map(repository => [repository.id, repository]));
  const taskResults = [];
  const clients = new Map();
  const repositoryFiles = new Map();
  try {
    for (const taskSpec of registry.tasks) {
      const oracle = oracleById.get(taskSpec.id);
      if (!oracle)
        throw new Error(`Reachability task '${taskSpec.id}' has no oracle.`);
      const repository = repositoryById.get(taskSpec.repository);
      if (!repository)
        throw new Error(`Reachability task '${taskSpec.id}' names unknown repository '${taskSpec.repository}'.`);
      const repositoryRoot = repositories.get(repository.id);
      if (!repositoryRoot)
        throw new Error(`Repository '${repository.id}' has no checked-out root.`);
      let client = clients.get(repository.id);
      if (!client) {
        client = await clientFactory(repository, repositoryRoot);
        clients.set(repository.id, client);
      }
      let files = repositoryFiles.get(repository.id);
      if (!files) {
        files = await listRepositoryFiles(repositoryRoot);
        repositoryFiles.set(repository.id, files);
      }
      taskResults.push(await analyzeTask(taskSpec, oracle, repository, repositoryRoot, files, client));
    }
  } finally {
    for (const client of clients.values())
      await client.close();
  }

  return {
    schemaVersion: 1,
    repositories: registry.repositories.map(repository => ({
      id: repository.id,
      url: repository.url,
      commit: repository.commit,
    })),
    tasks: taskResults,
    files: aggregateUniqueFiles(taskResults),
    summary: summarize(taskResults),
  };
}

async function analyzeTask(spec, oracle, repository, repositoryRoot, repositoryFiles, client) {
  const requiredPaths = oracle.requiredPaths.map(normalizePath);
  const direct = new Map(requiredPaths.map(path => [path, []]));
  const searchRuns = [];
  const treeRuns = [];

  for (const query of spec.queries) {
    const result = await client.callAndPage('search_project', {
      pattern: query.pattern,
      ignore_case: query.ignoreCase ?? true,
      context_lines: 0,
      max_results: query.maxResults ?? 200,
    });
    const visibleFiles = extractVisibleRepositoryPaths(result.text, repositoryFiles);
    const continuedFiles = result.pages.map(page => extractVisibleRepositoryPaths(page, repositoryFiles));
    const visiblePaths = requiredPaths.filter(path => containsPath(result.text, path));
    const continuedPaths = requiredPaths.filter(path =>
      !visiblePaths.includes(path) && result.pages.some(page => containsPath(page, path)));
    for (const path of visiblePaths) {
      direct.get(path).push({
        surface: 'search_project',
        input: query.pattern,
        position: visibleFiles.indexOf(path) + 1,
      });
    }
    for (const path of continuedPaths) {
      const pageIndex = result.pages.findIndex(page => containsPath(page, path));
      direct.get(path).push({
        surface: 'search_project+read_pack',
        input: query.pattern,
        calls: pageIndex + 2,
        position: continuedFiles[pageIndex].indexOf(path) + 1,
      });
    }
    searchRuns.push({
      pattern: query.pattern,
      rationale: query.rationale,
      visiblePaths,
      continuedPaths,
      visibleFiles,
      continuedFiles,
      boundary: parseSearchBoundary(result.text),
      usedStoredContinuation: result.pages.length > 0,
      error: result.isError ? result.text : null,
    });
  }

  for (const tree of spec.treePatterns) {
    const result = await client.call('get_tree', {
      format: 'json',
      include_patterns: [tree.pattern],
    });
    const treePaths = extractTreePaths(result.text);
    const visiblePaths = requiredPaths.filter(path => treePaths.includes(path));
    for (const path of visiblePaths)
      direct.get(path).push({ surface: 'get_tree', input: tree.pattern, position: treePaths.indexOf(path) + 1 });
    treeRuns.push({
      pattern: tree.pattern,
      rationale: tree.rationale,
      visiblePaths,
      truncated: result.text.includes('[Tree truncated at '),
      error: result.isError ? result.text : null,
    });
  }

  const discoveredSeeds = spec.seeds
    .filter(seed => direct.get(normalizePath(seed.path))?.some(evidence =>
      evidence.surface === seed.discoveredBy.surface && evidence.input === seed.discoveredBy.input &&
      (evidence.calls ?? 1) === 1))
    .map(seed => normalizePath(seed.path));
  const related = await traverseRelated(client, discoveredSeeds, spec.maximumRelatedHops ?? 2);
  const results = [];

  for (const requiredPath of requiredPaths) {
    const absolutePath = containedPath(repositoryRoot, requiredPath);
    const source = await readOptionalText(absolutePath);
    const matchingQueries = spec.queries
      .filter(query => source !== null && safeRegex(query).test(source))
      .map(query => query.pattern);
    const exact = await client.call('get_file', { path: requiredPath, start_line: 1, end_line: 1 });
    const directEvidence = direct.get(requiredPath);
    const oneCallEvidence = directEvidence.filter(evidence => (evidence.calls ?? 1) === 1);
    const continuedEvidence = directEvidence.filter(evidence =>
      evidence.surface === 'search_project+read_pack' && evidence.calls <= 3);
    const hop = related.hops.get(requiredPath);
    const classification = oneCallEvidence.length > 0
      ? ReachabilityClass.OneCall
      : continuedEvidence.length > 0 || hop !== undefined
        ? ReachabilityClass.ShortChain
        : !exact.isError
          ? ReachabilityClass.RequiresAnswerKnowledge
          : ReachabilityClass.Unreachable;
    results.push({
      path: requiredPath,
      classification,
      reason: classifyReason({
        classification,
        matchingQueries,
        searchRuns,
        treeRuns,
        related,
        directEvidence,
        requiredPath,
        sourceExists: source !== null,
        exact,
      }),
      directEvidence,
      relatedHop: hop ?? null,
      relatedVia: related.parents.get(requiredPath) ?? null,
      matchingTaskQueries: matchingQueries,
      limitEvidence: buildLimitEvidence(requiredPath, matchingQueries, searchRuns, treeRuns),
      exactPathReadable: !exact.isError,
    });
  }

  return {
    id: spec.id,
    repository: repository.id,
    taskSummary: spec.taskSummary,
    queries: spec.queries,
    treePatterns: spec.treePatterns,
    seeds: spec.seeds,
    searchRuns,
    treeRuns,
    related: {
      seedsUsed: discoveredSeeds,
      calls: related.calls,
      unresolvedEvidence: related.unresolvedEvidence,
    },
    requiredFiles: results,
  };
}

function buildLimitEvidence(path, matchingQueries, searchRuns, treeRuns) {
  const search = searchRuns
    .filter(run => matchingQueries.includes(run.pattern))
    .map(run => ({
      pattern: run.pattern,
      complete: run.boundary?.complete ?? null,
      limits: run.boundary?.limits ?? [],
      initialPosition: run.visibleFiles.indexOf(path) + 1 || null,
      continuationPage: run.continuedFiles.findIndex(files => files.includes(path)) + 1 || null,
    }));
  const tree = treeRuns
    .filter(run => matchesGlob(path, run.pattern))
    .map(run => ({ pattern: run.pattern, visible: run.visiblePaths.includes(path), truncated: run.truncated }));
  return { search, tree };
}

async function traverseRelated(client, seeds, maximumHops) {
  const hops = new Map(seeds.map(seed => [seed, 0]));
  const parents = new Map();
  const calls = [];
  const unresolvedEvidence = [];
  let frontier = [...seeds].sort(compareOrdinal);
  for (let hop = 1; hop <= maximumHops && frontier.length > 0; hop++) {
    const next = new Set();
    for (const seed of frontier) {
      const result = await client.callAndPage('related_files', { path: seed, direction: 'both' });
      const parsed = parseRelatedPaths(result.allText);
      calls.push({ seed, hop, resolved: parsed.resolved, unresolved: parsed.unresolved, error: result.isError ? result.text : null });
      unresolvedEvidence.push(...parsed.unresolved.map(item => ({ seed, ...item })));
      for (const path of parsed.resolved.sort(compareOrdinal)) {
        if (hops.has(path))
          continue;
        hops.set(path, hop);
        parents.set(path, seed);
        next.add(path);
      }
    }
    frontier = [...next].sort(compareOrdinal);
  }
  return { hops, parents, calls, unresolvedEvidence };
}

function classifyReason(context) {
  if (context.classification === ReachabilityClass.OneCall)
    return 'task-derived search or tree input names the file';
  if (context.classification === ReachabilityClass.ShortChain) {
    const hop = context.related.hops.get(context.requiredPath);
    if (hop !== undefined)
      return `resolved dependency evidence reaches the file at hop ${hop}`;
    const calls = Math.min(...context.directEvidence
      .filter(evidence => evidence.surface === 'search_project+read_pack')
      .map(evidence => evidence.calls));
    return `a stored search continuation names the file in ${calls} calls`;
  }
  if (!context.sourceExists)
    return 'required path is absent at the pinned repository commit';
  if (context.exact.isError)
    return 'the effective selection rejects the path even when it is known exactly';
  if (context.matchingQueries.length > 0) {
    const limitingRuns = context.searchRuns.filter(run =>
      context.matchingQueries.includes(run.pattern) && run.boundary && !run.boundary.complete);
    if (limitingRuns.length > 0) {
      const limits = [...new Set(limitingRuns.flatMap(run => run.boundary.limits))].sort(compareOrdinal);
      return `matching task term was observed only beyond search limits: ${limits.join(', ')}`;
    }
    return 'matching task term exists, but a complete search did not surface the file';
  }
  if (context.treeRuns.some(run => run.truncated))
    return 'no task-derived content term reaches the file and a relevant tree listing was truncated';
  if (context.related.unresolvedEvidence.length > 0)
    return 'no task-derived content term or resolved chain reaches the file; dependency evidence includes unresolved references';
  return 'no task-derived content term or resolved dependency chain reaches the file';
}

export function parseSearchBoundary(text) {
  const match = /\[Search boundary\] (complete|partial) · sources inspected=(\d+)\/(\d+) · matches retained=(\d+)\/(\d+) · matches written=(\d+) · declaration files named=(\d+)(?: · limits=([^;]+);|\.)/.exec(text);
  if (!match)
    return null;
  return {
    complete: match[1] === 'complete',
    inspectedSources: Number(match[2]),
    eligibleSources: Number(match[3]),
    retainedMatches: Number(match[4]),
    encounteredMatches: Number(match[5]),
    writtenMatches: Number(match[6]),
    namedDeclarationFiles: Number(match[7]),
    limits: match[8] ? match[8].split(',') : [],
  };
}

export function parseRelatedPaths(text) {
  const resolved = new Set();
  const unresolved = [];
  for (const line of normalizeLines(text)) {
    const match = /^(?<path>.+?) — (?<reason>.+?) — (?<status>resolved|unresolved|ambiguous|external|unsupported)(?: — |$)/.exec(line);
    if (!match)
      continue;
    const path = normalizePath(match.groups.path);
    if (match.groups.status === 'resolved')
      resolved.add(path);
    else
      unresolved.push({ path, status: match.groups.status, reason: match.groups.reason });
  }
  return { resolved: [...resolved], unresolved };
}

export function containsPath(text, path) {
  const expected = normalizePath(path);
  return normalizeLines(text).some(line => normalizePath(line.trim()) === expected);
}

export function extractVisibleRepositoryPaths(text, repositoryFiles) {
  const catalog = repositoryFiles instanceof Set ? repositoryFiles : new Set(repositoryFiles);
  const seen = new Set();
  const result = [];
  for (const line of normalizeLines(text)) {
    const path = normalizePath(line.trim());
    if (catalog.has(path) && !seen.has(path)) {
      seen.add(path);
      result.push(path);
    }
  }
  return result;
}

export function extractTreePaths(text) {
  const block = /<untrusted-data-[^>]+>\s*([\s\S]*?)\s*<\/untrusted-data-[^>]+>/.exec(text)?.[1];
  if (!block)
    return [];
  let document;
  try {
    document = JSON.parse(block);
  } catch {
    return [];
  }
  const paths = [];
  visit(document.tree, '');
  return paths.sort(compareOrdinal);

  function visit(node, prefix) {
    if (Array.isArray(node)) {
      for (const name of node)
        if (typeof name === 'string') paths.push(normalizePath(prefix ? `${prefix}/${name}` : name));
      return;
    }
    if (!node || typeof node !== 'object')
      return;
    for (const [name, value] of Object.entries(node)) {
      const path = name === '/' ? prefix : prefix ? `${prefix}/${name}` : name;
      if (value === null || typeof value === 'string')
        paths.push(normalizePath(path));
      else
        visit(value, path);
    }
  }
}

function aggregateUniqueFiles(tasks) {
  const files = new Map();
  for (const task of tasks) {
    for (const file of task.requiredFiles) {
      const key = `${task.repository}\u0000${file.path}`;
      const current = files.get(key) ?? {
        repository: task.repository,
        path: file.path,
        tasks: [],
        classification: file.classification,
        reasons: [],
      };
      current.tasks.push(task.id);
      current.classification = betterClass(current.classification, file.classification);
      if (!current.reasons.includes(file.reason))
        current.reasons.push(file.reason);
      files.set(key, current);
    }
  }
  return [...files.values()].sort((left, right) =>
    left.repository.localeCompare(right.repository, 'en') || compareOrdinal(left.path, right.path));
}

function summarize(tasks) {
  const occurrences = tasks.flatMap(task => task.requiredFiles);
  const unique = aggregateUniqueFiles(tasks);
  return {
    taskFileOccurrences: occurrences.length,
    uniqueRequiredFiles: unique.length,
    occurrencesByClass: countClasses(occurrences),
    uniqueFilesByClass: countClasses(unique),
  };
}

function countClasses(values) {
  return Object.fromEntries(Object.values(ReachabilityClass).map(value =>
    [value, values.filter(item => item.classification === value).length]));
}

function betterClass(left, right) {
  const order = Object.values(ReachabilityClass);
  return order.indexOf(left) <= order.indexOf(right) ? left : right;
}

function safeRegex(query) {
  try {
    return new RegExp(query.pattern, query.ignoreCase === false ? 'u' : 'iu');
  } catch (error) {
    throw new Error(`Task query '${query.pattern}' is not valid JavaScript regex for source verification: ${error.message}`);
  }
}

async function readOptionalText(path) {
  try {
    return await readFile(path, 'utf8');
  } catch (error) {
    if (error.code === 'ENOENT')
      return null;
    throw error;
  }
}

function containedPath(root, relativePath) {
  const normalizedRoot = resolve(root);
  const candidate = resolve(normalizedRoot, relativePath.replaceAll('/', sep));
  if (candidate !== normalizedRoot && !candidate.startsWith(`${normalizedRoot}${sep}`))
    throw new Error(`Required path escapes repository root: ${relativePath}.`);
  return candidate;
}

function normalizePath(value) {
  return value.replaceAll('\\', '/').replace(/^\.\//, '');
}

function normalizeLines(value) {
  return value.replaceAll('\r\n', '\n').replaceAll('\r', '\n').split('\n');
}

async function listRepositoryFiles(root) {
  const files = [];
  await visit(root, '');
  return files.sort(compareOrdinal);

  async function visit(directory, prefix) {
    const entries = await readdir(directory, { withFileTypes: true });
    for (const entry of entries.sort((left, right) => compareOrdinal(left.name, right.name))) {
      if (entry.name === '.git')
        continue;
      const relativePath = prefix ? `${prefix}/${entry.name}` : entry.name;
      if (entry.isDirectory())
        await visit(resolve(directory, entry.name), relativePath);
      else if (entry.isFile())
        files.push(normalizePath(relativePath));
    }
  }
}

function matchesGlob(path, glob) {
  let pattern = '^';
  for (let index = 0; index < glob.length; index++) {
    const character = glob[index];
    if (character === '*' && glob[index + 1] === '*') {
      pattern += '.*';
      index++;
    } else if (character === '*') {
      pattern += '[^/]*';
    } else if (character === '?') {
      pattern += '[^/]';
    } else {
      pattern += character.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
    }
  }
  return new RegExp(`${pattern}$`, 'i').test(path);
}

function compareOrdinal(left, right) {
  return left < right ? -1 : left > right ? 1 : 0;
}

function validateRegistry(registry) {
  if (registry?.schemaVersion !== 1 || !Array.isArray(registry.repositories) || !Array.isArray(registry.tasks))
    throw new Error('Reachability registry schemaVersion 1, repositories, and tasks are required.');
  for (const task of registry.tasks) {
    if (!task.id || !task.repository || !task.taskSummary || !Array.isArray(task.queries) || !Array.isArray(task.treePatterns) || !Array.isArray(task.seeds))
      throw new Error('Every reachability task requires id, repository, taskSummary, queries, treePatterns, and seeds.');
    for (const query of task.queries)
      if (!query.pattern || !query.rationale)
        throw new Error(`Every query for '${task.id}' requires pattern and rationale.`);
    for (const seed of task.seeds)
      if (!seed.path || !seed.discoveredBy?.surface || !seed.discoveredBy?.input)
        throw new Error(`Every seed for '${task.id}' must name how it is discovered.`);
  }
}
