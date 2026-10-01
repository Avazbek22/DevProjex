import { spawn } from 'node:child_process';

export async function startMcpReachabilityClient(server, timeoutMs = 120_000) {
  const child = spawn(server.command, server.args ?? [], {
    cwd: server.cwd,
    env: { ...process.env, ...(server.env ?? {}) },
    stdio: ['pipe', 'pipe', 'pipe'],
    windowsHide: true,
  });
  const messages = createMessageReader(child.stdout);
  const spawnFailure = new Promise((_, reject) => child.once('error', reject));
  let stderr = '';
  child.stderr.setEncoding('utf8');
  child.stderr.on('data', chunk => {
    if (stderr.length < 32_768)
      stderr += chunk.slice(0, 32_768 - stderr.length);
  });
  let nextId = 1;

  await request('initialize', {
    protocolVersion: '2025-06-18',
    capabilities: {},
    clientInfo: { name: 'devprojex-reachability', version: '1' },
  });
  write({ jsonrpc: '2.0', method: 'notifications/initialized', params: {} });

  return { call, callAndPage, close };

  async function call(name, argumentsValue) {
    const response = await request('tools/call', { name, arguments: argumentsValue });
    const result = response.result ?? {};
    return {
      isError: result.isError === true,
      text: Array.isArray(result.content)
        ? result.content.filter(item => item?.type === 'text').map(item => item.text ?? '').join('\n')
        : '',
      structuredContent: result.structuredContent ?? null,
      raw: result,
    };
  }

  async function callAndPage(name, argumentsValue) {
    const first = await call(name, argumentsValue);
    const packId = extractPackId(first.text);
    if (!packId)
      return { ...first, pages: [], allText: first.text };
    const pages = [];
    let startLine = 1;
    for (let page = 0; page < 100; page++) {
      const current = await call('read_pack', { pack_id: packId, start_line: startLine });
      pages.push(current.text);
      const next = /continue with start_line=(\d+)/.exec(current.text);
      if (!next)
        return { ...first, pages, allText: `${first.text}\n${pages.join('\n')}` };
      const parsed = Number(next[1]);
      if (!Number.isSafeInteger(parsed) || parsed <= startLine)
        throw new Error(`Stored result '${packId}' returned an invalid continuation.`);
      startLine = parsed;
    }
    throw new Error(`Stored result '${packId}' exceeded 100 pages.`);
  }

  async function request(method, params) {
    const id = nextId++;
    write({ jsonrpc: '2.0', id, method, params });
    const deadline = Date.now() + timeoutMs;
    while (true) {
      const remaining = deadline - Date.now();
      if (remaining <= 0)
        throw new Error(`MCP request '${method}' timed out.${stderrText()}`);
      const message = await withTimeout(Promise.race([messages.next(), spawnFailure]), remaining,
        `MCP request '${method}' timed out.${stderrText()}`);
      if (message?.id !== id)
        continue;
      if (message.error)
        throw new Error(`MCP request '${method}' failed with code ${message.error.code}.${stderrText()}`);
      return message;
    }
  }

  function write(message) {
    child.stdin.write(`${JSON.stringify(message)}\n`);
  }

  async function close() {
    if (!child.stdin.destroyed)
      child.stdin.end();
    await withTimeout(waitForExit(child), 15_000, 'MCP server did not stop after input closed.')
      .catch(async error => {
        child.kill();
        await waitForExit(child);
        throw error;
      });
    if (child.exitCode !== 0)
      throw new Error(`MCP server exited with code ${child.exitCode}.${stderrText()}`);
  }

  function stderrText() {
    return stderr.trim().length === 0 ? '' : ` Server stderr: ${stderr.trim()}`;
  }
}

function extractPackId(text) {
  return /\[Search stored\] pack_id=([^ ·;\r\n]+)/.exec(text)?.[1] ??
    /Related-files result stored as '([^']+)'/.exec(text)?.[1] ?? null;
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
        deliver(JSON.parse(line));
      } catch (error) {
        rejectAll(new Error(`MCP server returned invalid JSON: ${error.message}`));
      }
    }
  });
  stream.on('error', rejectAll);
  stream.on('end', () => rejectAll(new Error('MCP server output ended before the response arrived.')));
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

function withTimeout(promise, timeoutMs, message) {
  let timer;
  const timeout = new Promise((_, reject) => {
    timer = setTimeout(() => reject(new Error(message)), timeoutMs);
  });
  return Promise.race([promise, timeout]).finally(() => clearTimeout(timer));
}

function waitForExit(child) {
  if (!child.pid || child.exitCode !== null || child.signalCode !== null)
    return Promise.resolve();
  return new Promise(resolve => child.once('exit', resolve));
}
