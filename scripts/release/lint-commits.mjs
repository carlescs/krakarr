#!/usr/bin/env node
// Checks commit messages against Conventional Commits.
//   node scripts/release/lint-commits.mjs --range <from>..<to>   (CI)
//   node scripts/release/lint-commits.mjs --message-file <path>  (git commit-msg hook)
import { execFileSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { lintMessage } from './conventional.mjs';

const i = name => process.argv.indexOf(`--${name}`);
const messages = [];

if (i('message-file') !== -1) {
  const text = readFileSync(process.argv[i('message-file') + 1], 'utf8')
    .split('\n').filter(l => !l.startsWith('#')).join('\n').trim();
  messages.push({ sha: 'new commit', message: text });
} else if (i('range') !== -1) {
  const range = process.argv[i('range') + 1];
  const raw = execFileSync('git', ['log', range, '--no-merges', '--format=%H%x1f%B%x1e'], { encoding: 'utf8' });
  for (const entry of raw.split('\x1e').map(s => s.trim()).filter(Boolean)) {
    const [sha, message = ''] = entry.split('\x1f');
    messages.push({ sha: sha.slice(0, 7), message: message.trim() });
  }
} else {
  console.error('Usage: lint-commits.mjs --range <from>..<to> | --message-file <path>');
  process.exit(2);
}

let failed = 0;
for (const { sha, message } of messages) {
  const problems = lintMessage(message);
  if (problems.length === 0) continue;
  failed++;
  console.error(`✖ ${sha}: "${message.split('\n')[0]}"`);
  for (const p of problems) console.error(`    ${p}`);
}

if (failed > 0) {
  console.error(`\n${failed} commit message(s) do not follow Conventional Commits (https://www.conventionalcommits.org).`);
  console.error('Examples: "feat(series): add calendar view", "fix: handle empty torrent list", "feat!: drop PostgreSQL 12".');
  process.exit(1);
}
console.log(`✔ ${messages.length} commit message(s) follow Conventional Commits`);
