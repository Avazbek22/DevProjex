import { execFile } from 'node:child_process';
import { promisify } from 'node:util';
import { readFile, writeFile, mkdir } from 'node:fs/promises';
import { resolve, join } from 'node:path';
import { startMcpReachabilityClient } from '../../tools/McpUsageRecorder/lib/mcp-reachability-client.mjs';
import { normalizeCliRelated, parseMcpRelated, compareRelated } from '../../tools/DependencyLiveValidation/lib/live-validation.mjs';

const [rootValue, scanPath, outputPath, dataPath] = process.argv.slice(2);
const root = resolve(rootValue);
const assembly = resolve('Apps/TerminalHost/bin/Release/net10.0/devprojex.dll');
const scan = JSON.parse(await readFile(scanPath, 'utf8'));
const capture = promisify(execFile);
await mkdir(dataPath, { recursive: true });
await mkdir(join(dataPath, 'mcp'), { recursive: true });
await mkdir(join(dataPath, 'cli'), { recursive: true });
const client = await startMcpReachabilityClient({ command: 'dotnet', args: [assembly, 'mcp', '--root', root],
  cwd: root, env: { DEVPROJEX_INTERNAL_DATA_ROOT: join(dataPath, 'mcp') } }, 300_000);
const samples = [];
try {
  for (const sample of scan.samples) {
    const cli = await capture('dotnet', [assembly, 'related', sample.Path, '--project', root, '--direction', 'dependencies',
      '--format', 'json', '--git-mode', 'none', '--exclude', 'none', '--progress', 'never', '--plain', '--language', 'en'],
    { cwd: root, env: { ...process.env, DEVPROJEX_INTERNAL_DATA_ROOT: join(dataPath, 'cli') }, windowsHide: true,
      timeout: 300_000, maxBuffer: 16 * 1024 * 1024 });
    const mcp = await client.callAndPage('related_files', { path: sample.Path, direction: 'dependencies' });
    if (mcp.isError) throw new Error(mcp.allText);
    const parsed = parseMcpRelated(mcp.allText);
    parsed.seed ??= sample.Path;
    const parity = compareRelated(normalizeCliRelated(JSON.parse(cli.stdout))[0], parsed);
    if (!parity.equal) throw new Error(JSON.stringify(parity));
    const declaration = sample.NavigationDeclarations.find(item => item.Kind === 'Method' || item.Kind === 'Function');
    let navigation = null;
    if (declaration) {
      const name = declaration.Name.split(/[.#]/).filter(Boolean).findLast(part => !/^\d+$/.test(part));
      const search = await client.callAndPage('search_project', { pattern: name.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'),
        include_patterns: [sample.Path], context_lines: 0 });
      if (search.isError || !search.allText.includes(`in ${declaration.Name}`)) throw new Error(search.allText);
      const content = await client.callAndPage('get_file', { path: sample.Path, symbol: declaration.Name });
      if (content.isError) throw new Error(content.allText);
      navigation = declaration.Name;
    }
    samples.push({ path: sample.Path, equal: true, navigation, cli: parity.cli });
  }
} finally { await client.close(); }
await writeFile(outputPath, JSON.stringify(samples, null, 2));
console.log(`${root}: ${samples.length} matching samples`);
