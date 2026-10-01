import { readFileSync, writeFileSync } from 'node:fs';
if (process.argv[3] === '--fail-once') {
  try {
    writeFileSync(process.argv[4], 'attempted', { flag: 'wx' });
    process.exit(2);
  } catch (error) { if (error.code !== 'EEXIST') throw error; }
}
const input = JSON.parse(readFileSync(process.argv[2], 'utf8'));
if (JSON.stringify(input.candidates.map(candidate => candidate.id)) !== '["A","B"]')
  throw new Error('Judgment inputs must be anonymous.');
const responses = JSON.parse(readFileSync(new URL('./judge-responses.json', import.meta.url), 'utf8'));
process.stdout.write(JSON.stringify(responses[input.presentation]) + '\n');
