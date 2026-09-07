import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { StdioClientTransport } from '@modelcontextprotocol/sdk/client/stdio.js';
import { performance } from 'node:perf_hooks';
import { writeFile } from 'node:fs/promises';
import path from 'node:path';
import process from 'node:process';

const [command, root, seedPath, outputPath, ...prefixArguments] = process.argv.slice(2);
if (!command || !root || !seedPath || !outputPath) {
  throw new Error(
    'Usage: node measure-mcp-warm.mjs <devprojex-command> <root> <seed-path> <output.json> [command prefix arguments...]',
  );
}

function responseText(result) {
  return (result.content ?? [])
    .filter((item) => item.type === 'text')
    .map((item) => item.text)
    .join('\n');
}

const transport = new StdioClientTransport({
  command,
  args: [...prefixArguments, 'mcp', '--root', root],
  stderr: 'pipe',
});
const client = new Client(
  { name: 'devprojex-warm-session-benchmark', version: '1.0.0' },
  { capabilities: {} },
);

async function call(tool, args) {
  const started = performance.now();
  const result = await client.callTool({ name: tool, arguments: args });
  const elapsedMilliseconds = performance.now() - started;
  if (result.isError) {
    throw new Error(`${tool} failed: ${responseText(result)}`);
  }
  return {
    tool,
    elapsedMilliseconds,
    responseCharacters: responseText(result).length,
  };
}

async function runPass(ordinal) {
  const directory = path.posix.dirname(seedPath);
  const includePattern = directory === '.' ? '**' : `${directory}/**`;
  const calls = [];
  calls.push(await call('get_tree', {
    include_patterns: [includePattern],
    max_depth: 2,
    format: 'text',
  }));
  calls.push(await call('search_project', {
    pattern: 'class|function|using|import',
    include_patterns: [includePattern],
    max_results: 20,
  }));
  calls.push(await call('get_file', { path: seedPath, start_line: 1, end_line: 80 }));
  return {
    ordinal,
    elapsedMilliseconds: calls.reduce((sum, item) => sum + item.elapsedMilliseconds, 0),
    calls,
  };
}

try {
  await client.connect(transport);
  const passes = [await runPass(1), await runPass(2)];
  const report = {
    schemaVersion: 1,
    measuredUtc: new Date().toISOString(),
    root,
    seedPath,
    definition: 'two identical narrow tree/search/read sequences in one initialized MCP server process',
    passes,
    warmSpeedup: passes[0].elapsedMilliseconds / passes[1].elapsedMilliseconds,
  };
  await writeFile(outputPath, `${JSON.stringify(report, null, 2)}\n`, 'utf8');
  process.stdout.write(`${JSON.stringify(report, null, 2)}\n`);
} finally {
  await client.close();
}
