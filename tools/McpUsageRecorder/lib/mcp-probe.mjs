import { spawn } from 'node:child_process';

export async function probeMcpServer(server, client, timeoutMs = 30_000) {
  validateCommand(server, 'server');
  const child = spawn(server.command, server.args ?? [], {
    cwd: server.cwd,
    env: mergeEnvironment(server.env),
    stdio: ['pipe', 'pipe', 'pipe'],
    windowsHide: true,
  });
  const spawnFailure = new Promise((_, reject) => child.once('error', reject));
  const messages = createMessageReader(child.stdout);
  let stderr = '';
  child.stderr.setEncoding('utf8');
  child.stderr.on('data', chunk => {
    if (stderr.length < 16_384)
      stderr += chunk.slice(0, 16_384 - stderr.length);
  });
  const timer = setTimeout(() => child.kill(), timeoutMs);
  try {
    writeMessage(child, {
      jsonrpc: '2.0',
      id: 1,
      method: 'initialize',
      params: {
        protocolVersion: client.protocolVersion ?? '2025-06-18',
        capabilities: {},
        clientInfo: {
          name: client.name ?? 'mcp-usage-recorder',
          version: client.version,
        },
      },
    });
    const initialized = await nextResponse(messages, 1, timeoutMs, spawnFailure);
    if (initialized.error)
      throw new Error(`Server initialization failed: ${safeError(initialized.error)}.`);
    const instructions = initialized.result?.instructions;
    if (typeof instructions !== 'string' || instructions.length === 0)
      throw new Error('Server probe rejected: initialize did not return server instructions.');

    writeMessage(child, { jsonrpc: '2.0', method: 'notifications/initialized', params: {} });
    const toolsList = await readAllTools(child, messages, timeoutMs, spawnFailure);
    return {
      instructions,
      toolsList,
      protocolVersion: initialized.result?.protocolVersion ?? null,
      serverInfo: initialized.result?.serverInfo ?? null,
    };
  } catch (error) {
    if (stderr.trim().length > 0)
      error.message += ` Server stderr: ${stderr.trim()}`;
    throw error;
  } finally {
    clearTimeout(timer);
    if (!child.stdin.destroyed)
      child.stdin.end();
    if (!child.killed)
      child.kill();
    await waitForExit(child);
  }
}

async function readAllTools(child, messages, timeoutMs, spawnFailure) {
  const pages = [];
  const wirePages = [];
  const tools = [];
  const cursors = new Set();
  let cursor = null;
  for (let page = 0; page < 100; page++) {
    const id = page + 2;
    const params = cursor === null ? {} : { cursor };
    writeMessage(child, { jsonrpc: '2.0', id, method: 'tools/list', params });
    const listed = await nextResponse(messages, id, timeoutMs, spawnFailure);
    if (listed.error)
      throw new Error(`Server tools/list failed: ${safeError(listed.error)}.`);
    if (!Array.isArray(listed.result?.tools))
      throw new Error('Server probe rejected: tools/list did not return a tools array.');
    pages.push(listed.result);
    wirePages.push(listed.wireText);
    tools.push(...listed.result.tools);
    cursor = listed.result.nextCursor ?? null;
    if (cursor === null)
      return { tools, pages, wirePages };
    if (typeof cursor !== 'string' || cursor.length === 0 || cursors.has(cursor))
      throw new Error('Server probe rejected: tools/list pagination cursor is invalid or repeated.');
    cursors.add(cursor);
  }
  throw new Error('Server probe rejected: tools/list exceeded 100 pages.');
}

function validateCommand(value, label) {
  if (!value || typeof value !== 'object' || typeof value.command !== 'string' || value.command.length === 0)
    throw new Error(`${label} command is required.`);
  if (value.args !== undefined && (!Array.isArray(value.args) || value.args.some(argument => typeof argument !== 'string')))
    throw new Error(`${label} arguments must be strings.`);
}

function mergeEnvironment(additions) {
  if (additions === undefined)
    return process.env;
  if (!additions || typeof additions !== 'object' || Array.isArray(additions))
    throw new Error('Server environment must be an object.');
  return { ...process.env, ...additions };
}

function writeMessage(child, message) {
  child.stdin.write(`${JSON.stringify(message)}\n`);
}

function createMessageReader(stream) {
  const queue = [];
  const waiters = [];
  let buffer = '';
  let failure = null;
  stream.setEncoding('utf8');
  stream.on('data', chunk => {
    buffer += chunk;
    while (true) {
      const newline = buffer.indexOf('\n');
      if (newline < 0)
        break;
      const line = buffer.slice(0, newline).trim();
      buffer = buffer.slice(newline + 1);
      if (line.length === 0)
        continue;
      try {
        const message = JSON.parse(line);
        Object.defineProperty(message, 'wireText', { value: line });
        deliver(message);
      } catch (error) {
        rejectAll(new Error(`Server returned invalid JSON: ${error.message}`));
      }
    }
  });
  stream.on('error', rejectAll);
  stream.on('end', () => rejectAll(new Error('Server output ended before the probe completed.')));

  return { next };

  function next() {
    if (failure)
      return Promise.reject(failure);
    if (queue.length > 0)
      return Promise.resolve(queue.shift());
    return new Promise((resolve, reject) => waiters.push({ resolve, reject }));
  }

  function deliver(message) {
    const waiter = waiters.shift();
    if (waiter)
      waiter.resolve(message);
    else
      queue.push(message);
  }

  function rejectAll(error) {
    failure ??= error;
    for (const waiter of waiters.splice(0))
      waiter.reject(failure);
  }
}

async function nextResponse(reader, id, timeoutMs, spawnFailure) {
  const deadline = Date.now() + timeoutMs;
  while (true) {
    const remaining = deadline - Date.now();
    if (remaining <= 0)
      throw new Error(`Server probe timed out waiting for response ${id}.`);
    const message = await withTimeout(
      Promise.race([reader.next(), spawnFailure]),
      remaining,
      `Server probe timed out waiting for response ${id}.`);
    if (message?.id === id)
      return message;
  }
}

function withTimeout(promise, timeoutMs, message) {
  let timer;
  const timeout = new Promise((_, reject) => {
    timer = setTimeout(() => reject(new Error(message)), timeoutMs);
  });
  return Promise.race([promise, timeout]).finally(() => clearTimeout(timer));
}

function safeError(error) {
  if (typeof error?.code === 'number')
    return `code ${error.code}`;
  return 'unknown error';
}

function waitForExit(child) {
  if (!child.pid)
    return Promise.resolve();
  if (child.exitCode !== null || child.signalCode !== null)
    return Promise.resolve();
  return new Promise(resolve => child.once('exit', resolve));
}
