#!/usr/bin/env node
'use strict';

// Clients that read the bundle manifest start npx directly from mcp_config. The bundle format still
// requires an entry point, so a client that runs this file gets the same command.
const { spawnSync } = require('node:child_process');
const { constants: osConstants } = require('node:os');

const LAUNCHER_ARGUMENTS = Object.freeze(['-y', 'devprojex', 'mcp']);

function createCommand(serverArguments, platform = process.platform, comSpec = process.env.ComSpec) {
  const launcher = [...LAUNCHER_ARGUMENTS, ...serverArguments];
  // npx is a batch file on Windows, and only cmd can start a batch file.
  return platform === 'win32'
    ? { command: comSpec || 'cmd.exe', args: ['/d', '/c', 'npx', ...launcher] }
    : { command: 'npx', args: launcher };
}

function main() {
  const { command, args } = createCommand(process.argv.slice(2));
  const result = spawnSync(command, args, { stdio: 'inherit', shell: false, windowsHide: true });
  if (result.error) {
    process.stderr.write(`DevProjex could not start npx: ${result.error.message}\n`);
    return 1;
  }
  if (result.signal) {
    try {
      process.kill(process.pid, result.signal);
    } catch {
      const signalNumber = osConstants.signals[result.signal];
      return Number.isInteger(signalNumber) ? 128 + signalNumber : 1;
    }
    return 1;
  }
  return result.status === null ? 1 : result.status;
}

if (require.main === module) {
  process.exitCode = main();
}

module.exports = { LAUNCHER_ARGUMENTS, createCommand };
