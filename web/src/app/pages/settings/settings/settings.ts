import { Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { Api, SettingsMap, errorMessage } from '../../../core/api';
import { BytesPipe } from '../../../core/format';
import { Events } from '../../../core/events';
import {
  AutomationSettings, DirectoryListing, Language, SubtitleSettings, DownloadClientKind, DownloadClientSettings, DownloadClientTestResult, GeneralSettings, JellyfinSettings, JellyfinTestResult, MediaManagementSettings, MetadataSettings, OtlpProtocol, Quality,
  QualityProfile, RootFolder, ScheduledTaskInfo, SystemStatus, TelemetrySettings, TelemetryStatus,
} from '../../../core/models';
import { Toasts } from '../../../core/toast';
import { Modal } from '../../../shared/modal/modal';
import { TableSort } from '../../../core/table-sort';
import { SortHeader } from '../../../shared/sort-header/sort-header';
import { LanguagePicker } from '../../../shared/language-picker/language-picker';
import { SubtitleLanguages } from '../../../core/subtitles';
import { Languages } from '../../../core/languages';
import { Auth } from '../../../core/auth';
import { AccountSettings } from '../account/account';
import { UsersSettings } from '../users/users';

interface TokenDoc { token: string; description: string; example: string; }

const NAMING_TOKENS: { title: string; usedIn: string; tokens: TokenDoc[] }[] = [
  {
    title: 'Movie tokens',
    usedIn: 'Movie folder and movie file formats ({Quality} and {Release Group} only in the file format).',
    tokens: [
      { token: 'Movie Title', description: 'Title in the metadata language', example: 'Amélie: The Fabulous Destiny' },
      { token: 'Movie CleanTitle', description: 'Title with only letters, digits and spaces', example: 'Amélie The Fabulous Destiny' },
      { token: 'Original Title', description: 'Title in the original language (falls back to the title)', example: 'Le Fabuleux Destin d’Amélie Poulain' },
      { token: 'Release Year', description: 'Year of release', example: '2001' },
      { token: 'Quality', description: 'Quality of the file', example: 'Bluray-1080p' },
      { token: 'Release Group', description: 'Group that made the release, when known', example: 'KRK' },
      { token: 'TmdbId', description: 'TMDB id', example: '194' },
      { token: 'ImdbId', description: 'IMDb id, when known', example: 'tt0211915' },
    ],
  },
  {
    title: 'Series tokens',
    usedIn: 'Series folder (series tokens and ids), season folder ({Season} only) and episode file formats (all).',
    tokens: [
      { token: 'Series Title', description: 'Series title', example: 'The Office' },
      { token: 'Series CleanTitle', description: 'Title with only letters, digits and spaces', example: 'The Office' },
      { token: 'Series Year', description: 'Year the series started (empty when the title already ends with a year)', example: '2005' },
      { token: 'Season', description: 'Season number, two digits', example: '01' },
      { token: 'Episode', description: 'Episode number, two digits; several episodes as 01-E02', example: '01' },
      { token: 'Episode Title', description: 'Episode title; several episodes joined with " + "', example: 'Pilot' },
      { token: 'Quality', description: 'Quality of the file', example: 'WEBDL-1080p' },
      { token: 'Release Group', description: 'Group that made the release, when known', example: 'KRK' },
      { token: 'TmdbId', description: 'TMDB id, when known', example: '2316' },
      { token: 'TvdbId', description: 'TheTVDB id, when known', example: '73244' },
      { token: 'ImdbId', description: 'IMDb id, when known', example: 'tt0386676' },
    ],
  },
];

function sampleValues(tokens: TokenDoc[]): Record<string, string> {
  return Object.fromEntries(tokens.map(t => [t.token, t.example]));
}

/** Mirrors Krakarr.Media.Files.FileNameBuilder: expand tokens, drop empty brackets, sanitize. */
export function buildName(format: string, values: Record<string, string>): string {
  let result = format.replace(/\{([^{}]+)\}/g, (m, key: string) => (key in values ? values[key] : m));
  result = result.replace(/\[\s*\]|\(\s*\)/g, '');
  result = result.replace(/:/g, '-').replace(/[<>"|?*\\/\u0000-\u001f]/g, '');
  return result.trim().replace(/\.+$/, '').replace(/\s{2,}/g, ' ').trim();
}

type Tab = 'media' | 'profiles' | 'downloads' | 'subtitles' | 'metadata' | 'jellyfin' | 'telemetry' | 'tasks' | 'users' | 'general' | 'account';

@Component({
  selector: 'kr-settings',
  imports: [LanguagePicker, FormsModule, Modal, BytesPipe, AccountSettings, UsersSettings, SortHeader],
  templateUrl: './settings.html',
  styleUrl: './settings.scss',
})
export class SettingsPage implements OnInit {
  private readonly api = inject(Api);
  private readonly toasts = inject(Toasts);
  private readonly events = inject(Events);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly auth = inject(Auth);
  private readonly adminTabs: { id: Tab; label: string }[] = [
    { id: 'media', label: 'Media management' },
    { id: 'profiles', label: 'Quality profiles' },
    { id: 'downloads', label: 'Download client' },
    { id: 'subtitles', label: 'Subtitles' },
    { id: 'metadata', label: 'Metadata' },
    { id: 'jellyfin', label: 'Jellyfin' },
    { id: 'telemetry', label: 'Telemetry' },
    { id: 'tasks', label: 'Tasks' },
    { id: 'users', label: 'Users' },
    { id: 'general', label: 'General' },
    { id: 'account', label: 'Account' },
  ];
  /** Everyone manages their own account here; the rest of the settings are for admins. */
  protected readonly tabs = computed(() => this.auth.isAdmin() ? this.adminTabs : this.adminTabs.filter(t => t.id === 'account'));
  protected readonly tab = signal<Tab>(this.auth.isAdmin() ? 'media' : 'account');

  protected readonly rootFolders = signal<RootFolder[]>([]);
  protected readonly profiles = signal<QualityProfile[]>([]);
  protected readonly qualities = signal<Quality[]>([]);
  protected readonly media = signal<MediaManagementSettings | null>(null);
  protected readonly downloads = signal<DownloadClientSettings | null>(null);
  protected readonly automation = signal<AutomationSettings | null>(null);
  protected readonly metadata = signal<MetadataSettings | null>(null);
  protected readonly general = signal<GeneralSettings | null>(null);
  protected readonly status = signal<SystemStatus | null>(null);
  protected readonly profileForm = signal<QualityProfile | null>(null);
  protected readonly browser = signal<DirectoryListing | null>(null);
  protected readonly clientOptions: { id: DownloadClientKind; label: string; description: string }[] = [
    { id: 'Embedded', label: 'Built-in', description: 'Krakarr downloads torrents itself. Nothing else to run.' },
    { id: 'QBittorrent', label: 'qBittorrent', description: 'Use an existing qBittorrent via its WebUI; only tagged torrents are managed.' },
  ];
  protected readonly clientTest = signal<DownloadClientTestResult | null>(null);
  protected readonly subtitles = signal<SubtitleSettings | null>(null);
  protected readonly jellyfin = signal<JellyfinSettings | null>(null);
  protected readonly jellyfinTest = signal<JellyfinTestResult | null>(null);
  protected readonly testingJellyfin = signal(false);
  protected readonly telemetry = signal<TelemetrySettings | null>(null);
  protected readonly telemetryStatus = signal<TelemetryStatus | null>(null);
  protected readonly saving = signal(false);
  /** Tabs whose settings are saved with the footer's Save button, once they have loaded. */
  protected readonly saveable = computed(() => {
    switch (this.tab()) {
      case 'media': return !!this.media();
      case 'downloads': return !!this.downloads() || !!this.automation();
      case 'subtitles': return !!this.subtitles();
      case 'metadata': return !!this.metadata();
      case 'jellyfin': return !!this.jellyfin();
      case 'telemetry': return !!this.telemetry();
      default: return false;
    }
  });
  /** Wait after saving before reading the status: the server restarts the exporters in the background. */
  telemetryStatusDelayMs = 1500;
  protected readonly languages = signal<Language[]>([]);
  protected readonly languageCatalog = inject(Languages);
  private readonly subtitleLanguages = inject(SubtitleLanguages);
  protected wantedLanguages: string[] = [];
  protected readonly subtitleTest = signal<{ success: boolean; message: string } | null>(null);
  protected readonly subdlTest = signal<{ success: boolean; message: string } | null>(null);
  protected readonly testingSubtitles = signal(false);
  protected readonly testingClient = signal(false);
  protected readonly selectableQualities = computed(() => this.qualities().filter(q => q.id !== 0));
  protected readonly tokenDocs = NAMING_TOKENS;
  protected readonly sampleMovie = sampleValues(NAMING_TOKENS[0].tokens);
  protected readonly sampleEpisode = sampleValues(NAMING_TOKENS[1].tokens);
  protected readonly sampleMultiEpisode = { ...this.sampleEpisode, 'Episode': '01-E02', 'Episode Title': 'Pilot + Diversity Day' };
  /** Client-side approximation of the server's FileNameBuilder, for the examples under each format. */
  protected example(format: string, values: Record<string, string>) { return buildName(format ?? '', values); }
  protected browsePath = '/';
  protected rootMediaType: 'Movie' | 'Series' = 'Movie';
  protected readonly tasks = signal<ScheduledTaskInfo[]>([]);
  protected readonly taskSort = new TableSort(this.tasks, {
    name: t => t.name,
    interval: t => t.intervalSeconds,
    lastRun: { value: t => t.isRunning ? Number.MAX_SAFE_INTEGER : t.lastRun ? Date.parse(t.lastRun) : null, desc: true },
    duration: { value: t => t.lastDurationSeconds, desc: true },
    nextRun: t => t.isRunning || !t.nextRun ? null : Date.parse(t.nextRun),
  });
  /** Tasks whose "Run now" request is in flight, so the button can't be clicked twice. */
  protected readonly starting = signal(new Set<string>());
  /** Ticks so relative times ("in 4m") stay current. */
  private readonly now = signal(Date.now());

  async ngOnInit() {
    if (!this.auth.isAdmin()) return;
    this.events.taskUpdated.pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe(t => this.tasks.update(list => list.map(x => (x.id === t.id ? t : x))));
    const tick = setInterval(() => this.now.set(Date.now()), 15_000);
    this.destroyRef.onDestroy(() => clearInterval(tick));
    this.api.tasks().then(t => this.tasks.set(t), () => {});
    const [roots, profiles, qualities, media, downloads, automation, metadata, general, status] = await Promise.all([
      this.api.rootFolders(), this.api.qualityProfiles(), this.api.qualities(), this.api.settings('media'),
      this.api.settings('downloads'), this.api.settings('automation'), this.api.settings('metadata'),
      this.api.settings('general'), this.api.systemStatus(),
    ]);
    this.rootFolders.set(roots);
    this.profiles.set(profiles);
    this.qualities.set(qualities);
    this.media.set(media);
    this.downloads.set(downloads);
    this.automation.set(automation);
    this.metadata.set(metadata);
    this.general.set(general);
    this.status.set(status);
    this.api.settings('languages').then(l => this.wantedLanguages = l.preferred, () => {});
    this.api.settings('jellyfin').then(j => this.jellyfin.set(j), () => {});
    this.api.settings('telemetry').then(t => this.telemetry.set(t), () => {});
    this.api.telemetryStatus().then(s => this.telemetryStatus.set(s), () => {});
    this.languageCatalog.load().catch(() => {});
    const [subs, langs] = await Promise.all([this.api.settings('subtitles'), this.api.subtitleLanguages()]);
    if (subs) subs.subdl ??= { enabled: true };
    this.subtitles.set(subs);
    this.languages.set(langs);
  }

  /** Compact duration without zero parts: "30m", "1d", "1h 30m", "4.2s". */
  protected duration(seconds: number, precise = false) {
    if (precise && seconds < 10) return seconds < 0.1 ? '< 0.1s' : `${seconds.toFixed(1)}s`;
    const s = Math.round(seconds);
    const parts = [[Math.floor(s / 86400), 'd'], [Math.floor((s % 86400) / 3600), 'h'], [Math.floor((s % 3600) / 60), 'm'], [s % 60, 's']] as const;
    const first = parts.findIndex(([n]) => n > 0);
    if (first < 0) return '0s';
    return parts.slice(first, first + 2).filter(([n]) => n > 0).map(([n, u]) => `${n}${u}`).join(' ');
  }

  /** "3m ago" / "in 4m", re-rendered while the page is open. */
  protected when(iso: string) {
    this.now(); // re-evaluate on each tick
    const seconds = (new Date(iso).getTime() - Date.now()) / 1000;
    if (Math.abs(seconds) < 30) return seconds > 0 ? 'soon' : 'just now';
    return seconds > 0 ? `in ${this.duration(seconds)}` : `${this.duration(-seconds)} ago`;
  }

  /** Like {@link when} for past times; clock skew between browser and server never makes them "soon". */
  protected ago(iso: string) {
    this.now();
    const seconds = Math.max(0, (Date.now() - new Date(iso).getTime()) / 1000);
    return seconds < 30 ? 'just now' : `${this.duration(seconds)} ago`;
  }

  async runTask(task: ScheduledTaskInfo) {
    this.starting.update(s => new Set(s).add(task.id));
    try {
      await this.api.runTask(task.id);
      this.toasts.success(`${task.name} started`);
      this.tasks.set(await this.api.tasks());
    } catch (e) {
      this.toasts.error(errorMessage(e));
    } finally {
      this.starting.update(s => { const next = new Set(s); next.delete(task.id); return next; });
    }
  }

  /** "es-ES" → "ES", used as the release region placeholder. */
  protected regionFromLanguage(language: string) { return /^[a-z]{2,3}-([A-Za-z]{2})$/.exec(language?.trim() ?? '')?.[1].toUpperCase() ?? ''; }
  protected languageName(code: string) { return this.languages().find(l => l.code === code)?.name ?? code; }
  protected addLanguage(list: string[], code: string) { return code && !list.includes(code) ? [...list, code] : list; }
  protected removeLanguage(list: string[], code: string) { return list.filter(c => c !== code); }

  async testSubtitles(settings: SubtitleSettings, provider: 'opensubtitles' | 'subdl' = 'opensubtitles') {
    this.testingSubtitles.set(true);
    const target = provider === 'subdl' ? this.subdlTest : this.subtitleTest;
    try {
      const results = await this.api.testSubtitleProviders(settings);
      const r = results.find(x => x.name === provider) ?? results[0];
      target.set(r ? { success: r.success, message: r.message } : { success: false, message: 'No providers' });
    } catch (e) {
      target.set({ success: false, message: errorMessage(e) });
    } finally {
      this.testingSubtitles.set(false);
    }
  }

  protected qualitiesFor(p: QualityProfile) {
    return this.qualities().filter(q => p.allowedQualityIds.includes(q.id)).sort((a, b) => b.weight - a.weight);
  }

  /** Saves every section shown on the current tab (the footer's Save button). */
  async saveTab() {
    if (this.saving()) return;
    this.saving.set(true);
    try {
      switch (this.tab()) {
        case 'media': await this.saveSection('media', this.media()); break;
        case 'downloads': await Promise.all([this.saveSection('downloads', this.downloads()), this.saveSection('automation', this.automation())]); break;
        case 'subtitles':
          await this.saveSection('subtitles', this.subtitles());
          this.subtitleLanguages.load(true).catch(() => {});
          break;
        case 'metadata': await this.saveSection('metadata', this.metadata()); break;
        case 'jellyfin': await this.saveSection('jellyfin', this.jellyfin()); break;
        case 'telemetry': await this.saveTelemetry(); return;
        default: return;
      }
      this.toasts.success('Settings saved');
    } catch (e) {
      this.toasts.error(errorMessage(e));
    } finally {
      this.saving.set(false);
    }
  }

  private async saveSection<K extends 'media' | 'downloads' | 'automation' | 'metadata' | 'subtitles' | 'jellyfin'>(section: K, value: SettingsMap[K] | null) {
    if (value) await this.api.saveSettings(section, value);
  }

  async saveLanguages() {
    try {
      this.wantedLanguages = (await this.api.saveSettings('languages', { preferred: this.wantedLanguages })).preferred;
      await this.languageCatalog.load(true);
      this.toasts.success('Settings saved');
    } catch (e) {
      this.toasts.error(errorMessage(e));
    }
  }

  /** Switching protocol moves an endpoint on the other protocol's default port (4317 gRPC, 4318 HTTP) along with it. */
  protected setProtocol(t: TelemetrySettings, protocol: OtlpProtocol) {
    const [from, to] = protocol === 'Grpc' ? [':4318', ':4317'] : [':4317', ':4318'];
    if (t.endpoint?.endsWith(from)) t.endpoint = t.endpoint.slice(0, -from.length) + to;
    t.protocol = protocol;
  }

  private async saveTelemetry() {
    const settings = this.telemetry();
    if (!settings) return;
    this.telemetry.set(await this.api.saveSettings('telemetry', settings));
    this.toasts.success('Settings saved');
    await new Promise(resolve => setTimeout(resolve, this.telemetryStatusDelayMs));
    this.telemetryStatus.set(await this.api.telemetryStatus());
  }

  async testJellyfin(settings: JellyfinSettings) {
    this.testingJellyfin.set(true);
    try { this.jellyfinTest.set(await this.api.testJellyfin(settings)); }
    catch (e) { this.jellyfinTest.set({ success: false, message: errorMessage(e) }); }
    finally { this.testingJellyfin.set(false); }
  }

  async testClient(settings: DownloadClientSettings) {
    this.testingClient.set(true);
    try { this.clientTest.set(await this.api.testDownloadClient(settings)); }
    catch (e) { this.clientTest.set({ success: false, message: errorMessage(e) }); }
    finally { this.testingClient.set(false); }
  }

  // Root folders
  async openBrowser() {
    this.browsePath = this.rootFolders()[0]?.path ?? '/';
    await this.browse(this.browsePath);
  }

  async browse(path: string) {
    try {
      const listing = await this.api.browse(path);
      this.browsePath = listing.path;
      this.browser.set(listing);
    } catch {
      this.browser.set({ path, directories: [] });
    }
  }

  async addRoot() {
    try {
      await this.api.addRootFolder(this.browsePath, this.rootMediaType);
      this.browser.set(null);
      this.rootFolders.set(await this.api.rootFolders());
    } catch (e) {
      this.toasts.error(errorMessage(e));
    }
  }

  async deleteRoot(r: RootFolder) {
    await this.api.deleteRootFolder(r.id);
    this.rootFolders.set(await this.api.rootFolders());
  }

  // Profiles
  editProfile(p: QualityProfile | null) {
    this.profileForm.set(p ? structuredClone(p) : {
      id: 0, name: '', allowedQualityIds: [], cutoffQualityId: 0, upgradeAllowed: true, minSizeMb: 0, maxSizeMb: 0,
    });
  }

  toggleQuality(p: QualityProfile, id: number) {
    p.allowedQualityIds = p.allowedQualityIds.includes(id) ? p.allowedQualityIds.filter(x => x !== id) : [...p.allowedQualityIds, id];
    if (!p.allowedQualityIds.includes(p.cutoffQualityId)) p.cutoffQualityId = this.qualitiesFor(p)[0]?.id ?? 0;
    this.profileForm.set({ ...p });
  }

  async saveProfile(p: QualityProfile) {
    try {
      await this.api.saveQualityProfile(p);
      this.profileForm.set(null);
      this.profiles.set(await this.api.qualityProfiles());
      this.toasts.success('Profile saved');
    } catch (e) {
      this.toasts.error(errorMessage(e));
    }
  }

  async deleteProfile(p: QualityProfile) {
    try {
      await this.api.deleteQualityProfile(p.id);
      this.profileForm.set(null);
      this.profiles.set(await this.api.qualityProfiles());
    } catch (e) {
      this.toasts.error(errorMessage(e));
    }
  }

  // General
  async regenerate() {
    if (!confirm('Regenerate the API key? Existing integrations will stop working.')) return;
    this.general.set(await this.api.regenerateApiKey());
  }

  copy(text: string) {
    navigator.clipboard?.writeText(text).then(() => this.toasts.success('Copied'));
  }
}
