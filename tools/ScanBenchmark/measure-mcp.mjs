import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { StdioClientTransport } from '@modelcontextprotocol/sdk/client/stdio.js';
import { encode } from 'gpt-tokenizer/model/gpt-4o';
import { writeFile } from 'node:fs/promises';
import process from 'node:process';

const [command, root, outputPath, ...prefixArguments] = process.argv.slice(2);
if (!command || !root || !outputPath) {
  throw new Error(
    'Usage: node measure-mcp.mjs <devprojex-command> <flask-root> <output.json> [command prefix arguments...]',
  );
}

const question = 'Find where the session cookie is signed and which configuration keys affect it.';

function responseText(result) {
  return (result.content ?? [])
    .filter((item) => item.type === 'text')
    .map((item) => item.text)
    .join('\n');
}

function estimatedTokens(text) {
  // o200k_base is the calibration reference. The documented figure remains an
  // estimate because the consuming model/tokenizer may differ.
  return encode(text).length;
}

function extractSourcePaths(text) {
  const matches = text.match(/src\/[A-Za-z0-9_.\/-]+\.py/g) ?? [];
  return [...new Set(matches.map((value) => value.replace(/[.:,;]+$/, '')))];
}

async function consumeStoredPack(call, packResponse) {
  const match = packResponse.match(
    /Pack stored as '[^']+' \(([\d\s,.]+) characters, ([\d\s,.]+) lines\)\. Call read_pack/,
  );
  if (!match) {
    return { stored: false, readPackCalls: 0 };
  }
  const packId = packResponse.match(/Pack stored as '([^']+)'/)?.[1];
  if (!packId) {
    throw new Error('Stored pack response did not contain a pack id.');
  }
  const parseCount = (value) => Number.parseInt(value.replace(/\D/g, ''), 10);
  const characters = parseCount(match[1]);
  const lines = parseCount(match[2]);
  const pageLines = 500;
  let readPackCalls = 0;
  for (let startLine = 1; startLine <= lines; startLine += pageLines) {
    await call('read_pack', {
      pack_id: packId,
      start_line: startLine,
      end_line: Math.min(lines, startLine + pageLines - 1),
    });
    readPackCalls += 1;
  }
  return { stored: true, storedCharacters: characters, storedLines: lines, readPackCalls };
}

async function runScenario(name, action) {
  const transport = new StdioClientTransport({
    command,
    args: [...prefixArguments, 'mcp', '--root', root],
    stderr: 'pipe',
  });
  const client = new Client(
    { name: 'devprojex-pack-exploration-benchmark', version: '1.0.0' },
    { capabilities: {} },
  );
  const calls = [];
  try {
    await client.connect(transport);
    const call = async (toolName, args) => {
      const result = await client.callTool({ name: toolName, arguments: args });
      if (result.isError) {
        throw new Error(`${toolName} failed: ${responseText(result)}`);
      }
      const text = responseText(result);
      calls.push({
        tool: toolName,
        responseCharacters: text.length,
        estimatedTokens: estimatedTokens(text),
      });
      return text;
    };
    const details = await action(call);
    return {
      name,
      question,
      callCount: calls.length,
      responseCharacters: calls.reduce((sum, item) => sum + item.responseCharacters, 0),
      estimatedTokens: calls.reduce((sum, item) => sum + item.estimatedTokens, 0),
      calls,
      ...details,
    };
  } finally {
    await client.close();
  }
}

const packFirst = await runScenario('pack-first', async (call) => {
  const packResponse = await call('pack_context', {
    paths: ['src'],
    view: 'tree-content',
    format: 'markdown',
  });
  return { packedPaths: ['src'], ...(await consumeStoredPack(call, packResponse)) };
});

const exploration = await runScenario('exploration', async (call) => {
  await call('get_tree', {
    include_patterns: ['src/**'],
    max_depth: 4,
    format: 'text',
  });
  const signingMatches = await call('search_project', {
    pattern: 'signer|signing_serializer|URLSafeTimedSerializer',
    include_patterns: ['src/**'],
    max_results: 30,
  });
  const configurationMatches = await call('search_project', {
    pattern: 'SECRET_KEY_FALLBACKS|SESSION_COOKIE_|PERMANENT_SESSION_LIFETIME',
    include_patterns: ['src/**'],
    max_results: 50,
  });
  const paths = [...new Set([
    ...extractSourcePaths(signingMatches),
    ...extractSourcePaths(configurationMatches),
  ])].sort();
  if (paths.length === 0) {
    throw new Error('Exploration did not discover any Flask source files.');
  }
  for (const path of paths) {
    await call('get_file', { path });
  }
  const packResponse = await call('pack_context', {
    paths,
    view: 'tree-content',
    format: 'markdown',
  });
  return { packedPaths: paths, ...(await consumeStoredPack(call, packResponse)) };
});

const report = {
  schemaVersion: 1,
  measuredUtc: new Date().toISOString(),
  root,
  calibration: {
    tokenizer: 'o200k_base (gpt-tokenizer 4.0.0, gpt-4o model mapping)',
    interpretation: 'local response estimate; report as ±5% because a consuming model may tokenize differently',
  },
  scenarios: [packFirst, exploration],
};
await writeFile(outputPath, `${JSON.stringify(report, null, 2)}\n`, 'utf8');
process.stdout.write(`${JSON.stringify(report, null, 2)}\n`);
