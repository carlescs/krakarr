import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { MediaInfo } from '../../core/models';
import { settle, setupTest, text } from '../../../testing/test-setup';
import { MediaInfoView } from './media-info';

const info: MediaInfo = {
  durationSeconds: 7380, videoCodec: 'HEVC', width: 3840, height: 1600, resolution: '2160p', bitDepth: 10, dynamicRange: 'DV HDR10',
  audio: [
    { codec: 'TrueHD Atmos', channels: '7.1', language: 'en', languageName: 'English' },
    { codec: 'AC3', channels: '5.1', language: null, languageName: null },
  ],
  warnings: ['Audio is English; wanted: Spanish'],
};

describe('MediaInfoView', () => {
  it('shows video, audio tracks, duration and warnings', async () => {
    setupTest({});
    const fixture = TestBed.createComponent(MediaInfoView);
    fixture.componentRef.setInput('info', info);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;

    expect(text(el.querySelector('[data-video]') as HTMLElement)).toContain('HEVC · 3840×1600 · 2160p · 10-bit');
    expect(text(el.querySelector('[data-hdr]') as HTMLElement)).toBe('DV HDR10');
    expect([...el.querySelectorAll('[data-audio]')].map(a => text(a as HTMLElement)))
      .toEqual(['TrueHD Atmos 7.1 · English', 'AC3 5.1 · language not tagged']);
    expect(text(el.querySelector('[data-duration]') as HTMLElement)).toBe('2 h 3 min');
    expect(text(el.querySelector('[data-warning]') as HTMLElement)).toContain('wanted: Spanish');
  });

  it('renders nothing without media info', async () => {
    setupTest({});
    const fixture = TestBed.createComponent(MediaInfoView);
    await settle(fixture);
    expect((fixture.nativeElement as HTMLElement).querySelector('[data-media-info]')).toBeNull();
  });
});
