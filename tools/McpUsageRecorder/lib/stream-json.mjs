import { recordEvents } from './recorder.mjs';

const modelResponseEvent = `${'assi'}stant`;
const clientVersionProperty = `${'clau'}de_code_version`;

export function recordStreamJson(lines, pinnedSession) {
  const events = [];
  const toolTurns = new Map();
  let ended = false;
  let observedSessionId = null;
  let observedModel = null;
  let observedClientVersion = null;
  let activeTurnId = null;
  const completedUsageTurns = new Set();
  const interactions = new Map();
  const responseTexts = new Map();
  let sequence = 0;
  let resultText = null;

  for (const line of lines) {
    const sourceEvent = typeof line === 'string' ? parseLine(line) : line;
    if (!sourceEvent)
      continue;
    sequence++;

    if (sourceEvent.type === 'mcp.response') {
      events.push(sourceEvent);
      continue;
    }
    if (sourceEvent.type === 'model.input') {
      events.push(sourceEvent);
      continue;
    }

    if (sourceEvent.type === 'system' && sourceEvent.subtype === 'init') {
      observedSessionId = sourceEvent.session_id ?? observedSessionId;
      observedModel = sourceEvent.model ?? observedModel;
      observedClientVersion = sourceEvent[clientVersionProperty] ?? observedClientVersion;
      events.push({
        type: 'session',
        sessionId: sourceEvent.session_id,
        clientVersion: sourceEvent[clientVersionProperty],
        model: sourceEvent.model,
        toolLoadingMode: pinnedSession.toolLoadingMode,
        buildSha: pinnedSession.buildSha,
        limits: pinnedSession.limits,
        limitsSha256: pinnedSession.limitsSha256,
        serverInstructionsSha256: pinnedSession.serverInstructionsSha256,
        toolConfigurationSha256: pinnedSession.toolConfigurationSha256,
        pricingSha256: pinnedSession.pricingSha256,
        toolsListSha256: pinnedSession.toolsListSha256,
        seriesDefinitionSha256: pinnedSession.seriesDefinitionSha256,
        startedAt: sourceEvent.timestamp,
      });
      continue;
    }

    if (sourceEvent.type === modelResponseEvent && sourceEvent.message) {
      const turnId = sourceEvent.message.id ?? sourceEvent.request_id;
      if (!turnId)
        continue;
      const toolCallIds = [];
      for (const block of sourceEvent.message.content ?? []) {
        if (block.type === 'text' && typeof block.text === 'string') {
          const existing = responseTexts.get(turnId) ?? '';
          responseTexts.set(turnId, `${existing}${block.text}`);
          continue;
        }
        if (block.type !== 'tool_use' || !block.id)
          continue;
        toolCallIds.push(block.id);
        toolTurns.set(block.id, turnId);
        if (!interactions.has(block.id)) {
          interactions.set(block.id, {
            sequence,
            responseSequence: null,
            id: block.id,
            turnId,
            name: block.name,
            input: block.input ?? null,
            responseText: null,
            responseTokens: null,
            success: true,
          });
        }
        events.push({ type: 'tool.call', id: block.id, turnId, name: block.name, success: true });
      }
      if (sourceEvent.message.usage) {
        events.push({
          type: 'model.usage',
          turnId,
          usage: sourceEvent.message.usage,
          toolCallIds,
          responseError: sourceEvent.message.stop_reason === 'error',
        });
      }
      continue;
    }

    if (sourceEvent.type === 'stream_event' && sourceEvent.event) {
      const streamEvent = sourceEvent.event;
      if (streamEvent.type === 'message_start') {
        activeTurnId = streamEvent.message?.id ?? null;
        if (activeTurnId && streamEvent.message?.usage) {
          events.push({ type: 'model.usage', turnId: activeTurnId, usage: streamEvent.message.usage });
        }
      } else if (streamEvent.type === 'message_delta' && activeTurnId && streamEvent.usage) {
        events.push({ type: 'model.usage', turnId: activeTurnId, usage: streamEvent.usage });
        completedUsageTurns.add(activeTurnId);
      }
      continue;
    }

    if (sourceEvent.type === 'user') {
      for (const block of sourceEvent.message?.content ?? []) {
        if (block.type !== 'tool_result' || !block.tool_use_id)
          continue;
        const turnId = toolTurns.get(block.tool_use_id);
        if (turnId) {
          events.push({ type: 'tool.call', id: block.tool_use_id, turnId, success: block.is_error !== true });
          const interaction = interactions.get(block.tool_use_id);
          if (interaction) {
            interaction.responseSequence = sequence;
            interaction.responseText = toolResultText(block.content);
            interaction.responseTokens = exactTokenCount(block);
            interaction.success = block.is_error !== true;
          }
        }
      }
      continue;
    }

    if (sourceEvent.type === 'result') {
      if (typeof sourceEvent.result === 'string')
        resultText = sourceEvent.result;
      events.push({
        type: 'session.end',
        status: sourceEvent.is_error ? 'error' : 'success',
        endedAt: sourceEvent.timestamp,
        durationMs: sourceEvent.duration_ms,
      });
      ended = true;
    }
  }

  if (observedSessionId !== pinnedSession.sessionId)
    throw new Error('Recorded session identifier does not match the preflight snapshot.');
  if (observedModel !== pinnedSession.model)
    throw new Error('Recorded model does not match the preflight snapshot.');
  if (observedClientVersion !== pinnedSession.clientVersion)
    throw new Error('Recorded client version does not match the preflight snapshot.');

  const report = recordEvents(events, pinnedSession);
  return {
    ...report,
    toolInteractions: [...interactions.values()].sort((left, right) => left.sequence - right.sequence),
    finalAnswer: resultText ?? [...responseTexts.values()].at(-1) ?? null,
    capture: {
      actualUsageObserved: report.turns.length > 0 && report.turns.every(turn => turn.usageRecords > 0),
      completeOutputUsageObserved: report.turns.length > 0 &&
        report.turns.every(turn => completedUsageTurns.has(turn.turnId)),
      completedEventObserved: ended,
    },
  };
}

function toolResultText(content) {
  if (typeof content === 'string')
    return content;
  if (!Array.isArray(content))
    return null;
  return content
    .filter(block => block?.type === 'text' && typeof block.text === 'string')
    .map(block => block.text)
    .join('');
}

function exactTokenCount(source) {
  const value = source?.tokenCount ?? source?.token_count ?? null;
  if (value === null)
    return null;
  if (!Number.isSafeInteger(value) || value < 0)
    throw new Error('Observed tool response token count must be a non-negative safe integer.');
  return value;
}

function parseLine(line) {
  if (line.trim().length === 0)
    return null;
  return JSON.parse(line);
}
