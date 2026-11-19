import { test } from 'node:test';
import assert from 'node:assert/strict';
import { bumpFromCommits, compareVersions, formatVersion, increment, lintMessage, parseCommit, parseVersion, releaseNotes } from './conventional.mjs';

test('parses conventional headers', () => {
  assert.deepEqual(parseCommit('feat(series): add calendar'), { type: 'feat', scope: 'series', subject: 'add calendar', breaking: false, header: 'feat(series): add calendar' });
  assert.equal(parseCommit('fix!: drop old api').breaking, true);
  assert.equal(parseCommit('refactor: x\n\nBREAKING CHANGE: settings renamed').breaking, true);
  assert.equal(parseCommit('Add movie module'), null);
  assert.equal(parseCommit('feature: nope'), null);
  assert.equal(parseCommit('feat:missing space'), null);
});

test('lints messages', () => {
  assert.deepEqual(lintMessage('feat: add thing'), []);
  assert.deepEqual(lintMessage('Merge branch "x" into main'), []);
  assert.equal(lintMessage('Add thing').length, 1);
  assert.equal(lintMessage('fix: add thing.').length, 1);
  assert.equal(lintMessage('fix: ' + 'x'.repeat(120)).length, 1);
});

test('derives the bump from commits', () => {
  const v1 = parseVersion('1.2.3');
  assert.equal(bumpFromCommits(['docs: readme', 'chore: deps'], v1), 'none');
  assert.equal(bumpFromCommits(['fix: a', 'docs: b'], v1), 'patch');
  assert.equal(bumpFromCommits(['perf: faster'], v1), 'patch');
  assert.equal(bumpFromCommits(['fix: a', 'feat: b'], v1), 'minor');
  assert.equal(bumpFromCommits(['feat!: b'], v1), 'major');
  assert.equal(bumpFromCommits(['feat!: b'], parseVersion('0.3.0')), 'minor'); // pre-1.0
  assert.equal(bumpFromCommits(['Add something'], v1), 'none');
});

test('increments and orders versions', () => {
  assert.equal(formatVersion(increment(parseVersion('1.2.3'), 'patch')), '1.2.4');
  assert.equal(formatVersion(increment(parseVersion('1.2.3'), 'minor')), '1.3.0');
  assert.equal(formatVersion(increment(parseVersion('1.2.3'), 'major')), '2.0.0');
  const sorted = ['1.0.0', '1.0.0-rc.2', '1.0.0-rc.10', '0.9.1', '1.0.0-beta.1'].map(parseVersion).sort(compareVersions).map(formatVersion);
  assert.deepEqual(sorted, ['0.9.1', '1.0.0-beta.1', '1.0.0-rc.2', '1.0.0-rc.10', '1.0.0']);
  assert.equal(parseVersion('v1.2'), null);
});

test('groups release notes', () => {
  const notes = releaseNotes([
    { sha: 'a'.repeat(40), message: 'feat(movies): import libraries' },
    { sha: 'b'.repeat(40), message: 'fix: crash on empty folder' },
    { sha: 'c'.repeat(40), message: 'docs: readme' },
    { sha: 'd'.repeat(40), message: 'Old style commit' },
    { sha: 'e'.repeat(40), message: 'feat!: rename settings' },
  ], { repoUrl: 'https://github.com/o/r' });
  assert.match(notes, /### ⚠ Breaking changes\n\n- rename settings/);
  assert.match(notes, /### Features\n\n- \*\*movies:\*\* import libraries \(\[aaaaaaa\]\(https:\/\/github.com\/o\/r\/commit\/a+\)\)\n- rename settings/);
  assert.match(notes, /### Bug fixes\n\n- crash on empty folder/);
  assert.match(notes, /### Other changes\n\n- Old style commit/);
  assert.doesNotMatch(notes, /readme/);
});
