'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawnSync } = require('node:child_process');
const {
  PLATFORM_PACKAGES,
  resolveBinary,
  unsupportedMessage,
} = require('../devprojex/bin/devprojex.js');

test('maps every supported Node platform to an exact package binary', () => {
  assert.deepEqual(Object.keys(PLATFORM_PACKAGES), [
    'win32-x64',
    'win32-arm64',
    'linux-x64',
    'linux-arm64',
    'darwin-x64',
    'darwin-arm64',
  ]);

  const resolved = resolveBinary('linux', 'arm64', (request) => {
    assert.equal(request, '@devprojex/cli-linux-arm64/bin/devprojex');
    return path.join('registry', 'devprojex');
  });
  assert.equal(resolved, path.join('registry', 'devprojex'));
});

test('unsupported and omitted-optional errors name platforms and alternatives', () => {
  const unsupported = unsupportedMessage('freebsd', 'x64');
  assert.match(unsupported, /freebsd-x64/);
  assert.match(unsupported, /linux-x64 \(glibc\)/);
  assert.match(unsupported, /musl/);
  assert.match(unsupported, /--omit=optional/);
  assert.match(unsupported, /dnx devprojex/);
  assert.match(unsupported, /GitHub releases/);

  assert.throws(
    () => resolveBinary('win32', 'x64', () => { throw new Error('missing'); }),
    /optional platform package is missing/,
  );
});

test('DEVPROJEX_BINARY overrides package resolution', () => {
  const previous = process.env.DEVPROJEX_BINARY;
  process.env.DEVPROJEX_BINARY = path.join('debug', 'devprojex');
  try {
    assert.equal(resolveBinary('unsupported', 'unsupported', () => assert.fail()), process.env.DEVPROJEX_BINARY);
  } finally {
    if (previous === undefined) delete process.env.DEVPROJEX_BINARY;
    else process.env.DEVPROJEX_BINARY = previous;
  }
});

test('launcher supplies exact npm provenance without changing arguments or inherited environment', () => {
  const args = ['project with spaces Юникод', '$HOME', 'quote\'"`;&|', 'line\nbreak'];
  const result = spawnSync(process.execPath, [
    path.resolve(__dirname, '../devprojex/bin/devprojex.js'),
    '-e', 'process.stdout.write(JSON.stringify({ marker: process.env.DEVPROJEX_NPM_EXECUTABLE, inherited: process.env.DPX_LAUNCHER_TEST, args: process.argv.slice(1) }))',
    '--', ...args,
  ], {
    env: {
      ...process.env,
      DEVPROJEX_BINARY: process.execPath,
      DEVPROJEX_NPM_EXECUTABLE: 'stale-parent-marker',
      DPX_LAUNCHER_TEST: 'preserved',
    },
    encoding: 'utf8',
  });
  assert.equal(result.status, 0, result.stderr);
  assert.deepEqual(JSON.parse(result.stdout), {
    marker: process.execPath,
    inherited: 'preserved',
    args,
  });
});

test('launcher preserves the child exit code', () => {
  const result = spawnSync(process.execPath, [
    path.resolve(__dirname, '../devprojex/bin/devprojex.js'), '-e', 'process.exit(23)',
  ], { env: { ...process.env, DEVPROJEX_BINARY: process.execPath } });
  assert.equal(result.status, 23);
});

test('launcher preserves a native child termination signal', { skip: process.platform === 'win32' }, () => {
  const temporaryRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'devprojex-launcher-signal-'));
  const child = path.join(temporaryRoot, 'terminate.sh');
  fs.writeFileSync(child, '#!/bin/sh\nkill -TERM $$\n', { mode: 0o700 });
  try {
    const result = spawnSync(process.execPath, [
      path.resolve(__dirname, '../devprojex/bin/devprojex.js'),
    ], {
      env: { ...process.env, DEVPROJEX_BINARY: child },
      stdio: 'pipe',
    });
    assert.equal(result.status, null);
    assert.equal(result.signal, 'SIGTERM');
  } finally {
    fs.rmSync(temporaryRoot, { recursive: true, force: true });
  }
});
