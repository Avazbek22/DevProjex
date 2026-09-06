'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const path = require('node:path');
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
