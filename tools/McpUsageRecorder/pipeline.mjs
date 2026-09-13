#!/usr/bin/env node
import process from 'node:process';
import { runPipeline } from './lib/pipeline-runner.mjs';

try {
  const options = parseOptions(process.argv.slice(2));
  if (options.has('help')) {
    process.stdout.write(usage());
  } else {
    const mode = required(options, 'mode');
    if (mode !== 'new' && mode !== 'resume')
      throw new Error('--mode must be new or resume.');
    const result = await runPipeline({
      mode,
      definitionPath: required(options, 'definition'),
      rootDirectory: mode === 'new' ? required(options, 'root') : undefined,
      seriesDirectory: mode === 'resume' ? required(options, 'series') : undefined,
    });
    process.stdout.write(`${JSON.stringify(result, null, 2)}\n`);
  }
} catch (error) {
  process.stderr.write(`${error.message}\n${usage()}`);
  process.exitCode = 2;
}

function parseOptions(args) {
  const options = new Map();
  for (let index = 0; index < args.length; index++) {
    const name = args[index];
    if (name === '--help') {
      options.set('help', true);
      continue;
    }
    if (!['--mode', '--definition', '--root', '--series'].includes(name))
      throw new Error(`Unknown option '${name}'.`);
    const value = args[++index];
    if (!value)
      throw new Error(`${name} requires a value.`);
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

function usage() {
  return [
    'Usage:',
    '  node tools/McpUsageRecorder/pipeline.mjs --mode new --definition FILE --root DIR',
    '  node tools/McpUsageRecorder/pipeline.mjs --mode resume --definition FILE --series DIR',
    '',
  ].join('\n');
}
