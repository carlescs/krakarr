import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { button, mockApi, settle, setupTest, text, type } from '../../../../testing/test-setup';
import { MediaServerSettings } from './media-servers';

const sections: Record<string, unknown> = {
  jellyfin: { url: 'http://jf:8096', apiKey: '********', publicUrl: null, refreshLibrary: true, pathMappings: [] },
  emby: { url: null, apiKey: null, pathMappings: [] },
  plex: { url: 'http://plex:32400', token: '********', pathMappings: [{ from: '/data/tv', to: '/tv' }] },
};

async function render(overrides: Record<string, (...args: never[]) => unknown> = {}) {
  const api = setupTest({
    api: mockApi({
      settings: async (section: string) => structuredClone(sections[section]),
      saveSettings: async (_: string, value: unknown) => value,
      ...overrides,
    }),
  });
  const fixture = TestBed.createComponent(MediaServerSettings);
  await settle(fixture);
  return { api, fixture, el: fixture.nativeElement as HTMLElement };
}

describe('MediaServerSettings', () => {
  it('shows each server with its path mappings', async () => {
    const { el } = await render();
    expect([...el.querySelectorAll('h2')].map(h => h.textContent)).toEqual(['Jellyfin', 'Emby', 'Plex']);
    expect((el.querySelector('#jellyfin-url') as HTMLInputElement).value).toBe('http://jf:8096');
    expect((el.querySelector('#jellyfin-refresh') as HTMLInputElement).checked).toBe(true);
    expect(el.querySelectorAll('[data-mappings="plex"] [data-mapping]').length).toBe(1);
    expect(text(el, '[data-mappings="emby"]')).toContain('Only needed when');
  });

  it('adds a path mapping and saves every server', async () => {
    const { api, el, fixture } = await render();
    type(el, '#emby-url', 'http://emby:8096');
    (el.querySelector('[data-mappings="emby"] button') as HTMLButtonElement).click();
    await settle(fixture);
    const [from, to] = [...el.querySelectorAll<HTMLInputElement>('[data-mappings="emby"] [data-mapping] input')];
    from.value = '/data/movies'; from.dispatchEvent(new Event('input'));
    to.value = 'D:\\Movies'; to.dispatchEvent(new Event('input'));
    await settle(fixture);

    button(el, 'Save').click();
    await settle(fixture);
    expect(api.saveSettings).toHaveBeenCalledWith('emby', { url: 'http://emby:8096', apiKey: null, pathMappings: [{ from: '/data/movies', to: 'D:\\Movies' }] });
    expect(api.saveSettings).toHaveBeenCalledWith('jellyfin', expect.objectContaining({ url: 'http://jf:8096', refreshLibrary: true }));
    expect(api.saveSettings).toHaveBeenCalledWith('plex', expect.objectContaining({ token: '********' }));
  });

  it('removes a path mapping', async () => {
    const { el, fixture } = await render();
    button(el, 'Remove mapping').click();
    await settle(fixture);
    expect(el.querySelectorAll('[data-mappings="plex"] [data-mapping]').length).toBe(0);
  });

  it('tests each server and shows the result', async () => {
    const { api, el, fixture } = await render({
      testPlex: async () => ({ success: true, serverName: 'Den', version: '1.41.2' }),
      testEmby: async () => ({ success: false, message: 'Emby answered HTTP 401' }),
    });
    button(el, 'Test Plex').click();
    await settle(fixture);
    expect(api.testPlex).toHaveBeenCalledWith(expect.objectContaining({ url: 'http://plex:32400' }));
    expect(text(el, '[data-result="plex"]')).toBe('Connected to Den 1.41.2');

    button(el, 'Test Emby').click();
    await settle(fixture);
    expect(text(el, '[data-result="emby"]')).toBe('Emby answered HTTP 401');
  });
});
