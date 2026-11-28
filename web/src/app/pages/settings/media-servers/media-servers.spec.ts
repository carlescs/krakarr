import { TestBed } from '@angular/core/testing';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { button, mockApi, settle, setupTest, text, type } from '../../../../testing/test-setup';
import { MediaServerSettings } from './media-servers';

const sections: Record<string, unknown> = {
  jellyfin: { url: 'http://jf:8096', apiKey: '********', publicUrl: null, refreshLibrary: true, pathMappings: [], playStatusUserId: 'u1' },
  emby: { url: null, apiKey: null, pathMappings: [] },
  plex: { url: 'http://plex:32400', token: '********', pathMappings: [{ from: '/data/tv', to: '/tv' }] },
};

async function render(overrides: Record<string, (...args: never[]) => unknown> = {}) {
  const api = setupTest({
    api: mockApi({
      settings: async (section: string) => structuredClone(sections[section]),
      saveSettings: async (_: string, value: unknown) => value,
      jellyfinUsers: async () => [{ id: 'u1', name: 'Anna' }, { id: 'u2', name: 'Zoe' }],
      ...overrides,
    }),
  });
  const fixture = TestBed.createComponent(MediaServerSettings);
  await settle(fixture);
  return { api, fixture, el: fixture.nativeElement as HTMLElement };
}

const headings = (el: HTMLElement) => [...el.querySelectorAll('section h2')].map(h => h.textContent);
const options = (el: HTMLElement, selector: string) => [...el.querySelectorAll<HTMLOptionElement>(`${selector} option`)].map(o => o.textContent?.trim());

describe('MediaServerSettings', () => {
  afterEach(() => vi.restoreAllMocks());

  it('shows only the servers in use, with their path mappings', async () => {
    const { el } = await render();
    expect(headings(el)).toEqual(['Jellyfin', 'Plex']);
    expect(el.querySelector('[data-server="emby"]')).toBeNull();
    expect((el.querySelector('#jellyfin-url') as HTMLInputElement).value).toBe('http://jf:8096');
    expect((el.querySelector('#jellyfin-refresh') as HTMLInputElement).checked).toBe(true);
    expect(el.querySelectorAll('[data-mappings="plex"] [data-mapping]').length).toBe(1);
    expect(options(el, '#add-server')).toEqual(['Add a media server…', 'Emby']);
  });

  it('lists the Jellyfin users to pick whose play status to show', async () => {
    const { api, el } = await render();
    expect(api.jellyfinUsers).toHaveBeenCalledWith(expect.objectContaining({ url: 'http://jf:8096' }));
    expect(options(el, '#jellyfin-play-user')).toEqual(["Nobody (don't show play status)", 'Anna', 'Zoe']);
    expect((el.querySelector('#jellyfin-play-user') as HTMLSelectElement).selectedOptions[0].textContent?.trim()).toBe('Anna');
  });

  it('adds a server from the picker, with a path mapping, and saves the servers in use', async () => {
    const { api, el, fixture } = await render();
    type(el, '#add-server', 'emby');
    await settle(fixture);
    expect(headings(el)).toEqual(['Jellyfin', 'Emby', 'Plex']);
    expect(el.querySelector('#add-server')).toBeNull();

    type(el, '#emby-url', 'http://emby:8096');
    (el.querySelector('[data-mappings="emby"] button') as HTMLButtonElement).click();
    await settle(fixture);
    const [from, to] = [...el.querySelectorAll<HTMLInputElement>('[data-mappings="emby"] [data-mapping] input')];
    from.value = '/data/movies'; from.dispatchEvent(new Event('input'));
    to.value = 'D:\\Movies'; to.dispatchEvent(new Event('input'));
    (el.querySelector('#plex-play-status') as HTMLInputElement).click();
    await settle(fixture);

    button(el, 'Save').click();
    await settle(fixture);
    expect(api.saveSettings).toHaveBeenCalledWith('emby', { url: 'http://emby:8096', apiKey: null, pathMappings: [{ from: '/data/movies', to: 'D:\\Movies' }] });
    expect(api.saveSettings).toHaveBeenCalledWith('jellyfin', expect.objectContaining({ url: 'http://jf:8096', refreshLibrary: true, playStatusUserId: 'u1' }));
    expect(api.saveSettings).toHaveBeenCalledWith('plex', expect.objectContaining({ token: '********', playStatus: true }));
  });

  it('does not save servers that are not in use', async () => {
    const { api, el, fixture } = await render();
    button(el, 'Save').click();
    await settle(fixture);
    expect(api.saveSettings).toHaveBeenCalledTimes(2);
    expect(api.saveSettings).not.toHaveBeenCalledWith('emby', expect.anything());
  });

  it('removes a server after confirmation, clearing its settings', async () => {
    const { api, el, fixture } = await render();
    const confirm = vi.spyOn(window, 'confirm').mockReturnValueOnce(false);
    button(el, 'Remove Plex').click();
    await settle(fixture);
    expect(confirm).toHaveBeenCalledWith(expect.stringContaining('Remove Plex?'));
    expect(headings(el)).toEqual(['Jellyfin', 'Plex']);
    expect(api.saveSettings).not.toHaveBeenCalled();

    confirm.mockReturnValueOnce(true);
    button(el, 'Remove Plex').click();
    await settle(fixture);
    expect(api.saveSettings).toHaveBeenCalledWith('plex', { url: null, token: null, pathMappings: [], playStatus: false });
    expect(headings(el)).toEqual(['Jellyfin']);
    expect(options(el, '#add-server')).toEqual(['Add a media server…', 'Emby', 'Plex']);
  });

  it('removes a server that was only just added without saving anything', async () => {
    const { api, el, fixture } = await render();
    vi.spyOn(window, 'confirm').mockReturnValue(true);
    type(el, '#add-server', 'emby');
    await settle(fixture);
    button(el, 'Remove Emby').click();
    await settle(fixture);
    expect(api.saveSettings).not.toHaveBeenCalled();
    expect(headings(el)).toEqual(['Jellyfin', 'Plex']);
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

    type(el, '#add-server', 'emby');
    await settle(fixture);
    button(el, 'Test Emby').click();
    await settle(fixture);
    expect(text(el, '[data-result="emby"]')).toBe('Emby answered HTTP 401');
  });
});
