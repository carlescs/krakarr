import { describe, expect, it } from 'vitest';
import { buildName } from './settings';

describe('buildName', () => {
  it('expands tokens, drops empty brackets and sanitizes like the server', () => {
    const values = { 'Movie Title': 'Alien: Romulus', 'Release Year': '2024', 'Release Group': '' };
    expect(buildName('{Movie Title} ({Release Year}) [{Release Group}]', values)).toBe('Alien- Romulus (2024)');
    expect(buildName('{Unknown} {Movie Title}?', values)).toBe('{Unknown} Alien- Romulus');
  });
});
