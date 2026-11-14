import { spawnSync } from 'node:child_process';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

export function sessionProvenance(folder, root, manifest, server) {
  const python = process.env.AGENT_BENCHMARK_PYTHON || (process.platform === 'win32' ? 'python' : 'python3');
  const script = join(dirname(fileURLToPath(import.meta.url)), 'provenance.py');
  const result = spawnSync(python, [script, '--session', folder, '--root', root, '--manifest', manifest,
    ...(server ? ['--server', server] : [])],
    { encoding: 'utf8', windowsHide: true, timeout: 30000 });
  try {
    if (result.status === 0) return JSON.parse(result.stdout);
  } catch { /* Fail closed when the checker cannot establish the source. */ }
  return { status: 'unverified', violations: [{ reason: 'provenance-check-failed' }] };
}
