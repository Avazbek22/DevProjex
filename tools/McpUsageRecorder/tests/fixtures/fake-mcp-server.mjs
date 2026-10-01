import { createInterface } from 'node:readline';

const arm = process.argv[2] ?? 'unknown';
const lines = createInterface({ input: process.stdin, crlfDelay: Infinity });
for await (const line of lines) {
  const message = JSON.parse(line);
  if (message.method === 'initialize') {
    respond(message.id, {
      protocolVersion: message.params.protocolVersion,
      serverInfo: { name: `fixture-${arm}`, version: '1.0.0' },
      instructions: `Pinned ${arm} instructions.`,
      capabilities: { tools: {} },
    });
  } else if (message.method === 'tools/list') {
    const secondPage = message.params?.cursor === 'next-page';
    respond(message.id, secondPage
      ? { tools: [{
        name: 'get_tree',
        description: 'Fixture tree tool.',
        inputSchema: { type: 'object', properties: {} },
      }] }
      : {
        tools: [{
          name: arm === 'left' ? 'get_file' : 'search_project',
          description: `Fixture ${arm} tool.`,
          inputSchema: { type: 'object', properties: {} },
        }],
        nextCursor: 'next-page',
      });
  }
}

function respond(id, result) {
  process.stdout.write(`${JSON.stringify({ jsonrpc: '2.0', id, result })}\n`);
}
