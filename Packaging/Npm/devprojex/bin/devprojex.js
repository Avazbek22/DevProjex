#!/usr/bin/env node
'use strict';

const { spawnSync } = require('node:child_process');

const PLATFORM_PACKAGES = Object.freeze({
  'win32-x64': Object.freeze({ packageName: '@devprojex/cli-win32-x64', binary: 'devprojex.exe' }),
  'win32-arm64': Object.freeze({ packageName: '@devprojex/cli-win32-arm64', binary: 'devprojex.exe' }),
  'linux-x64': Object.freeze({ packageName: '@devprojex/cli-linux-x64', binary: 'devprojex' }),
  'linux-arm64': Object.freeze({ packageName: '@devprojex/cli-linux-arm64', binary: 'devprojex' }),
  'darwin-x64': Object.freeze({ packageName: '@devprojex/cli-darwin-x64', binary: 'devprojex' }),
  'darwin-arm64': Object.freeze({ packageName: '@devprojex/cli-darwin-arm64', binary: 'devprojex' }),
});

function unsupportedMessage(platform = process.platform, arch = process.arch) {
  return [
    `DevProjex has no headless npm binary for ${platform}-${arch}, or its optional platform package is missing.`,
    'Supported platforms: win32-x64, win32-arm64, linux-x64 (glibc), linux-arm64 (glibc), darwin-x64, darwin-arm64.',
    'Alpine Linux and other musl systems are not supported in this release.',
    'Do not install with --omit=optional. Alternatives: dnx devprojex, or a binary from the GitHub releases page.',
  ].join('\n');
}

function resolveBinary(platform = process.platform, arch = process.arch, resolver = require.resolve) {
  if (process.env.DEVPROJEX_BINARY) {
    return process.env.DEVPROJEX_BINARY;
  }

  const target = PLATFORM_PACKAGES[`${platform}-${arch}`];
  if (!target) {
    throw new Error(unsupportedMessage(platform, arch));
  }

  try {
    return resolver(`${target.packageName}/bin/${target.binary}`);
  } catch (error) {
    const failure = new Error(unsupportedMessage(platform, arch));
    failure.cause = error;
    throw failure;
  }
}

function main() {
  let binary;
  try {
    binary = resolveBinary();
  } catch (error) {
    process.stderr.write(`${error.message}\n`);
    return 1;
  }

  const result = spawnSync(binary, process.argv.slice(2), {
    stdio: 'inherit',
    shell: false,
    windowsHide: false,
  });
  if (result.error) {
    process.stderr.write(`DevProjex could not start ${binary}: ${result.error.message}\n`);
    return 1;
  }
  return result.status === null ? 1 : result.status;
}

if (require.main === module) {
  process.exitCode = main();
}

module.exports = { PLATFORM_PACKAGES, resolveBinary, unsupportedMessage };
