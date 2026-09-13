import { createHash } from 'node:crypto';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { join, resolve } from 'node:path';

const usageNames = Object.freeze([
  'inputTokens',
  'cacheWriteTokens',
  'cacheReadTokens',
  'outputTokens',
]);

const sharedIdentityFields = Object.freeze([
  ['productBuildSha', 'product SHA'],
  ['model', 'model'],
  ['clientVersion', 'client version'],
  ['toolLoadingMode', 'tool loading mode'],
  ['serverInstructionsSha256', 'server instructions fingerprint'],
  ['toolConfigurationSha256', 'tool configuration fingerprint'],
  ['limitsSha256', 'limits fingerprint'],
  ['pricingSha256', 'pricing fingerprint'],
]);

export function buildSeriesManifest(configuration) {
  requireObject(configuration, 'series configuration');
  const seriesId = requireText(configuration.seriesId, 'series identifier');
  if (!/^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$/.test(seriesId))
    throw new Error('Series identifier must contain only letters, digits, dots, underscores, and hyphens.');
  const productBuildSha = requireText(configuration.productBuildSha, 'product SHA');
  if (!/^[0-9a-f]{40}$/.test(productBuildSha))
    throw new Error('Product SHA must be a full lowercase Git SHA.');
  const serverInstructions = requireText(configuration.serverInstructions, 'server instructions');
  requireObject(configuration.toolConfiguration, 'tool configuration');
  requireObject(configuration.limits, 'limits');
  const pricing = normalizePricing(configuration.pricing);
  const identity = {
    seriesId,
    productBuildSha,
    model: requireText(configuration.model, 'model'),
    clientVersion: requireText(configuration.clientVersion, 'client version'),
    toolLoadingMode: requireText(configuration.toolLoadingMode, 'tool loading mode'),
    serverInstructionsSha256: digest(serverInstructions),
    toolConfigurationSha256: digest(canonicalJson(configuration.toolConfiguration)),
    limitsSha256: digest(canonicalJson(configuration.limits)),
    pricingSha256: digest(canonicalJson(pricing)),
  };
  return {
    schemaVersion: 1,
    identity,
    pricing,
  };
}

export async function createSeries(rootDirectory, configuration) {
  const manifest = buildSeriesManifest(configuration);
  const root = resolve(rootDirectory);
  const directory = join(root, manifest.identity.seriesId);
  await mkdir(root, { recursive: true });
  try {
    await mkdir(directory);
  } catch (error) {
    if (error?.code === 'EEXIST')
      throw new Error(`Series directory already exists: ${directory}. Use the explicit resume command.`);
    throw error;
  }
  await mkdir(join(directory, 'records'));
  await writeFile(join(directory, 'series.json'), serialize(manifest), { encoding: 'utf8', flag: 'wx' });
  return { directory, manifest };
}

export async function resumeSeries(seriesDirectory, configuration) {
  const expected = buildSeriesManifest(configuration);
  const directory = resolve(seriesDirectory);
  let actual;
  try {
    actual = JSON.parse(await readFile(join(directory, 'series.json'), 'utf8'));
  } catch (error) {
    if (error?.code === 'ENOENT')
      throw new Error(`Series cannot be resumed because its manifest is missing: ${directory}.`);
    throw error;
  }
  validateManifest(actual);
  const mismatch = firstIdentityMismatch(actual.identity, expected.identity, true);
  if (mismatch)
    throw new Error(`Series identity mismatch: ${mismatch}.`);
  return { directory, manifest: actual };
}

export async function readSeriesManifest(seriesDirectory) {
  const manifest = JSON.parse(await readFile(join(resolve(seriesDirectory), 'series.json'), 'utf8'));
  validateManifest(manifest);
  return manifest;
}

export function createRunRecord(manifest, slot, report) {
  validateManifest(manifest);
  requireObject(slot, 'run slot');
  requireObject(report, 'session report');
  requireObject(report.session, 'session report identity');
  requireObject(report.totals, 'session totals');
  const task = requireText(slot.task, 'task');
  const repetition = requirePositiveInteger(slot.repetition, 'repetition');
  const arm = requireText(slot.arm, 'arm');
  const sessionId = requireText(report.session.sessionId, 'session identifier');
  if (!isUuid(sessionId))
    throw new Error('Session identifier must be a UUID.');
  const identity = {
    seriesId: manifest.identity.seriesId,
    task,
    repetition,
    arm,
    sessionId,
    productBuildSha: report.session.buildSha,
    model: report.session.model,
    clientVersion: report.session.clientVersion,
    toolLoadingMode: report.session.toolLoadingMode,
    serverInstructionsSha256: report.session.serverInstructionsSha256,
    toolConfigurationSha256: report.session.toolConfigurationSha256,
    limitsSha256: report.session.limitsSha256,
    pricingSha256: report.session.pricingSha256,
  };
  const mismatch = firstIdentityMismatch(identity, manifest.identity, false);
  if (mismatch)
    throw new Error(`Session report identity mismatch: ${mismatch}.`);
  const usage = copyUsage(report.totals.usage, 'session usage');
  const turns = requireTurns(report.turns);
  const modelTurns = requireNonNegativeInteger(report.totals.modelTurns, 'model turn count');
  if (modelTurns !== turns.length)
    throw new Error('Raw record rejected: model turn count does not match the distinct turns.');
  assertUsageEqual(sumTurnUsage(turns), usage,
    'Raw record rejected: sum of turns does not equal the session total');
  const normalizedCost = calculateCost(usage, manifest.pricing);
  const outcome = normalizeOutcome(report.session.status);
  const storageKey = digest(canonicalJson({ task, repetition, arm }));
  return {
    schemaVersion: 1,
    storageKey,
    identity,
    measurement: {
      usage,
      cost: normalizedCost,
      outcome,
      durationMs: nullableNonNegativeInteger(report.session.durationMs, 'duration'),
      modelTurns,
      toolCalls: requireNonNegativeInteger(report.totals.toolCalls, 'tool call count'),
      wireResponseBytes: nullableNonNegativeInteger(report.totals.wireResponseBytes, 'wire response bytes'),
      decodedResponseBytes: nullableNonNegativeInteger(report.totals.decodedResponseBytes, 'decoded response bytes'),
      modelInputBytes: nullableNonNegativeInteger(report.totals.modelInputBytes, 'model input bytes'),
      turns,
    },
  };
}

export async function storeRunRecord(seriesDirectory, record, requestedStorageKey = record?.storageKey) {
  const directory = resolve(seriesDirectory);
  const manifest = JSON.parse(await readFile(join(directory, 'series.json'), 'utf8'));
  validateManifest(manifest);
  validateRecord(record);
  const manifestMismatch = firstIdentityMismatch(record.identity, manifest.identity, true);
  if (manifestMismatch)
    throw new Error(`Saved run mismatch: ${manifestMismatch}.`);
  const storageKey = requireFingerprint(requestedStorageKey, 'storage key');
  const path = join(directory, 'records', `${storageKey}.json`);
  let existing = null;
  try {
    existing = JSON.parse(await readFile(path, 'utf8'));
  } catch (error) {
    if (error?.code !== 'ENOENT')
      throw error;
  }
  if (existing) {
    validateRecord(existing);
    const mismatch = firstRunMismatch(existing, record);
    if (mismatch)
      throw new Error(`Saved run mismatch: ${mismatch}; refusing to reuse ${path}.`);
    return { status: 'reused', path };
  }
  assertCostMatches(record, manifest);
  if (record.storageKey !== storageKey)
    throw new Error('Saved run mismatch: task, repetition, or arm does not match the requested storage slot.');
  assertStorageKey(record);
  const records = await loadRecords(directory);
  if (records.some(existingRecord => existingRecord.identity.sessionId === record.identity.sessionId))
    throw new Error(`Saved run mismatch: session identifier '${record.identity.sessionId}' is already recorded.`);
  try {
    await writeFile(path, serialize(record), { encoding: 'utf8', flag: 'wx' });
  } catch (error) {
    if (error?.code !== 'EEXIST')
      throw error;
    const raced = JSON.parse(await readFile(path, 'utf8'));
    const mismatch = firstRunMismatch(raced, record);
    if (mismatch)
      throw new Error(`Saved run mismatch after concurrent creation: ${mismatch}.`);
    return { status: 'reused', path };
  }
  return { status: 'stored', path };
}

export async function summarizeSeries(seriesDirectory) {
  const directory = resolve(seriesDirectory);
  const manifest = JSON.parse(await readFile(join(directory, 'series.json'), 'utf8'));
  const records = await loadRecords(directory);
  return summarizeSeriesRecords(manifest, records);
}

export function summarizeSeriesRecords(manifest, records) {
  validateManifest(manifest);
  if (!Array.isArray(records) || records.length === 0)
    throw new Error('Series summary requires at least one raw run record.');
  const storageKeys = new Set();
  const sessionIds = new Set();
  for (const record of records) {
    validateRecord(record);
    assertStorageKey(record);
    if (record.identity.seriesId !== manifest.identity.seriesId) {
      throw new Error(
        `Raw record '${record.storageKey}' belongs to series '${record.identity.seriesId}', not '${manifest.identity.seriesId}'.`);
    }
    const sharedMismatch = firstIdentityMismatch(record.identity, manifest.identity, false);
    if (sharedMismatch)
      throw new Error(`Raw record '${record.storageKey}' has a different ${sharedMismatch}.`);
    if (storageKeys.has(record.storageKey))
      throw new Error(`Raw record '${record.storageKey}' appears more than once in the series.`);
    storageKeys.add(record.storageKey);
    if (sessionIds.has(record.identity.sessionId))
      throw new Error(`Session identifier '${record.identity.sessionId}' appears more than once in the series.`);
    sessionIds.add(record.identity.sessionId);
    assertUsageEqual(
      sumTurnUsage(record.measurement.turns),
      record.measurement.usage,
      `Raw record '${record.storageKey}' rejected: sum of turns does not equal the session total`);
    assertCostMatches(record, manifest);
  }

  const arms = buildArmTotals(records);
  validateArmAccounting(records, arms);
  return {
    schemaVersion: 1,
    series: manifest.identity,
    sessions: records.length,
    rows: [...records]
      .sort(compareRecords)
      .map(record => ({
        seriesId: record.identity.seriesId,
        task: record.identity.task,
        repetition: record.identity.repetition,
        arm: record.identity.arm,
        sessionId: record.identity.sessionId,
        usage: record.measurement.usage,
        cost: record.measurement.cost,
        outcome: record.measurement.outcome,
      })),
    arms,
  };
}

export function validateArmAccounting(records, arms) {
  if (!Array.isArray(records) || !Array.isArray(arms))
    throw new Error('Arm accounting requires raw records and arm totals.');
  const byArm = new Map();
  for (const arm of arms) {
    requireObject(arm, 'arm total');
    const name = requireText(arm.arm, 'arm total name');
    if (byArm.has(name))
      throw new Error(`Series summary rejected: arm '${name}' appears more than once.`);
    byArm.set(name, arm);
  }
  const recordArms = new Set(records.map(record => record.identity.arm));
  if (byArm.size !== recordArms.size || [...recordArms].some(arm => !byArm.has(arm)))
    throw new Error('Series summary rejected: sum of sessions does not equal the arm total.');
  for (const name of recordArms) {
    const sessions = records.filter(record => record.identity.arm === name);
    const actual = byArm.get(name);
    const usage = sessions.reduce(
      (total, record) => addUsage(total, record.measurement.usage),
      emptyUsage());
    const outcomes = { success: 0, error: 0, aborted: 0 };
    for (const record of sessions)
      outcomes[record.measurement.outcome]++;
    const currency = sessions[0].measurement.cost.currency;
    if (sessions.some(record => record.measurement.cost.currency !== currency) ||
        actual.sessions !== sessions.length ||
        canonicalJson(actual.usage) !== canonicalJson(usage) ||
        canonicalJson(actual.outcomes) !== canonicalJson(outcomes) ||
        actual.cost?.currency !== currency ||
        actual.cost?.amount !== sessions.reduce((sum, record) => sum + record.measurement.cost.amount, 0)) {
      throw new Error('Series summary rejected: sum of sessions does not equal the arm total.');
    }
  }
}

async function loadRecords(directory) {
  const { readdir } = await import('node:fs/promises');
  const names = (await readdir(join(directory, 'records')))
    .filter(name => name.endsWith('.json'))
    .sort();
  const records = [];
  for (const name of names)
  {
    const record = JSON.parse(await readFile(join(directory, 'records', name), 'utf8'));
    if (name !== `${record.storageKey}.json`)
      throw new Error(`Raw record file '${name}' does not match its storage key.`);
    records.push(record);
  }
  return records;
}

function buildArmTotals(records) {
  const totals = new Map();
  for (const record of records) {
    const arm = record.identity.arm;
    let total = totals.get(arm);
    if (!total) {
      total = {
        arm,
        sessions: 0,
        usage: emptyUsage(),
        cost: { amount: 0, currency: record.measurement.cost.currency },
        outcomes: { success: 0, error: 0, aborted: 0 },
      };
      totals.set(arm, total);
    }
    if (total.cost.currency !== record.measurement.cost.currency)
      throw new Error(`Arm '${arm}' contains costs in more than one currency.`);
    total.sessions++;
    addUsage(total.usage, record.measurement.usage);
    total.cost.amount += record.measurement.cost.amount;
    total.outcomes[record.measurement.outcome]++;
  }
  return [...totals.values()].sort((left, right) => left.arm.localeCompare(right.arm, 'en'));
}

function validateManifest(manifest) {
  requireObject(manifest, 'series manifest');
  if (manifest.schemaVersion !== 1)
    throw new Error('Unsupported series manifest schema version.');
  requireObject(manifest.identity, 'series identity');
  requireText(manifest.identity.seriesId, 'series identifier');
  requireText(manifest.identity.productBuildSha, 'product SHA');
  requireText(manifest.identity.model, 'model');
  requireText(manifest.identity.clientVersion, 'client version');
  requireText(manifest.identity.toolLoadingMode, 'tool loading mode');
  requireFingerprint(manifest.identity.serverInstructionsSha256, 'server instructions fingerprint');
  requireFingerprint(manifest.identity.toolConfigurationSha256, 'tool configuration fingerprint');
  requireFingerprint(manifest.identity.limitsSha256, 'limits fingerprint');
  requireFingerprint(manifest.identity.pricingSha256, 'pricing fingerprint');
  const pricing = normalizePricing(manifest.pricing);
  if (manifest.identity.pricingSha256 !== digest(canonicalJson(pricing)))
    throw new Error('Series manifest pricing does not match its fingerprint.');
}

function validateRecord(record) {
  requireObject(record, 'raw run record');
  if (record.schemaVersion !== 1)
    throw new Error('Unsupported raw run record schema version.');
  requireFingerprint(record.storageKey, 'storage key');
  requireObject(record.identity, 'run identity');
  for (const name of ['seriesId', 'task', 'arm', 'sessionId', 'productBuildSha', 'model', 'clientVersion',
    'toolLoadingMode']) {
    requireText(record.identity[name], name);
  }
  requirePositiveInteger(record.identity.repetition, 'repetition');
  for (const name of ['serverInstructionsSha256', 'toolConfigurationSha256', 'limitsSha256', 'pricingSha256'])
    requireFingerprint(record.identity[name], name);
  requireObject(record.measurement, 'run measurement');
  copyUsage(record.measurement.usage, 'session usage');
  normalizeCost(record.measurement.cost);
  normalizeOutcome(record.measurement.outcome);
  const turns = requireTurns(record.measurement.turns);
  const modelTurns = requireNonNegativeInteger(record.measurement.modelTurns, 'model turn count');
  if (modelTurns !== turns.length)
    throw new Error('Raw run record model turn count does not match the distinct turns.');

}

function firstIdentityMismatch(actual, expected, includeSeries) {
  if (includeSeries && actual.seriesId !== expected.seriesId)
    return 'series identifier';
  for (const [field, label] of sharedIdentityFields)
    if (actual[field] !== expected[field])
      return label;
  return null;
}

function firstRunMismatch(left, right) {
  const identityFields = [
    ['seriesId', 'series identifier'],
    ['task', 'task'],
    ['repetition', 'repetition'],
    ['arm', 'arm'],
    ['sessionId', 'session identifier'],
    ...sharedIdentityFields,
  ];
  for (const [field, label] of identityFields)
    if (left.identity[field] !== right.identity[field])
      return label;
  for (const name of usageNames)
    if (left.measurement.usage[name] !== right.measurement.usage[name])
      return `${usageLabel(name)} usage`;
  if (canonicalJson(left.measurement.cost) !== canonicalJson(right.measurement.cost))
    return 'cost';
  if (left.measurement.outcome !== right.measurement.outcome)
    return 'outcome';
  if (canonicalJson(left) !== canonicalJson(right))
    return 'record contents';
  return null;
}

function requireTurns(turns) {
  if (!Array.isArray(turns))
    throw new Error('Session turns must be an array.');
  const seen = new Set();
  return turns.map(turn => {
    const turnId = requireText(turn.turnId, 'turn identifier');
    if (seen.has(turnId))
      throw new Error(`Session turn identifier '${turnId}' appears more than once.`);
    seen.add(turnId);
    return {
      turnId,
      usage: copyUsage(turn.usage, 'turn usage'),
    };
  });
}

function sumTurnUsage(turns) {
  return turns.reduce((total, turn) => addUsage(total, turn.usage), emptyUsage());
}

function copyUsage(usage, label) {
  requireObject(usage, label);
  return Object.fromEntries(usageNames.map(name => [name, requireNonNegativeInteger(usage[name], `${label} ${name}`)]));
}

function assertUsageEqual(actual, expected, message) {
  for (const name of usageNames)
    if (actual[name] !== expected[name])
      throw new Error(`${message}: ${usageLabel(name)} differs.`);
}

function usageLabel(name) {
  return ({
    inputTokens: 'input',
    cacheWriteTokens: 'cache-write',
    cacheReadTokens: 'cache-read',
    outputTokens: 'output',
  })[name];
}

function normalizeCost(cost) {
  requireObject(cost, 'cost');
  if (!Number.isFinite(cost.amount) || cost.amount < 0)
    throw new Error('Cost amount must be a non-negative finite number.');
  return {
    amount: cost.amount,
    currency: requireText(cost.currency, 'cost currency'),
  };
}

function normalizePricing(pricing) {
  requireObject(pricing, 'pricing');
  requireObject(pricing.perMillionTokens, 'pricing perMillionTokens');
  return {
    currency: requireText(pricing.currency, 'pricing currency'),
    perMillionTokens: Object.fromEntries(usageNames.map(name => {
      const rate = pricing.perMillionTokens[name];
      if (!Number.isFinite(rate) || rate < 0)
        throw new Error(`Pricing rate ${name} must be a non-negative finite number.`);
      return [name, rate];
    })),
  };
}

function calculateCost(usage, pricing) {
  const normalized = normalizePricing(pricing);
  const amount = usageNames.reduce(
    (total, name) => total + usage[name] * normalized.perMillionTokens[name],
    0) / 1_000_000;
  return { amount, currency: normalized.currency };
}

function assertCostMatches(record, manifest) {
  const expected = calculateCost(record.measurement.usage, manifest.pricing);
  if (canonicalJson(record.measurement.cost) !== canonicalJson(expected))
    throw new Error(`Raw record '${record.storageKey}' cost does not match its usage and pinned pricing.`);
}

function assertStorageKey(record) {
  const expected = digest(canonicalJson({
    task: record.identity.task,
    repetition: record.identity.repetition,
    arm: record.identity.arm,
  }));
  if (record.storageKey !== expected)
    throw new Error('Raw run record storage key does not match its task, repetition, and arm.');
}

function normalizeOutcome(outcome) {
  if (!['success', 'error', 'aborted'].includes(outcome))
    throw new Error('Outcome must be success, error, or aborted.');
  return outcome;
}

function compareRecords(left, right) {
  return left.identity.task.localeCompare(right.identity.task, 'en') ||
    left.identity.arm.localeCompare(right.identity.arm, 'en') ||
    left.identity.repetition - right.identity.repetition ||
    left.identity.sessionId.localeCompare(right.identity.sessionId, 'en');
}

function emptyUsage() {
  return { inputTokens: 0, cacheWriteTokens: 0, cacheReadTokens: 0, outputTokens: 0 };
}

function addUsage(target, value) {
  for (const name of usageNames)
    target[name] += value[name];
  return target;
}

function canonicalJson(value) {
  return JSON.stringify(canonicalize(value));
}

function canonicalize(value) {
  if (Array.isArray(value))
    return value.map(canonicalize);
  if (!value || typeof value !== 'object')
    return value;
  return Object.fromEntries(Object.keys(value).sort().map(key => [key, canonicalize(value[key])]));
}

function serialize(value) {
  return `${JSON.stringify(value, null, 2)}\n`;
}

function digest(value) {
  return createHash('sha256').update(value).digest('hex');
}

function requireObject(value, label) {
  if (!value || typeof value !== 'object' || Array.isArray(value))
    throw new Error(`${label} must be an object.`);
  return value;
}

function requireText(value, label) {
  if (typeof value !== 'string' || value.length === 0)
    throw new Error(`${label} must be a non-empty string.`);
  return value;
}

function requireFingerprint(value, label) {
  if (typeof value !== 'string' || !/^[0-9a-f]{64}$/.test(value))
    throw new Error(`${label} must be a lowercase SHA-256 fingerprint.`);
  return value;
}

function requirePositiveInteger(value, label) {
  if (!Number.isSafeInteger(value) || value <= 0)
    throw new Error(`${label} must be a positive safe integer.`);
  return value;
}

function requireNonNegativeInteger(value, label) {
  if (!Number.isSafeInteger(value) || value < 0)
    throw new Error(`${label} must be a non-negative safe integer.`);
  return value;
}

function nullableNonNegativeInteger(value, label) {
  if (value === null || value === undefined)
    return null;
  return requireNonNegativeInteger(value, label);
}

function isUuid(value) {
  return typeof value === 'string' &&
    /^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(value);
}
