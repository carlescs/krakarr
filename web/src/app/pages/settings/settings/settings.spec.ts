import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, Router, convertToParamMap, provideRouter } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { of } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import { Events } from '../../../core/events';
import { ScheduledTaskInfo } from '../../../core/models';
import { button, mockApi, settle, setupTest, text } from '../../../../testing/test-setup';
import { settingsRouteMatcher } from '../../../app.routes';
import { SettingsPage } from './settings';

/** The page behind the real settings route, so its tab links navigate. Call `setupTest` with `routed` first. */
async function render(url = '/settings') {
  const harness = await RouterTestingHarness.create();
  const page = await harness.navigateByUrl(url, SettingsPage);
  await settle(harness.fixture);
  return { harness, page, fixture: harness.fixture, el: harness.fixture.nativeElement as HTMLElement };
}

const routed = [provideRouter([{ matcher: settingsRouteMatcher, component: SettingsPage }])];

/** The tab link labelled `label`. */
function tabLink(root: HTMLElement, label: string): HTMLAnchorElement {
  const found = [...root.querySelectorAll<HTMLAnchorElement>('h1 + div a')].find(a => a.textContent?.trim() === label);
  if (!found) throw new Error(`No tab "${label}"`);
  return found;
}

const task = (t: Partial<ScheduledTaskInfo>): ScheduledTaskInfo => ({
  id: 'rss', name: 'RSS sync', intervalSeconds: 900, lastRun: null, lastDurationSeconds: null, lastSucceeded: null, lastError: null, nextRun: null, isRunning: false, ...t,
});

describe('SettingsPage', () => {
  it('only shows plain users their account', async () => {
    const api = setupTest({ role: 'User' });
    const fixture = TestBed.createComponent(SettingsPage);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    expect(text(el, 'h1')).toBe('Account');
    expect(el.querySelector('kr-account-settings')).not.toBeNull();
    expect(api.settings).not.toHaveBeenCalled();
  });

  it('shows admins every tab and loads the settings', async () => {
    const api = setupTest({ providers: routed, api: mockApi({ rootFolders: async () => [{ id: 1, path: '/data/movies', mediaType: 'Movie', accessible: false }] }) });
    const { fixture, el, page, harness } = await render();
    expect(text(el, 'h1')).toBe('Settings');
    expect([...el.querySelectorAll('h1 + div a')].map(b => b.textContent)).toEqual([
      'Media management', 'Quality profiles', 'Custom formats', 'Download client', 'Subtitles', 'Metadata', 'Media servers', 'Telemetry', 'Health', 'Tasks', 'Backups', 'Database', 'Users', 'General', 'Account',
    ]);
    expect(api.settings).toHaveBeenCalledWith('media');
    expect(api.settings).toHaveBeenCalledWith('general');

    tabLink(el, 'Users').click();
    await settle(fixture);
    expect(el.querySelector('kr-users-settings')).not.toBeNull();
    expect(TestBed.inject(Router).url).toBe('/settings/users');
    // Same page instance, so the settings aren't fetched again and unsaved edits on other tabs survive.
    expect(harness.routeDebugElement?.componentInstance).toBe(page);
    expect(api.settings.mock.calls.filter(([section]) => section === 'media')).toHaveLength(1);
  });

  it('opens the tab named in the URL', async () => {
    setupTest({
      api: mockApi({ health: async () => ({ checkedAt: null, issues: [] }) }),
      providers: [{ provide: ActivatedRoute, useValue: { paramMap: of(convertToParamMap({ tab: 'health' })) } }],
    });
    const fixture = TestBed.createComponent(SettingsPage);
    await settle(fixture);
    expect((fixture.nativeElement as HTMLElement).querySelector('kr-health-settings')).not.toBeNull();
  });

  it('ignores tabs a plain user may not open and drops them from the URL', async () => {
    setupTest({ role: 'User', providers: [{ provide: ActivatedRoute, useValue: { paramMap: of(convertToParamMap({ tab: 'health' })) } }] });
    const navigate = vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
    const fixture = TestBed.createComponent(SettingsPage);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('kr-health-settings')).toBeNull();
    expect(el.querySelector('kr-account-settings')).not.toBeNull();
    expect(navigate).toHaveBeenCalledWith(['/settings'], { replaceUrl: true });
  });

  it('links every tab to its own URL', async () => {
    setupTest({ api: mockApi({ health: async () => ({ checkedAt: null, issues: [] }) }) });
    const fixture = TestBed.createComponent(SettingsPage);
    await settle(fixture);
    const links = [...(fixture.nativeElement as HTMLElement).querySelectorAll<HTMLAnchorElement>('a[href^="/settings/"]')];
    expect(links.map(a => a.getAttribute('href'))).toContain('/settings/health');
    expect(links.find(a => a.getAttribute('aria-current') === 'page')?.getAttribute('href')).toBe('/settings/media');
  });

  it('lists scheduled tasks, runs one and follows live updates', async () => {
    const api = setupTest({
      providers: routed,
      api: mockApi({
        tasks: async () => [task({ lastRun: new Date(Date.now() - 3 * 60_000).toISOString(), lastSucceeded: false, lastDurationSeconds: 2.5 })],
        runTask: async () => undefined,
      }),
    });
    const { fixture, el } = await render();
    tabLink(el, 'Tasks').click();
    await settle(fixture);
    expect(text(el, 'tbody')).toContain('RSS sync');
    expect(text(el, 'tbody')).toContain('Every 15m');
    expect(text(el, 'tbody')).toContain('3m ago (failed)');
    expect(text(el, 'tbody')).toContain('2.5s');

    button(el, 'Run now').click();
    await settle(fixture);
    expect(api.runTask).toHaveBeenCalledWith('rss');

    TestBed.inject(Events).taskUpdated.next(task({ isRunning: true }));
    await settle(fixture);
    expect(text(el, 'tbody')).toContain('Running…');
  });

  it('saves the telemetry settings and shows the export status', async () => {
    const settings = {
      enabled: false, endpoint: 'http://otel:4317', protocol: 'Grpc', headers: null, serviceName: 'krakarr', resourceAttributes: null,
      exportTraces: true, exportMetrics: true, exportLogs: false, traceSampleRatio: 1, metricExportIntervalSeconds: 60, libraryStatsIntervalMinutes: 5,
    };
    let status = { active: false, signals: [] as string[], endpoint: null as string | null, protocol: null as string | null, error: null };
    const api = setupTest({
      providers: routed,
      api: mockApi({
        settings: async (section: string) => (section === 'telemetry' ? { ...settings } : section === 'languages' ? { preferred: [] } : {}),
        rootFolders: async () => [], qualityProfiles: async () => [], qualities: async () => [], systemStatus: async () => ({}),
        telemetryStatus: async () => status,
        saveSettings: async (_: string, value: unknown) => value,
      }),
    });
    const { page, fixture, el } = await render();
    page.telemetryStatusDelayMs = 0;
    tabLink(el, 'Telemetry').click();
    await settle(fixture);
    expect(text(el)).toContain('Telemetry is off');

    const protocol = el.querySelector('select') as HTMLSelectElement;
    protocol.value = 'HttpProtobuf';
    protocol.dispatchEvent(new Event('change'));
    const enabled = [...el.querySelectorAll('label')].find(l => l.textContent?.includes('Export telemetry'))!.querySelector('input')!;
    enabled.click();
    await settle(fixture);

    status = { active: true, signals: ['traces', 'metrics'], endpoint: 'http://otel:4318', protocol: 'HttpProtobuf', error: null };
    button(el, 'Save').click();
    await settle(fixture);
    await settle(fixture);
    expect(api.saveSettings).toHaveBeenCalledWith('telemetry', expect.objectContaining({ enabled: true, protocol: 'HttpProtobuf', endpoint: 'http://otel:4318' }));
    expect(text(el)).toContain('Exporting traces, metrics to http://otel:4318 (HTTP)');
  });

  it('saves the whole subtitles tab from the footer', async () => {
    const subtitles = {
      enabled: false, languages: ['en'], hearingImpaired: 'PreferNormal', minimumScoreMovies: 70, minimumScoreEpisodes: 80, searchIntervalHours: 24,
      includeAiTranslated: false, useEmbeddedSubtitles: true, upgradeDays: 7, upgradeBelowScore: 90,
      sync: { automatic: false, belowScore: 90, timeoutSeconds: 300 }, openSubtitles: { enabled: true }, subdl: { enabled: true },
    };
    const api = setupTest({
      providers: routed,
      api: mockApi({
        settings: async (section: string) => (section === 'subtitles' ? { ...subtitles } : section === 'languages' ? { preferred: [] } : {}),
        subtitleLanguages: async () => [{ code: 'en', name: 'English' }],
        rootFolders: async () => [], qualityProfiles: async () => [], qualities: async () => [], systemStatus: async () => ({}),
        saveSettings: async (_: string, value: unknown) => value,
        subtitleSyncTools: async () => ({ alass: true, ffsubsync: false, ffmpeg: true }),
      }),
    });
    const { fixture, el } = await render();
    tabLink(el, 'Subtitles').click();
    await settle(fixture);
    expect(text(el, '[data-sync-tools]')).toContain('alass · found');
    expect(text(el, '[data-sync-tools]')).toContain('ffsubsync · not found');
    [...el.querySelectorAll('[data-sync-settings] label')].find(l => l.textContent?.includes('Sync new subtitles automatically'))!.querySelector('input')!.click();

    const options = el.querySelector('section.card') as HTMLElement;
    [...options.querySelectorAll('label')].find(l => l.textContent?.includes('Download subtitles automatically'))!.querySelector('input')!.click();
    await settle(fixture);
    expect([...el.querySelectorAll('button')].filter(b => b.textContent?.trim() === 'Save')).toHaveLength(1);
    button(el, 'Save').click();
    await settle(fixture);
    expect(api.saveSettings).toHaveBeenCalledTimes(1);
    expect(api.saveSettings).toHaveBeenCalledWith('subtitles', expect.objectContaining({ enabled: true, sync: expect.objectContaining({ automatic: true }) }));
  });

  it('saves the download client and automation settings together', async () => {
    const api = setupTest({
      providers: routed,
      api: mockApi({
        settings: async (section: string) => (section === 'downloads' ? { client: 'Embedded', qBittorrent: {} } : section === 'automation' ? { rssSyncIntervalMinutes: 15 } : section === 'languages' ? { preferred: [] } : {}),
        rootFolders: async () => [], qualityProfiles: async () => [], qualities: async () => [], systemStatus: async () => ({}),
        saveSettings: async (_: string, value: unknown) => value,
      }),
    });
    const { fixture, el } = await render();
    tabLink(el, 'Download client').click();
    await settle(fixture);
    button(el, 'Save').click();
    await settle(fixture);
    expect(api.saveSettings).toHaveBeenCalledWith('downloads', expect.objectContaining({ client: 'Embedded' }));
    expect(api.saveSettings).toHaveBeenCalledWith('automation', { rssSyncIntervalMinutes: 15 });
  });
  it('scores custom formats in the quality profile editor', async () => {
    const profile = {
      id: 1, name: 'HD', allowedQualityIds: [16, 17], cutoffQualityId: 17, upgradeAllowed: true, minSizeMb: 0, maxSizeMb: 0,
      formatScores: [{ formatId: 1, score: 100 }], minFormatScore: 0, cutoffFormatScore: 0,
    };
    const api = setupTest({
      providers: routed,
      api: mockApi({
        settings: async (section: string) => (section === 'languages' ? { preferred: [] } : {}),
        rootFolders: async () => [], qualityProfiles: async () => [profile], systemStatus: async () => ({}),
        qualities: async () => [{ id: 16, name: 'WEBDL-1080p', source: 'WebDl', resolution: 1080, weight: 16 }, { id: 17, name: 'Bluray-1080p', source: 'Bluray', resolution: 1080, weight: 17 }],
        customFormats: async () => [
          { id: 1, name: 'Atmos', specifications: [] },
          { id: 2, name: 'x265', specifications: [] },
        ],
        saveQualityProfile: async (p: unknown) => p,
      }),
    });
    const { fixture, el } = await render('/settings/profiles');
    button(el, 'HD').click();
    await settle(fixture);

    const score = (name: string) => el.querySelector<HTMLInputElement>(`[data-format-scores] input[aria-label="${name} score"]`)!;
    expect(score('Atmos').value).toBe('100');
    score('Atmos').value = '0';
    score('Atmos').dispatchEvent(new Event('input'));
    score('x265').value = '-50';
    score('x265').dispatchEvent(new Event('input'));
    const cutoff = el.querySelector<HTMLInputElement>('#profile-cutoff-score')!;
    cutoff.value = '200';
    cutoff.dispatchEvent(new Event('input'));
    await settle(fixture);

    button(el.querySelector('[role="dialog"]') as HTMLElement, 'Save').click();
    await settle(fixture);
    expect(api.saveQualityProfile).toHaveBeenCalledWith(expect.objectContaining({ formatScores: [{ formatId: 2, score: -50 }], cutoffFormatScore: 200 }));
  });
});
