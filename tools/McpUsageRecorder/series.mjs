#!/usr/bin/env node
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { dirname, resolve } from 'node:path';
import process from 'node:process';
import {
  createRunRecord,
  createSeries,
  readSeriesManifest,
  resumeSeries,
  storeRunRecord,
  summarizeSeries,
} from './lib/series-store.mjs';

try {
  const command = process.argv[2];
  const options = parseOptions(process.argv.slice(3));
  switch (command) {
    case 'new': {
      allowOnly(options, ['root', 'configuration']);
      const configuration = await readJson(required(options, 'configuration'));
      const result = await createSeries(required(options, 'root'), configuration);
      writeJson({ status: 'created', directory: result.directory, manifest: result.manifest });
      break;
    }
    case 'resume': {
      allowOnly(options, ['series', 'configuration']);
      const configuration = await readJson(required(options, 'configuration'));
      const result = await resumeSeries(required(options, 'series'), configuration);
      writeJson({ status: 'resumed', directory: result.directory, manifest: result.manifest });
      break;
    }
    case 'append': {
      allowOnly(options, ['series', 'report', 'task', 'repetition', 'arm', 'attempt']);
      const seriesDirectory = required(options, 'series');
      const manifest = await readSeriesManifest(seriesDirectory);
      const report = await readJson(required(options, 'report'));
      const repetition = Number(required(options, 'repetition'));
      const record = createRunRecord(manifest, {
        task: required(options, 'task'),
        repetition,
        arm: required(options, 'arm'),
        attempt: options.has('attempt') ? Number(options.get('attempt')) : 1,
      }, report);
      const result = await storeRunRecord(seriesDirectory, record);
      writeJson({ ...result, identity: record.identity });
      break;
    }
    case 'summarize': {
      allowOnly(options, ['series', 'output']);
      const summary = await summarizeSeries(required(options, 'series'));
      const output = options.get('output') ?? '-';
      const json = `${JSON.stringify(summary, null, 2)}\n`;
      if (output === '-') {
        process.stdout.write(json);
      } else {
        const outputPath = resolve(output);
        await mkdir(dirname(outputPath), { recursive: true });
        await writeFile(outputPath, json, { encoding: 'utf8' });
      }
      break;
    }
    case '--help':
    case '-h':
    case 'help':
      process.stdout.write(usage());
      break;
    default:
      throw new Error(`Unknown command: ${command ?? '<missing>'}.`);
  }
} catch (error) {
  process.stderr.write(`${error.message}\n${usage()}`);
  process.exitCode = 2;
}

function parseOptions(args) {
  const options = new Map();
  for (let index = 0; index < args.length; index += 2) {
    const name = args[index];
    const value = args[index + 1];
    if (!name?.startsWith('--') || value === undefined)
      throw new Error(`Invalid option sequence near '${name ?? '<end>'}'.`);
    if (options.has(name.slice(2)))
      throw new Error(`Option '${name}' was provided more than once.`);
    options.set(name.slice(2), value);
  }
  return options;
}

function required(options, name) {
  const value = options.get(name);
  if (!value)
    throw new Error(`--${name} is required.`);
  return value;
}

function allowOnly(options, allowed) {
  const expected = new Set(allowed);
  for (const name of options.keys())
    if (!expected.has(name))
      throw new Error(`Option '--${name}' is not valid for this command.`);
}

async function readJson(path) {
  return JSON.parse(await readFile(resolve(path), 'utf8'));
}

function writeJson(value) {
  process.stdout.write(`${JSON.stringify(value, null, 2)}\n`);
}

function usage() {
  return [
    'Usage:',
    '  node tools/McpUsageRecorder/series.mjs new --root DIR --configuration FILE',
    '  node tools/McpUsageRecorder/series.mjs resume --series DIR --configuration FILE',
    '  node tools/McpUsageRecorder/series.mjs append --series DIR --report FILE --task ID --repetition N --arm ID [--attempt N]',
    '  node tools/McpUsageRecorder/series.mjs summarize --series DIR [--output FILE]',
    '',
  ].join('\n');
}
