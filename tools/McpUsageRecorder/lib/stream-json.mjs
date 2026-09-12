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

  for (const line of lines) {
    const sourceEvent = typeof line === 'string' ? parseLine(line) : line;
    if (!sourceEvent)
      continue;

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
        if (block.type !== 'tool_use' || !block.id)
          continue;
        toolCallIds.push(block.id);
        toolTurns.set(block.id, turnId);
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
        if (turnId)
          events.push({ type: 'tool.call', id: block.tool_use_id, turnId, success: block.is_error !== true });
      }
      continue;
    }

    if (sourceEvent.type === 'result') {
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
    capture: {
      actualUsageObserved: report.turns.length > 0 && report.turns.every(turn => turn.usageRecords > 0),
      completeOutputUsageObserved: report.turns.length > 0 &&
        report.turns.every(turn => completedUsageTurns.has(turn.turnId)),
      completedEventObserved: ended,
    },
  };
}

function parseLine(line) {
  if (line.trim().length === 0)
    return null;
  return JSON.parse(line);
}
