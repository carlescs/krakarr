#!/usr/bin/env node
// Computes the next release version from git tags (vX.Y.Z) and Conventional Commits.
//
// Usage: node scripts/release/next-version.mjs [--bump auto|patch|minor|major] [--version X.Y.Z]
//                                             [--prerelease rc] [--manual] [--notes-file path]
// Writes key=value lines (release, version, tag, previous, bump, prerelease) to $GITHUB_OUTPUT or stdout.
import { execFileSync } from 'node:child_process';
import { appendFileSync, writeFileSync } from 'node:fs';
import { bumpFromCommits, compareVersions, formatVersion, increment, parseVersion, releaseNotes } from './conventional.mjs';

function arg(name, fallback = null) {
  const i = process.argv.indexOf(`--${name}`);
  if (i === -1) return fallback;
  const value = process.argv[i + 1];
  return value === undefined || value.startsWith('--') ? true : value;
}

const git = (...args) => execFileSync('git', args, { encoding: 'utf8' }).trim();

function fail(message) {
  console.error(`::error::${message}`);
  process.exit(1);
}

const requestedBump = String(arg('bump', 'auto') || 'auto');
const explicit = arg('version') && arg('version') !== true ? String(arg('version')).trim() : '';
const preId = arg('prerelease') && arg('prerelease') !== true ? String(arg('prerelease')).trim() : '';
const manual = arg('manual') === true;
const notesFile = arg('notes-file');

if (!['auto', 'patch', 'minor', 'major'].includes(requestedBump)) fail(`Unknown bump "${requestedBump}"`);
if (preId && !/^[0-9A-Za-z-]+$/.test(preId)) fail(`Invalid prerelease identifier "${preId}"`);

const tags = git('tag', '--list', 'v*').split('\n').filter(Boolean)
  .map(t => ({ tag: t, v: parseVersion(t) })).filter(t => t.v)
  .sort((a, b) => compareVersions(b.v, a.v));
const lastStable = tags.find(t => !t.v.prerelease) ?? null;
const lastAny = tags[0] ?? null;
const base = lastStable?.v ?? { major: 0, minor: 0, patch: 0, prerelease: null };

// Commits since the last stable release (or the whole history for the first release).
const range = lastStable ? [`${lastStable.tag}..HEAD`] : ['HEAD'];
const raw = git('log', ...range, '--format=%H%x1f%B%x1e');
const commits = raw.split('\x1e').map(s => s.trim()).filter(Boolean).map(s => {
  const [sha, message = ''] = s.split('\x1f');
  return { sha: sha.trim(), message: message.trim() };
});

let version;
let bump = requestedBump;
if (explicit) {
  version = parseVersion(explicit);
  if (!version) fail(`"${explicit}" is not a valid semantic version`);
  if (lastAny && compareVersions(version, lastAny.v) <= 0) fail(`${explicit} is not greater than the latest version ${formatVersion(lastAny.v)}`);
  if (tags.some(t => compareVersions(t.v, version) === 0)) fail(`v${explicit} already exists`);
  bump = 'explicit';
} else {
  if (bump === 'auto') {
    bump = bumpFromCommits(commits.map(c => c.message), base);
    // A manual run always produces a release: fall back to a patch bump.
    if (bump === 'none' && manual) bump = 'patch';
  }

  if (bump === 'none') {
    version = null;
  } else {
    version = increment(base, bump);
    if (preId) {
      const core = formatVersion(version);
      const existing = tags.filter(t => t.v.prerelease?.startsWith(`${preId}.`) && formatVersion({ ...t.v, prerelease: null }) === core);
      const n = existing.length === 0 ? 1 : Math.max(...existing.map(t => +t.v.prerelease.split('.')[1] || 0)) + 1;
      version = { ...version, prerelease: `${preId}.${n}` };
    }
  }
}

const out = {
  release: version ? 'true' : 'false',
  version: version ? formatVersion(version) : '',
  tag: version ? `v${formatVersion(version)}` : '',
  previous: lastStable?.tag ?? '',
  bump,
  prerelease: version?.prerelease ? 'true' : 'false',
  commits: String(commits.length),
};

if (notesFile && version) {
  const repoUrl = process.env.GITHUB_SERVER_URL && process.env.GITHUB_REPOSITORY
    ? `${process.env.GITHUB_SERVER_URL}/${process.env.GITHUB_REPOSITORY}` : undefined;
  const compare = repoUrl && lastStable ? `\n**Full changelog:** ${repoUrl}/compare/${lastStable.tag}...${out.tag}\n` : '';
  writeFileSync(notesFile, releaseNotes(commits, { repoUrl }) + compare);
}

const lines = Object.entries(out).map(([k, v]) => `${k}=${v}`).join('\n') + '\n';
if (process.env.GITHUB_OUTPUT) appendFileSync(process.env.GITHUB_OUTPUT, lines);
process.stdout.write(lines);
