export function analyzeSavedReadings(records, options = {}) {
  const smallFileCharacters = requirePositiveInteger(
    options.smallFileCharacters,
    'small-file character threshold');
  const sessions = records.map(record => analyzeSession(record, smallFileCharacters));
  const totalToolCharacters = sessions.reduce((sum, session) => sum + session.totalToolCharacters, 0);
  const readingGroups = combineReadingGroups(sessions, totalToolCharacters);
  const chains = sessions.flatMap(session => session.chains);
  const toolCarryCost = combineToolCarryCost(sessions);
  return {
    sessions: sessions.length,
    totalToolCharacters,
    readingGroups,
    chains,
    eliminableTurns: chains.reduce((sum, chain) => sum + chain.eliminableTurns, 0),
    toolCarryCost,
  };
}

export function extractKnownAddresses(text) {
  const addresses = [];
  if (typeof text !== 'string' || text.length === 0)
    return addresses;
  const path = String.raw`(?:[A-Za-z]:[\\/])?[^\s\x60"'<>|]+?\.[A-Za-z0-9][A-Za-z0-9+_-]*`;
  const selector = new RegExp(
    String.raw`(?<path>${path})(?:(?::(?<line>\d+)(?:-(?<end>\d+))?)|(?:#L(?<anchor>\d+))|(?:(?::|#)(?<symbol>[A-Za-z_][A-Za-z0-9_.+:#|=<>-]*)))`,
    'g');
  for (const match of text.matchAll(selector)) {
    addresses.push({
      path: normalizePath(match.groups.path),
      line: match.groups.line ? Number(match.groups.line) : match.groups.anchor ? Number(match.groups.anchor) : null,
      endLine: match.groups.end ? Number(match.groups.end) : null,
      symbol: match.groups.symbol ?? null,
    });
  }
  try {
    collectStructuredAddresses(JSON.parse(text), addresses);
  } catch {
  }
  return uniqueAddresses(addresses);
}

function analyzeSession(record, smallFileCharacters) {
  const interactions = normalizeInteractions(record);
  const turnIndexes = new Map(record.measurement.turns.map((turn, index) => [turn.turnId, index]));
  const totalToolCharacters = interactions.reduce(
    (sum, interaction) => sum + (interaction.responseText?.length ?? 0),
    0);
  const fullReadings = [];
  for (const interaction of interactions) {
    if (!isWholeFileRead(interaction))
      continue;
    const path = requestedPath(interaction);
    const known = knownBefore(interactions, interaction.sequence, path);
    const characters = interaction.responseText?.length ?? 0;
    fullReadings.push({
      sessionId: record.identity.sessionId,
      task: record.identity.task,
      arm: record.identity.arm,
      path,
      characters,
      classification: known.length > 0
        ? 'known-section-unused'
        : characters <= smallFileCharacters
          ? 'small-whole-read'
          : 'large-needs-address',
      knownAddresses: known,
    });
  }
  return {
    sessionId: record.identity.sessionId,
    totalToolCharacters,
    fullReadings,
    chains: findReadChains(record, interactions),
    toolCarryCost: calculateToolCarryCost(record, interactions, turnIndexes),
  };
}

function normalizeInteractions(record) {
  if (!Array.isArray(record.measurement?.toolInteractions))
    throw new Error(`Session '${record.identity?.sessionId ?? '<unknown>'}' has no recorded tool interactions.`);
  return [...record.measurement.toolInteractions].sort(
    (left, right) => left.sequence - right.sequence || left.id.localeCompare(right.id, 'en'));
}

function isWholeFileRead(interaction) {
  if (interaction.name !== 'get_file')
    return false;
  const input = interaction.input ?? {};
  return noValue(input.symbol) && noValue(input.start_line) && noValue(input.startLine) &&
    noValue(input.end_line) && noValue(input.endLine);
}

function requestedPath(interaction) {
  const path = interaction.input?.path ?? interaction.input?.file;
  if (typeof path !== 'string' || path.length === 0)
    throw new Error(`get_file interaction '${interaction.id}' does not contain a path.`);
  return normalizePath(path);
}

function knownBefore(interactions, sequence, path) {
  const result = [];
  for (const interaction of interactions) {
    if (!interaction.success || interaction.responseSequence === null || interaction.responseSequence >= sequence)
      continue;
    for (const address of extractKnownAddresses(interaction.responseText))
      if (samePath(address.path, path))
        result.push(address);
  }
  return uniqueAddresses(result);
}

function findReadChains(record, interactions) {
  const turnOrder = new Map(record.measurement.turns.map((turn, index) => [turn.turnId, index]));
  const chains = [];
  let current = [];
  for (const interaction of interactions) {
    if (interaction.name !== 'get_file' || (current.length > 0 &&
        turnOrder.get(interaction.turnId) !== turnOrder.get(current.at(-1).turnId) + 1)) {
      appendChain(chains, record, interactions, current);
      current = interaction.name === 'get_file' ? [interaction] : [];
      continue;
    }
    current.push(interaction);
  }
  appendChain(chains, record, interactions, current);
  return chains;
}

function appendChain(chains, record, interactions, chain) {
  if (chain.length < 2)
    return;
  const before = chain[0].sequence;
  const elements = chain.map((interaction, index) => {
    const path = requestedPath(interaction);
    const addressableBefore = knownBefore(interactions, before, path).length > 0;
    return { index: index + 1, path, addressableBefore };
  });
  const batchable = elements.slice(1).every(element => element.addressableBefore);
  chains.push({
    sessionId: record.identity.sessionId,
    task: record.identity.task,
    arm: record.identity.arm,
    length: elements.length,
    addressableBefore: elements.filter(element => element.addressableBefore).length,
    batchable,
    eliminableTurns: batchable ? elements.length - 1 : 0,
    elements,
  });
}

function calculateToolCarryCost(record, interactions, turnIndexes) {
  const totalTurns = record.measurement.turns.length;
  return interactions.map(interaction => {
    if (interaction.responseText === null)
      return null;
    if (interaction.responseTokens === null)
      throw new Error(
        `Session '${record.identity.sessionId}' interaction '${interaction.id}' has no observed response token count.`);
    const turnIndex = turnIndexes.get(interaction.turnId);
    if (turnIndex === undefined)
      throw new Error(`Interaction '${interaction.id}' refers to an unknown model turn.`);
    const subsequentTurns = totalTurns - turnIndex - 1;
    return {
      tool: interaction.name,
      responseTokens: interaction.responseTokens,
      subsequentTurns,
      tokenTurns: interaction.responseTokens * subsequentTurns,
    };
  }).filter(Boolean);
}

function combineReadingGroups(sessions, totalToolCharacters) {
  return ['known-section-unused', 'small-whole-read', 'large-needs-address'].map(classification => {
    const readings = sessions.flatMap(session => session.fullReadings)
      .filter(reading => reading.classification === classification);
    const characters = readings.reduce((sum, reading) => sum + reading.characters, 0);
    return {
      classification,
      readings: readings.length,
      characters,
      shareOfToolCharacters: totalToolCharacters === 0 ? 0 : characters / totalToolCharacters,
    };
  });
}

function combineToolCarryCost(sessions) {
  const tools = new Map();
  for (const value of sessions.flatMap(session => session.toolCarryCost)) {
    let total = tools.get(value.tool);
    if (!total) {
      total = { tool: value.tool, responses: 0, responseTokens: 0, tokenTurns: 0 };
      tools.set(value.tool, total);
    }
    total.responses++;
    total.responseTokens += value.responseTokens;
    total.tokenTurns += value.tokenTurns;
  }
  return [...tools.values()].sort(
    (left, right) => right.tokenTurns - left.tokenTurns || left.tool.localeCompare(right.tool, 'en'));
}

function collectStructuredAddresses(value, addresses) {
  if (Array.isArray(value)) {
    for (const item of value)
      collectStructuredAddresses(item, addresses);
    return;
  }
  if (!value || typeof value !== 'object')
    return;
  const path = value.path ?? value.file;
  const line = value.line ?? value.startLine ?? value.start_line ?? null;
  const endLine = value.endLine ?? value.end_line ?? null;
  const symbol = value.symbol ?? value.name ?? null;
  if (typeof path === 'string' && (Number.isSafeInteger(line) || typeof symbol === 'string')) {
    addresses.push({
      path: normalizePath(path),
      line: Number.isSafeInteger(line) ? line : null,
      endLine: Number.isSafeInteger(endLine) ? endLine : null,
      symbol: typeof symbol === 'string' ? symbol : null,
    });
  }
  for (const nested of Object.values(value))
    collectStructuredAddresses(nested, addresses);
}

function uniqueAddresses(addresses) {
  const seen = new Set();
  return addresses.filter(address => {
    const key = JSON.stringify(address);
    if (seen.has(key))
      return false;
    seen.add(key);
    return true;
  });
}

function samePath(left, right) {
  return normalizePath(left).toLocaleLowerCase('en-US') === normalizePath(right).toLocaleLowerCase('en-US');
}

function normalizePath(path) {
  return path.replaceAll('\\', '/');
}

function noValue(value) {
  return value === undefined || value === null || value === '';
}

function requirePositiveInteger(value, label) {
  if (!Number.isSafeInteger(value) || value <= 0)
    throw new Error(`${label} must be a positive safe integer.`);
  return value;
}
