export function normalizeCliRelated(document) {
  if (document?.kind !== 'devprojex-related-files')
    throw new Error('CLI related output has an unexpected shape.');
  return document.seeds.map(seed => ({
    seed: seed.seed,
    noFactsReason: seed.noFactsReason ?? null,
    dependencies: normalizeFiles(seed.dependencies),
    dependents: normalizeFiles(seed.dependents),
  }));
}

export function parseMcpRelated(text) {
  const result = { seed: null, noFactsReason: null, dependencies: [], dependents: [] };
  let section = null;
  for (const raw of text.split(/\r?\n/)) {
    const line = stripFrame(raw.trim());
    if (line.startsWith('Seed: ')) {
      result.seed = line.slice('Seed: '.length);
      section = null;
      continue;
    }
    if (line.startsWith('[No facts] ')) {
      result.noFactsReason = line.slice('[No facts] '.length).replace(/[.]$/, '');
      section = null;
      continue;
    }
    if (line === 'Dependencies:') {
      section = 'dependencies';
      continue;
    }
    if (line === 'Dependents:') {
      section = 'dependents';
      continue;
    }
    const match = /^(.+?) — (.+) — (resolved|ambiguous|unresolved|external) — \d+ tokens(?: — .*)?$/.exec(line);
    if (!match || section === null) continue;
    result[section].push({
      path: match[1],
      status: match[3],
      reasons: match[2].split(' · '),
    });
  }
  result.dependencies.sort(compareFile);
  result.dependents.sort(compareFile);
  return result;
}

export function compareRelated(cliSeed, mcpSeed) {
  const left = canonicalRelated(cliSeed);
  const right = canonicalRelated(mcpSeed);
  return {
    equal: JSON.stringify(left) === JSON.stringify(right),
    cli: left,
    mcp: right,
  };
}

export function classifyExpectedRelations(sample, observed) {
  const actualResolved = new Set([
    ...observed.dependencies.filter(file => file.status === 'resolved').map(file => `dependencies:${file.path}`),
    ...observed.dependents.filter(file => file.status === 'resolved').map(file => `dependents:${file.path}`),
  ]);
  const actualUnresolved = new Set((sample.evidence ?? [])
    .filter(edge => edge.status === 'unresolved')
    .map(edge => edge.reference));
  const expected = sample.expectedRelations ?? [];
  const classified = expected.map(relation => {
    const key = `${relation.direction}:${relation.path}`;
    const state = actualResolved.has(key)
      ? 'confirmed'
      : relation.reference && actualUnresolved.has(relation.reference)
        ? 'missed-honestly'
        : 'missed-silently';
    return { ...relation, state };
  });
  const expectedKeys = new Set(expected.map(relation => `${relation.direction}:${relation.path}`));
  const falseEdges = [...actualResolved].filter(key => !expectedKeys.has(key));
  return {
    relations: classified,
    confirmed: classified.filter(relation => relation.state === 'confirmed').length,
    missedHonestly: classified.filter(relation => relation.state === 'missed-honestly').length,
    missedSilently: classified.filter(relation => relation.state === 'missed-silently').length,
    falseEdges,
  };
}

function normalizeFiles(files = []) {
  return files.map(file => ({
    path: file.path,
    status: file.status,
    reasons: [...file.reasons],
  })).sort(compareFile);
}

function canonicalRelated(seed) {
  return {
    seed: seed.seed ?? null,
    noFactsReason: seed.noFactsReason ?? null,
    dependencies: normalizeFiles(seed.dependencies),
    dependents: normalizeFiles(seed.dependents),
  };
}

function compareFile(left, right) {
  return left.path.localeCompare(right.path, 'en') ||
    left.status.localeCompare(right.status, 'en') ||
    left.reasons.join('\0').localeCompare(right.reasons.join('\0'), 'en');
}

function stripFrame(line) {
  return line
    .replace(/^.*?<untrusted-data[^>]*>/, '')
    .replace(/<\/untrusted-data>.*$/, '')
    .trim();
}
