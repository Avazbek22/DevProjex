import { createHash } from 'node:crypto';
import { createInterface } from 'node:readline';

const usageNames = Object.freeze({
  inputTokens: ['inputTokens', 'input_tokens'],
  cacheWriteTokens: [
    'cacheWriteTokens',
    'cache_write_input_tokens',
    'cache_creation_input_tokens',
  ],
  cacheReadTokens: ['cacheReadTokens', 'cache_read_input_tokens'],
  outputTokens: ['outputTokens', 'output_tokens'],
});

export async function recordCapture(readable, defaults = {}) {
  const state = createState(defaults);
  const lines = createInterface({ input: readable, crlfDelay: Infinity });
  let lineNumber = 0;
  for await (const line of lines) {
    lineNumber++;
    if (line.trim().length === 0)
      continue;
    let event;
    try {
      event = JSON.parse(line);
    } catch (error) {
      throw new Error(`Invalid JSON on capture line ${lineNumber}: ${error.message}`);
    }
    applyEvent(state, event, lineNumber);
  }
  return finish(state);
}

export function recordEvents(events, defaults = {}) {
  const state = createState(defaults);
  events.forEach((event, index) => applyEvent(state, event, index + 1));
  return finish(state);
}

function createState(defaults) {
  return {
    metadata: {
      clientVersion: defaults.clientVersion ?? null,
      model: defaults.model ?? null,
      toolLoadingMode: defaults.toolLoadingMode ?? null,
    },
    startedAt: defaults.startedAt ?? null,
    endedAt: null,
    durationMs: defaults.durationMs ?? null,
    status: null,
    responses: [],
    modelInputs: [],
    turns: new Map(),
    toolCalls: new Map(),
    diagnostics: [],
  };
}

function applyEvent(state, event, lineNumber) {
  if (!event || typeof event !== 'object' || Array.isArray(event))
    throw new Error(`Capture line ${lineNumber} must contain an object.`);
  switch (event.type) {
    case 'session':
      state.metadata.clientVersion = scalar(event.clientVersion, state.metadata.clientVersion);
      state.metadata.model = scalar(event.model, state.metadata.model);
      state.metadata.toolLoadingMode = scalar(event.toolLoadingMode, state.metadata.toolLoadingMode);
      state.startedAt = scalar(event.startedAt, state.startedAt);
      break;
    case 'mcp.response':
      captureResponse(state, event, lineNumber);
      break;
    case 'model.input':
      captureModelInput(state, event, lineNumber);
      break;
    case 'model.usage':
      captureUsage(state, event, lineNumber);
      break;
    case 'tool.call':
      captureToolCall(state, event, lineNumber);
      break;
    case 'session.end':
      state.status = normalizeStatus(event.status);
      state.endedAt = scalar(event.endedAt, state.endedAt);
      state.durationMs = nonNegativeNumber(event.durationMs, 'durationMs', lineNumber, true) ?? state.durationMs;
      break;
    default:
      throw new Error(`Unknown capture event type on line ${lineNumber}: ${String(event.type)}`);
  }
}

function captureResponse(state, event, lineNumber) {
  const wire = readWire(event, lineNumber);
  const decoded = optionalText(event.decodedText, 'decodedText', lineNumber);
  const response = {
    requestId: scalar(event.requestId, null),
    turnId: scalar(event.turnId, null),
    success: event.success !== false,
    wireBytes: wire.length,
    wireSha256: digest(wire),
    decodedObserved: decoded !== null,
    decodedBytes: decoded === null ? null : Buffer.byteLength(decoded),
    decodedCharacters: decoded?.length ?? null,
    decodedSha256: decoded === null ? null : digest(Buffer.from(decoded)),
  };
  state.responses.push(response);
}

function captureModelInput(state, event, lineNumber) {
  const turnId = requiredId(event.turnId, 'turnId', lineNumber);
  const text = optionalText(event.text, 'text', lineNumber);
  const byteCount = nonNegativeNumber(event.bytes, 'bytes', lineNumber, true);
  if (text === null && byteCount === null)
    throw new Error(`model.input on line ${lineNumber} requires text or bytes.`);
  state.modelInputs.push({
    turnId,
    observed: event.observed !== false,
    bytes: text === null ? byteCount : Buffer.byteLength(text),
    characters: text?.length ?? null,
    sha256: text === null ? scalar(event.sha256, null) : digest(Buffer.from(text)),
  });
}

function captureUsage(state, event, lineNumber) {
  const turnId = requiredId(event.turnId, 'turnId', lineNumber);
  let turn = state.turns.get(turnId);
  if (!turn) {
    turn = {
      turnId,
      usage: emptyUsage(),
      usageRecords: 0,
      toolCallIds: new Set(),
      responseError: false,
    };
    state.turns.set(turnId, turn);
  }
  turn.usageRecords++;
  for (const [target, aliases] of Object.entries(usageNames)) {
    const value = firstNumber(event.usage ?? event, aliases, lineNumber);
    if (value !== null)
      turn.usage[target] = Math.max(turn.usage[target], value);
  }
  if (Array.isArray(event.toolCallIds)) {
    for (const id of event.toolCallIds)
      turn.toolCallIds.add(requiredId(id, 'toolCallIds[]', lineNumber));
  }
  turn.responseError ||= event.responseError === true;
}

function captureToolCall(state, event, lineNumber) {
  const id = requiredId(event.id, 'id', lineNumber);
  const turnId = requiredId(event.turnId, 'turnId', lineNumber);
  const existing = state.toolCalls.get(id);
  if (existing && existing.turnId !== turnId)
    throw new Error(`tool.call '${id}' is associated with multiple turns.`);
  state.toolCalls.set(id, {
    id,
    turnId,
    name: scalar(event.name, existing?.name ?? null),
    success: event.success !== false,
  });
  let turn = state.turns.get(turnId);
  if (!turn) {
    turn = {
      turnId,
      usage: emptyUsage(),
      usageRecords: 0,
      toolCallIds: new Set(),
      responseError: false,
    };
    state.turns.set(turnId, turn);
  }
  turn.toolCallIds.add(id);
}

function finish(state) {
  const turns = [...state.turns.values()].map(turn => ({
    turnId: turn.turnId,
    usage: turn.usage,
    usageRecords: turn.usageRecords,
    toolCalls: turn.toolCallIds.size,
    responseError: turn.responseError,
  }));
  const usage = turns.reduce((sum, turn) => addUsage(sum, turn.usage), emptyUsage());
  const decodedObserved = state.responses.filter(response => response.decodedObserved).length;
  const inputObserved = state.modelInputs.filter(input => input.observed).length;
  const status = state.status ?? 'aborted';
  return {
    schemaVersion: 1,
    session: {
      ...state.metadata,
      startedAt: state.startedAt,
      endedAt: state.endedAt,
      durationMs: state.durationMs,
      status,
      successful: status === 'success',
    },
    totals: {
      wireResponseBytes: sum(state.responses, 'wireBytes'),
      decodedResponseBytes: sumNullable(state.responses, 'decodedBytes'),
      decodedResponsesObserved: decodedObserved,
      decodedResponsesUnobserved: state.responses.length - decodedObserved,
      modelInputBytes: sumNullable(state.modelInputs.filter(input => input.observed), 'bytes'),
      modelInputsObserved: inputObserved,
      modelInputsUnobserved: state.modelInputs.length - inputObserved,
      modelTurns: turns.length,
      toolCalls: state.toolCalls.size,
      failedToolCalls: [...state.toolCalls.values()].filter(call => !call.success).length,
      failedMcpResponses: state.responses.filter(response => !response.success).length,
      usage,
    },
    turns,
    responses: state.responses,
    modelInputs: state.modelInputs,
    toolCalls: [...state.toolCalls.values()],
    diagnostics: state.diagnostics,
  };
}

function readWire(event, lineNumber) {
  const hasBase64 = event.wireBase64 !== undefined;
  const hasText = event.wireText !== undefined;
  if (hasBase64 === hasText)
    throw new Error(`mcp.response on line ${lineNumber} requires exactly one of wireBase64 or wireText.`);
  if (hasBase64) {
    if (typeof event.wireBase64 !== 'string')
      throw new Error(`wireBase64 on line ${lineNumber} must be a string.`);
    return Buffer.from(event.wireBase64, 'base64');
  }
  if (typeof event.wireText !== 'string')
    throw new Error(`wireText on line ${lineNumber} must be a string.`);
  return Buffer.from(event.wireText);
}

function firstNumber(source, names, lineNumber) {
  for (const name of names) {
    if (source[name] !== undefined)
      return nonNegativeNumber(source[name], name, lineNumber, false);
  }
  return null;
}

function emptyUsage() {
  return { inputTokens: 0, cacheWriteTokens: 0, cacheReadTokens: 0, outputTokens: 0 };
}

function addUsage(left, right) {
  for (const name of Object.keys(left))
    left[name] += right[name];
  return left;
}

function sum(values, property) {
  return values.reduce((total, value) => total + value[property], 0);
}

function sumNullable(values, property) {
  const observed = values.map(value => value[property]).filter(value => value !== null);
  return observed.length === 0 ? null : observed.reduce((total, value) => total + value, 0);
}

function digest(value) {
  return createHash('sha256').update(value).digest('hex');
}

function optionalText(value, name, lineNumber) {
  if (value === undefined || value === null)
    return null;
  if (typeof value !== 'string')
    throw new Error(`${name} on line ${lineNumber} must be a string.`);
  return value;
}

function requiredId(value, name, lineNumber) {
  if (typeof value !== 'string' || value.length === 0)
    throw new Error(`${name} on line ${lineNumber} must be a non-empty string.`);
  return value;
}

function nonNegativeNumber(value, name, lineNumber, nullable) {
  if (nullable && (value === undefined || value === null))
    return null;
  if (!Number.isSafeInteger(value) || value < 0)
    throw new Error(`${name} on line ${lineNumber} must be a non-negative safe integer.`);
  return value;
}

function scalar(value, fallback) {
  return value === undefined || value === null ? fallback : value;
}

function normalizeStatus(value) {
  if (value === 'success' || value === 'error' || value === 'aborted')
    return value;
  throw new Error(`session.end status must be success, error, or aborted.`);
}
