import { TestBed } from '@angular/core/testing';
import { describe, expect, it, vi } from 'vitest';
import { ScoredSubtitle } from '../../core/models';
import { button, mockApi, settle, setupTest, text } from '../../../testing/test-setup';
import { SubtitleResults, resultKey } from './subtitle-results';

const r = (payload: string, score: number, extra: Partial<ScoredSubtitle> = {}) => ({
  candidate: { provider: 'subdl', payload, language: 'es', releaseNames: [`Alien.${payload}`], downloads: score },
  score, acceptable: score >= 70, matches: [], rejections: score >= 70 ? [] : ['score below minimum'], ...extra,
}) as unknown as ScoredSubtitle;

describe('SubtitleResults', () => {
  it('lists results with their badges, reports picks and marks downloaded ones', async () => {
    setupTest({ api: mockApi({ subtitleLanguages: async () => [{ code: 'es', name: 'Spanish' }] }) });
    const fixture = TestBed.createComponent(SubtitleResults);
    const low = r('low', 40, { best: true });
    fixture.componentRef.setInput('results', [r('good', 90), low]);
    fixture.componentRef.setInput('downloaded', new Set([resultKey(r('good', 90))]));
    const pick = vi.fn();
    fixture.componentInstance.pick.subscribe(pick);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;

    const rows = [...el.querySelectorAll<HTMLElement>('[data-result]')];
    expect(text(rows[0])).toContain('Downloaded');
    expect(text(rows[1])).toContain('Top pick');
    expect(text(rows[1])).toContain('score below minimum');

    button(el, 'Download Alien.low').click();
    expect(pick).toHaveBeenCalledWith(low);
  });

  it('disables every button while one downloads', async () => {
    setupTest({ api: mockApi({ subtitleLanguages: async () => [] }) });
    const fixture = TestBed.createComponent(SubtitleResults);
    fixture.componentRef.setInput('results', [r('a', 90), r('b', 80)]);
    fixture.componentRef.setInput('downloading', 'subdl:a');
    fixture.componentRef.setInput('action', 'Use');
    await settle(fixture);
    const buttons = [...(fixture.nativeElement as HTMLElement).querySelectorAll<HTMLButtonElement>('[data-result] button')];
    expect(buttons.map(b => b.disabled)).toEqual([true, true]);
    expect(buttons.map(b => b.textContent!.trim())).toEqual(['…', 'Use']);
  });
});
