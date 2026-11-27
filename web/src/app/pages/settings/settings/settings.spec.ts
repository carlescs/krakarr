import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap } from '@angular/router';
import { of } from 'rxjs';
import { describe, expect, it } from 'vitest';
import { Events } from '../../../core/events';
import { ScheduledTaskInfo } from '../../../core/models';
import { button, mockApi, settle, setupTest, text } from '../../../../testing/test-setup';
import { SettingsPage } from './settings';

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
    const api = setupTest({ api: mockApi({ rootFolders: async () => [{ id: 1, path: '/data/movies', mediaType: 'Movie', accessible: false }] }) });
    const fixture = TestBed.createComponent(SettingsPage);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    expect(text(el, 'h1')).toBe('Settings');
    expect([...el.querySelectorAll('h1 + div button')].map(b => b.textContent)).toEqual([
      'Media management', 'Quality profiles', 'Download client', 'Subtitles', 'Metadata', 'Media servers', 'Telemetry', 'Health', 'Tasks', 'Backups', 'Database', 'Users', 'General', 'Account',
    ]);
    expect(api.settings).toHaveBeenCalledWith('media');
    expect(api.settings).toHaveBeenCalledWith('general');

    button(el, 'Users').click();
    await settle(fixture);
    expect(el.querySelector('kr-users-settings')).not.toBeNull();
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

  it('ignores tabs a plain user may not open', async () => {
    setupTest({ role: 'User', providers: [{ provide: ActivatedRoute, useValue: { paramMap: of(convertToParamMap({ tab: 'health' })) } }] });
    const fixture = TestBed.createComponent(SettingsPage);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    expect(el.querySelector('kr-health-settings')).toBeNull();
    expect(el.querySelector('kr-account-settings')).not.toBeNull();
  });

  it('lists scheduled tasks, runs one and follows live updates', async () => {
    const api = setupTest({
      api: mockApi({
        tasks: async () => [task({ lastRun: new Date(Date.now() - 3 * 60_000).toISOString(), lastSucceeded: false, lastDurationSeconds: 2.5 })],
        runTask: async () => undefined,
      }),
    });
    const fixture = TestBed.createComponent(SettingsPage);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    button(el, 'Tasks').click();
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
      api: mockApi({
        settings: async (section: string) => (section === 'telemetry' ? { ...settings } : section === 'languages' ? { preferred: [] } : {}),
        rootFolders: async () => [], qualityProfiles: async () => [], qualities: async () => [], systemStatus: async () => ({}),
        telemetryStatus: async () => status,
        saveSettings: async (_: string, value: unknown) => value,
      }),
    });
    const fixture = TestBed.createComponent(SettingsPage);
    fixture.componentInstance.telemetryStatusDelayMs = 0;
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    button(el, 'Telemetry').click();
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
      api: mockApi({
        settings: async (section: string) => (section === 'subtitles' ? { ...subtitles } : section === 'languages' ? { preferred: [] } : {}),
        subtitleLanguages: async () => [{ code: 'en', name: 'English' }],
        rootFolders: async () => [], qualityProfiles: async () => [], qualities: async () => [], systemStatus: async () => ({}),
        saveSettings: async (_: string, value: unknown) => value,
        subtitleSyncTools: async () => ({ alass: true, ffsubsync: false, ffmpeg: true }),
      }),
    });
    const fixture = TestBed.createComponent(SettingsPage);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    button(el, 'Subtitles').click();
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
      api: mockApi({
        settings: async (section: string) => (section === 'downloads' ? { client: 'Embedded', qBittorrent: {} } : section === 'automation' ? { rssSyncIntervalMinutes: 15 } : section === 'languages' ? { preferred: [] } : {}),
        rootFolders: async () => [], qualityProfiles: async () => [], qualities: async () => [], systemStatus: async () => ({}),
        saveSettings: async (_: string, value: unknown) => value,
      }),
    });
    const fixture = TestBed.createComponent(SettingsPage);
    await settle(fixture);
    const el = fixture.nativeElement as HTMLElement;
    button(el, 'Download client').click();
    await settle(fixture);
    button(el, 'Save').click();
    await settle(fixture);
    expect(api.saveSettings).toHaveBeenCalledWith('downloads', expect.objectContaining({ client: 'Embedded' }));
    expect(api.saveSettings).toHaveBeenCalledWith('automation', { rssSyncIntervalMinutes: 15 });
  });
});
