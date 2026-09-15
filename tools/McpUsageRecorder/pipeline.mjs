#!/usr/bin/env node
import process from 'node:process';
import { mkdir, writeFile } from 'node:fs/promises';
import { dirname, resolve } from 'node:path';
import { loadPipelineDefinition, runPipeline, validateSavedPipeline } from './lib/pipeline-runner.mjs';
import { buildPipelineReport, formatPipelineTable } from './lib/pipeline-report.mjs';
import { preparePipelineJudgments } from './lib/pipeline-judging.mjs';

try {
  const options = parseOptions(process.argv.slice(2));
  if (options.has('help')) {
    process.stdout.write(usage());
  } else {
    const mode = required(options, 'mode');
    if (!['new', 'resume', 'report'].includes(mode))
      throw new Error('--mode must be new, resume, or report.');
    const format = options.get('format') ?? 'json';
    if (!['json', 'markdown'].includes(format))
      throw new Error('--format must be json or markdown.');
    const definitionPath = required(options, 'definition');
    const loaded = await loadPipelineDefinition(definitionPath);
    const result = mode === 'report'
      ? await validateSavedPipeline(required(options, 'series'), loaded.definition, loaded.baseDirectory)
      : await runPipeline({
        mode,
        definitionPath,
        rootDirectory: mode === 'new' ? required(options, 'root') : undefined,
        seriesDirectory: mode === 'resume' ? required(options, 'series') : undefined,
      });
    if (mode !== 'report')
      await preparePipelineJudgments(result.directory, loaded.definition, loaded.baseDirectory);
    const report = await buildPipelineReport(result.directory, loaded.definition, loaded.baseDirectory);
    const json = format === 'markdown' ? formatPipelineTable(report) :
      `${JSON.stringify({ execution: result.results ?? [], report }, null, 2)}\n`;
    if (options.has('output')) {
      const outputPath = resolve(options.get('output'));
      await mkdir(dirname(outputPath), { recursive: true });
      await writeFile(outputPath, json, { encoding: 'utf8' });
    } else {
      process.stdout.write(json);
    }
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
    if (!['--mode', '--definition', '--root', '--series', '--output', '--format'].includes(name))
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
    '  node tools/McpUsageRecorder/pipeline.mjs --mode new --definition FILE --root DIR [--output FILE] [--format json|markdown]',
    '  node tools/McpUsageRecorder/pipeline.mjs --mode resume --definition FILE --series DIR [--output FILE]',
    '  node tools/McpUsageRecorder/pipeline.mjs --mode report --definition FILE --series DIR [--output FILE]',
    '',
  ].join('\n');
}
