// Conventional Commits parsing and semantic-version helpers (no dependencies).
// Spec: https://www.conventionalcommits.org/en/v1.0.0/

export const TYPES = ['feat', 'fix', 'perf', 'refactor', 'docs', 'test', 'build', 'ci', 'chore', 'style', 'revert'];

const HEADER = /^(?<type>[a-z]+)(?:\((?<scope>[\w\-./ ]+)\))?(?<breaking>!)?: (?<subject>\S.*)$/;

/** Parses a commit message; returns null when the header is not a Conventional Commit. */
export function parseCommit(message) {
  const [header = '', ...rest] = message.replace(/\r\n/g, '\n').split('\n');
  const m = HEADER.exec(header.trim());
  if (!m || !TYPES.includes(m.groups.type)) return null;
  const body = rest.join('\n');
  return {
    type: m.groups.type,
    scope: m.groups.scope ?? null,
    subject: m.groups.subject.trim(),
    breaking: Boolean(m.groups.breaking) || /^BREAKING[ -]CHANGE: /m.test(body),
    header: header.trim(),
  };
}

/** Commits git itself creates and that we never ask authors to rewrite. */
export function isExempt(message) {
  const header = message.split('\n')[0];
  return /^Merge (branch|pull request|remote-tracking branch) /.test(header) || /^Revert ".*"$/.test(header) || /^(fixup|squash|amend)! /.test(header);
}

/** Problems with a commit message, empty when valid. */
export function lintMessage(message) {
  if (isExempt(message)) return [];
  const header = message.split('\n')[0];
  const problems = [];
  const parsed = parseCommit(message);
  if (!parsed) {
    problems.push(`header must look like "type(scope)?: subject" with type one of ${TYPES.join(', ')}`);
  } else if (/[.]$/.test(parsed.subject)) {
    problems.push('subject must not end with a period');
  }
  if (header.length > 100) problems.push(`header is ${header.length} characters (max 100)`);
  return problems;
}

export function parseVersion(text) {
  const m = /^v?(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z.-]+))?$/.exec(text.trim());
  if (!m) return null;
  return { major: +m[1], minor: +m[2], patch: +m[3], prerelease: m[4] ?? null };
}

export function formatVersion(v) {
  return `${v.major}.${v.minor}.${v.patch}${v.prerelease ? `-${v.prerelease}` : ''}`;
}

/** Compares two versions per SemVer precedence. */
export function compareVersions(a, b) {
  for (const k of ['major', 'minor', 'patch']) if (a[k] !== b[k]) return a[k] - b[k];
  if (a.prerelease === b.prerelease) return 0;
  if (!a.prerelease) return 1;
  if (!b.prerelease) return -1;
  const pa = a.prerelease.split('.'), pb = b.prerelease.split('.');
  for (let i = 0; i < Math.max(pa.length, pb.length); i++) {
    if (pa[i] === undefined) return -1;
    if (pb[i] === undefined) return 1;
    const na = /^\d+$/.test(pa[i]), nb = /^\d+$/.test(pb[i]);
    if (na && nb && +pa[i] !== +pb[i]) return +pa[i] - +pb[i];
    if (na !== nb) return na ? -1 : 1;
    if (pa[i] !== pb[i]) return pa[i] < pb[i] ? -1 : 1;
  }
  return 0;
}

const RANK = { none: 0, patch: 1, minor: 2, major: 3 };

/**
 * Release type implied by commits: breaking → major, feat → minor, fix/perf → patch, anything else → none.
 * Before 1.0.0, breaking changes bump the minor version (0.x is "anything may change").
 */
export function bumpFromCommits(messages, current) {
  let bump = 'none';
  for (const message of messages) {
    const c = parseCommit(message);
    if (!c) continue;
    const b = c.breaking ? 'major' : c.type === 'feat' ? 'minor' : c.type === 'fix' || c.type === 'perf' ? 'patch' : 'none';
    if (RANK[b] > RANK[bump]) bump = b;
  }
  if (bump === 'major' && current.major === 0) bump = 'minor';
  return bump;
}

export function increment(v, bump) {
  switch (bump) {
    case 'major': return { major: v.major + 1, minor: 0, patch: 0, prerelease: null };
    case 'minor': return { major: v.major, minor: v.minor + 1, patch: 0, prerelease: null };
    case 'patch': return { major: v.major, minor: v.minor, patch: v.patch + 1, prerelease: null };
    default: return { ...v, prerelease: null };
  }
}

/** Markdown release notes grouped by change type. */
export function releaseNotes(commits, { repoUrl } = {}) {
  const groups = { breaking: [], feat: [], fix: [], perf: [], other: [] };
  for (const { sha, message } of commits) {
    if (isExempt(message)) continue;
    const c = parseCommit(message);
    const short = sha.slice(0, 7);
    const link = repoUrl ? `[${short}](${repoUrl}/commit/${sha})` : short;
    const text = c ? `${c.scope ? `**${c.scope}:** ` : ''}${c.subject}` : message.split('\n')[0];
    const line = `- ${text} (${link})`;
    if (c?.breaking) groups.breaking.push(line);
    if (c && ['feat', 'fix', 'perf'].includes(c.type)) groups[c.type].push(line);
    else if (!c || !['docs', 'test', 'ci', 'chore', 'style', 'build'].includes(c.type)) groups.other.push(line);
  }

  const sections = [
    ['⚠ Breaking changes', groups.breaking],
    ['Features', groups.feat],
    ['Bug fixes', groups.fix],
    ['Performance', groups.perf],
    ['Other changes', groups.other],
  ].filter(([, lines]) => lines.length > 0);

  return sections.length === 0
    ? '_No user-facing changes._\n'
    : sections.map(([title, lines]) => `### ${title}\n\n${lines.join('\n')}\n`).join('\n');
}
