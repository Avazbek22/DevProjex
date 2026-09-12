import { createHash, randomUUID } from 'node:crypto';

export function createSessionIdentity() {
  return randomUUID();
}

export function validateSeriesConfiguration(configuration, knownSessionIds = new Set()) {
  const servers = Object.keys(configuration.mcpConfig?.mcpServers ?? {});
  rejectUnless(servers.length === 1, 'exactly one compared server is required per session');
  rejectUnless(configuration.recorderConnected === true, 'the usage recorder must be connected');

  const sessionId = configuration.sessionId;
  rejectUnless(isUuid(sessionId), 'a new UUID session identifier is required');
  rejectUnless(!knownSessionIds.has(sessionId), 'the session identifier was already used');
  const state = configuration.sessionState;
  rejectUnless(state && Object.values(state).every(value => value === 0), 'session state must be empty');

  rejectUnless(/^[0-9a-f]{40}$/.test(configuration.buildSha ?? ''), 'a full lowercase build SHA is required');
  rejectUnless(isPinnedLimits(configuration.limits), 'all series limits must be pinned');

  rejectUnless(nonEmpty(configuration.model) && configuration.model === configuration.expectedModel,
    'the model must match the pinned model');
  rejectUnless(nonEmpty(configuration.clientVersion) && configuration.clientVersion === configuration.expectedClientVersion,
    'the client version must match the pinned version');

  knownSessionIds.add(sessionId);
  const limits = canonicalize(configuration.limits);
  return Object.freeze({
    sessionId,
    server: servers[0],
    buildSha: configuration.buildSha,
    limits,
    limitsSha256: digest(JSON.stringify(limits)),
    model: configuration.model,
    clientVersion: configuration.clientVersion,
    toolLoadingMode: configuration.toolLoadingMode,
  });
}

function isPinnedLimits(limits) {
  if (!limits || typeof limits !== 'object' || Array.isArray(limits))
    return false;
  const entries = Object.entries(limits);
  return entries.length > 0 && entries.every(([name, value]) => nonEmpty(name) && isPinnedValue(value));
}

function isPinnedValue(value) {
  if (value === null || value === undefined)
    return false;
  if (typeof value === 'number')
    return Number.isFinite(value) && value >= 0;
  if (typeof value === 'string')
    return value.length > 0;
  if (typeof value === 'boolean')
    return true;
  if (Array.isArray(value))
    return value.length > 0 && value.every(isPinnedValue);
  if (typeof value === 'object') {
    const entries = Object.entries(value);
    return entries.length > 0 && entries.every(([name, nested]) => nonEmpty(name) && isPinnedValue(nested));
  }
  return false;
}

function canonicalize(value) {
  if (Array.isArray(value))
    return value.map(canonicalize);
  if (!value || typeof value !== 'object')
    return value;
  return Object.fromEntries(Object.keys(value).sort().map(key => [key, canonicalize(value[key])]));
}

function isUuid(value) {
  return typeof value === 'string' &&
    /^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(value);
}

function nonEmpty(value) {
  return typeof value === 'string' && value.length > 0;
}

function digest(value) {
  return createHash('sha256').update(value).digest('hex');
}

function rejectUnless(condition, message) {
  if (!condition)
    throw new Error(`Series configuration rejected: ${message}.`);
}
