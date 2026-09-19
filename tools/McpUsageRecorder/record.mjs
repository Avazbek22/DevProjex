#!/usr/bin/env node
import { createReadStream, createWriteStream } from 'node:fs';
import { mkdir } from 'node:fs/promises';
import { dirname, resolve } from 'node:path';
import process from 'node:process';
import { recordCapture } from './lib/recorder.mjs';

const options = parseArguments(process.argv.slice(2));
const input = options.input === '-'
  ? process.stdin
  : createReadStream(resolve(options.input), { encoding: 'utf8' });
const report = await recordCapture(input, {
  clientVersion: options.clientVersion,
  model: options.model,
  toolLoadingMode: options.toolLoadingMode,
});
const json = JSON.stringify(report, null, 2) + '\n';
if (options.output === '-') {
  process.stdout.write(json);
} else {
  const outputPath = resolve(options.output);
  await mkdir(dirname(outputPath), { recursive: true });
  await new Promise((resolveWrite, rejectWrite) => {
    const output = createWriteStream(outputPath, { encoding: 'utf8' });
    output.on('error', rejectWrite);
    output.on('finish', resolveWrite);
    output.end(json);
  });
}

function parseArguments(args) {
  const options = { input: '-', output: '-' };
  for (let index = 0; index < args.length; index++) {
    const name = args[index];
    if (name === '--help') {
      process.stdout.write(usage());
      process.exit(0);
    }
    if (!['--input', '--output', '--client-version', '--model', '--tool-loading-mode'].includes(name))
      fail(`Unknown option: ${name}`);
    const value = args[++index];
    if (!value)
      fail(`Missing value for ${name}.`);
    const property = name.slice(2).replace(/-([a-z])/g, (_, letter) => letter.toUpperCase());
    options[property] = value;
  }
  return options;
}

function fail(message) {
  process.stderr.write(`${message}\n${usage()}`);
  process.exit(2);
}

function usage() {
  return 'Usage: node tools/McpUsageRecorder/record.mjs --input capture.ndjson --output report.json ' +
    '[--client-version VERSION] [--model MODEL] [--tool-loading-mode MODE]\n';
}
