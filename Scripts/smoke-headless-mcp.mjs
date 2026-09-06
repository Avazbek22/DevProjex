import { spawn } from 'node:child_process';

const [command, root, ...prefixArguments] = process.argv.slice(2);
if (!command || !root) {
  throw new Error('Usage: node smoke-headless-mcp.mjs <command> <root> [prefix arguments...]');
}

const child = spawn(command, [...prefixArguments, 'mcp', '--root', root], {
  shell: false,
  windowsHide: true,
  stdio: ['pipe', 'pipe', 'pipe'],
});
let stdout = '';
let stderr = '';
let settled = false;

const result = new Promise((resolve, reject) => {
  const timeout = setTimeout(() => reject(new Error(`MCP initialize timed out. stderr: ${stderr}`)), 30_000);
  child.stderr.setEncoding('utf8');
  child.stderr.on('data', (chunk) => { stderr += chunk; });
  child.stdout.setEncoding('utf8');
  child.stdout.on('data', (chunk) => {
    stdout += chunk;
    for (const line of stdout.split(/\r?\n/)) {
      if (!line.trim()) continue;
      try {
        const message = JSON.parse(line);
        if (message.id === 1 && message.result?.serverInfo) {
          clearTimeout(timeout);
          settled = true;
          resolve(message.result);
          return;
        }
      } catch {
        // Wait for a complete JSON line.
      }
    }
  });
  child.on('error', (error) => {
    clearTimeout(timeout);
    reject(error);
  });
  child.on('exit', (code) => {
    if (!settled) {
      clearTimeout(timeout);
      reject(new Error(`MCP server exited ${code}. stderr: ${stderr}`));
    }
  });
});

child.stdin.write(`${JSON.stringify({
  jsonrpc: '2.0',
  id: 1,
  method: 'initialize',
  params: {
    protocolVersion: '2025-06-18',
    capabilities: {},
    clientInfo: { name: 'devprojex-headless-smoke', version: '1.0.0' },
  },
})}\n`);

try {
  const initialized = await result;
  if (!initialized.protocolVersion) throw new Error('MCP initialize response has no protocolVersion.');
  process.stdout.write(`MCP initialize OK: ${initialized.serverInfo.name}\n`);
} finally {
  child.stdin.end();
  child.kill();
}
