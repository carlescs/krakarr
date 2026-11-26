import { describe, expect, it } from 'vitest';
import { airsOn, formatBytes, formatDuration } from './format';

describe('format', () => {
  it('formats bytes', () => {
    expect(formatBytes(0)).toBe('0 B');
    expect(formatBytes(1023)).toBe('1023 B');
    expect(formatBytes(1536)).toBe('1.5 KB');
    expect(formatBytes(8 * 1024 ** 3)).toBe('8.0 GB');
  });

  it('says where a show airs or aired', () => {
    expect(airsOn('Netflix', 'Continuing')).toBe('Airs on Netflix');
    expect(airsOn('Apple TV+', 'Upcoming')).toBe('Airs on Apple TV+');
    expect(airsOn('AMC', 'Ended')).toBe('Aired on AMC');
    expect(airsOn(null, 'Ended')).toBe('');
  });

  it('formats durations', () => {
    expect(formatDuration(undefined)).toBe('∞');
    expect(formatDuration(45)).toBe('45s');
    expect(formatDuration(125)).toBe('2m 5s');
    expect(formatDuration(3 * 3600 + 120)).toBe('3h 2m');
    expect(formatDuration(2 * 86400 + 3600)).toBe('2d 1h');
  });
});
