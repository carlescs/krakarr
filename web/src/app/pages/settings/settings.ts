import { Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { Api, errorMessage } from '../../core/api';
import { BytesPipe } from '../../core/format';
import { Events } from '../../core/events';
import {
  AutomationSettings, DirectoryListing, Language, SubtitleSettings, DownloadClientKind, DownloadClientSettings, DownloadClientTestResult, GeneralSettings, MediaManagementSettings, MetadataSettings, Quality,
  QualityProfile, RootFolder, ScheduledTaskInfo, SystemStatus,
} from '../../core/models';
import { Toasts } from '../../core/toast';
import { Modal } from '../../shared/modal';
import { Auth } from '../../core/auth';
import { AccountSettings } from './account';
import { UsersSettings } from './users';

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

type Tab = 'media' | 'profiles' | 'downloads' | 'subtitles' | 'metadata' | 'tasks' | 'users' | 'general' | 'account';

@Component({
  selector: 'kr-settings',
  imports: [FormsModule, Modal, BytesPipe, AccountSettings, UsersSettings],
  template: `
    <h1 class="page-title mb-4 sm:mb-6">{{ auth.isAdmin() ? 'Settings' : 'Account' }}</h1>
    <div class="-mx-4 mb-6 flex gap-1 overflow-x-auto border-b border-surface-700 px-4 [scrollbar-width:none] sm:mx-0 sm:px-0" [class.hidden]="tabs().length < 2">
      @for (t of tabs(); track t.id) {
        <button class="-mb-px whitespace-nowrap border-b-2 px-4 py-2 text-sm font-medium transition"
                [class]="tab() === t.id ? 'border-accent-400 text-white' : 'border-transparent text-slate-400 hover:text-slate-200'"
                (click)="tab.set(t.id)">{{ t.label }}</button>
      }
    </div>

    <!-- Media management -->
    @if (tab() === 'media') {
      <div class="space-y-6">
        <section class="card p-4 sm:p-5">
          <div class="mb-4 flex items-center">
            <h2 class="flex-1 font-semibold text-white">Root folders</h2>
            <button class="btn-secondary btn-sm" (click)="openBrowser()">+ Add root folder</button>
          </div>
          @if (rootFolders().length === 0) {
            <p class="text-sm text-amber-300">Add root folders where movies and series will be stored (e.g. /data/media/movies and /data/media/tv).</p>
          }
          <ul class="divide-y divide-surface-800">
            @for (r of rootFolders(); track r.id) {
              <li class="flex flex-wrap items-center gap-x-3 gap-y-1 py-2 text-sm">
                <span class="badge" [class]="r.mediaType === 'Series' ? 'bg-violet-500/15 text-violet-300' : 'bg-accent-500/15 text-accent-300'">{{ r.mediaType === 'Series' ? 'Series' : 'Movies' }}</span>
                <span class="min-w-0 flex-1 break-all font-mono">{{ r.path }}</span>
                @if (!r.accessible) { <span class="badge bg-rose-500/15 text-rose-300">Not accessible</span> }
                @if (r.freeSpace != null) { <span class="text-slate-400">{{ r.freeSpace | bytes }} free</span> }
                <button class="btn-ghost btn-sm text-rose-300" (click)="deleteRoot(r)">Remove</button>
              </li>
            }
          </ul>
        </section>
        @if (media(); as m) {
          <section class="card space-y-4 p-4 sm:p-5">
            <h2 class="font-semibold text-white">File management</h2>
            <label class="flex items-center gap-2 text-sm"><input type="checkbox" class="accent-accent-500" [(ngModel)]="m.renameFiles" /> Rename movie and episode files (off keeps the original file names)</label>
            <div>
              <label class="label">Movie folder format</label><input class="input font-mono" [(ngModel)]="m.movieFolderFormat" />
              <p class="hint break-all">Example: <span class="font-mono text-slate-400">{{ example(m.movieFolderFormat, sampleMovie) }}</span></p>
            </div>
            <div>
              <label class="label">Movie file format</label><input class="input font-mono" [(ngModel)]="m.movieFileFormat" />
              <p class="hint break-all">Example: <span class="font-mono text-slate-400">{{ example(m.movieFileFormat, sampleMovie) }}.mkv</span></p>
            </div>
            <div>
              <label class="label">Series folder format</label><input class="input font-mono" [(ngModel)]="m.seriesFolderFormat" />
              <p class="hint break-all">Example: <span class="font-mono text-slate-400">{{ example(m.seriesFolderFormat, sampleEpisode) }}</span></p>
            </div>
            <div>
              <label class="label">Season folder format</label><input class="input font-mono" [(ngModel)]="m.seasonFolderFormat" />
              <p class="hint break-all">Example: <span class="font-mono text-slate-400">{{ example(m.seasonFolderFormat, sampleEpisode) }}</span> · season 0 always goes to <span class="font-mono">Specials</span></p>
            </div>
            <div>
              <label class="label">Episode file format</label><input class="input font-mono" [(ngModel)]="m.episodeFileFormat" />
              <p class="hint break-all">Example: <span class="font-mono text-slate-400">{{ example(m.episodeFileFormat, sampleEpisode) }}.mkv</span></p>
              <p class="hint break-all">Two-episode file: <span class="font-mono text-slate-400">{{ example(m.episodeFileFormat, sampleMultiEpisode) }}.mkv</span></p>
            </div>

            <details class="rounded-lg border border-surface-700 bg-surface-900/40 text-sm">
              <summary class="cursor-pointer select-none px-4 py-2.5 font-medium text-slate-200">Naming reference</summary>
              <div class="space-y-5 border-t border-surface-700 px-4 py-4 text-slate-300">
                <p>
                  Write any text and put tokens in braces, e.g. <code class="font-mono text-accent-300">{{ '{Movie Title} ({Release Year})' }}</code>.
                  Token names are case sensitive. Unknown tokens are left as typed.
                </p>
                @for (group of tokenDocs; track group.title) {
                  <div>
                    <h3 class="mb-1 text-xs font-semibold uppercase tracking-wide text-slate-400">{{ group.title }}</h3>
                    <p class="mb-2 text-xs text-slate-500">{{ group.usedIn }}</p>
                    <div class="overflow-x-auto">
                      <table class="table text-xs">
                        <thead><tr><th>Token</th><th>Value</th><th class="max-sm:hidden">Example</th></tr></thead>
                        <tbody>
                          @for (t of group.tokens; track t.token) {
                            <tr>
                              <td class="whitespace-nowrap font-mono text-accent-300">{{ '{' + t.token + '}' }}</td>
                              <td>{{ t.description }}</td>
                              <td class="whitespace-nowrap font-mono text-slate-400 max-sm:hidden">{{ t.example }}</td>
                            </tr>
                          }
                        </tbody>
                      </table>
                    </div>
                  </div>
                }
                <div>
                  <h3 class="mb-1 text-xs font-semibold uppercase tracking-wide text-slate-400">Rules</h3>
                  <ul class="list-disc space-y-1 pl-5 text-xs text-slate-400">
                    <li>A token without a value (no release group, no year…) becomes empty, and brackets left empty by it, <span class="font-mono">[]</span> or <span class="font-mono">()</span>, are removed.</li>
                    <li><span class="font-mono">:</span> becomes <span class="font-mono">-</span>; other characters not allowed in file names (<span class="font-mono">/ \ * ? " &lt; &gt; |</span>) are dropped. Repeated spaces collapse and trailing dots are trimmed.</li>
                    <li>File formats get the original file extension appended; don't add one yourself.</li>
                    <li>A file holding several episodes lists them as <span class="font-mono">E01-E02</span> in <span class="font-mono">{{ '{Episode}' }}</span> and joins their titles with <span class="font-mono">+</span>.</li>
                    <li><span class="font-mono">{{ '{Series Year}' }}</span> is left empty when the series title already ends with a year, e.g. "Doctor Who (2005)".</li>
                    <li>Season folders are only used for series that have "Use season folders" enabled.</li>
                    <li>Formats apply to new imports. To bring existing series in line, use <span class="font-medium text-slate-300">Organize</span> on the series page; it shows a preview first.</li>
                  </ul>
                </div>
              </div>
            </details>
            <label class="flex items-center gap-2 text-sm"><input type="checkbox" class="accent-accent-500" [(ngModel)]="m.useHardlinks" /> Use hardlinks instead of copies (keeps seeding without doubling disk usage)</label>
            <div class="max-w-xs"><label class="label">Minimum video size (MB)</label><input class="input" type="number" min="0" [(ngModel)]="m.minimumVideoFileSizeMb" /></div>
            <div class="flex justify-end"><button class="btn-primary" (click)="save('media', m)">Save</button></div>
          </section>
        }
      </div>
    }

    <!-- Quality profiles -->
    @if (tab() === 'profiles') {
      <div class="mb-4 flex justify-end"><button class="btn-secondary btn-sm" (click)="editProfile(null)">+ Add profile</button></div>
      <div class="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
        @for (p of profiles(); track p.id) {
          <button class="card flex flex-col items-start justify-start p-4 sm:p-5 text-left transition hover:border-accent-500/50" (click)="editProfile(p)">
            <div class="font-semibold text-white">{{ p.name }}</div>
            <div class="mt-2 flex flex-wrap gap-1">
              @for (q of qualitiesFor(p); track q.id) {
                <span class="badge" [class]="q.id === p.cutoffQualityId ? 'bg-accent-500/20 text-accent-300' : 'bg-surface-700 text-slate-300'">{{ q.name }}</span>
              }
            </div>
          </button>
        }
      </div>
    }

    <!-- Download client -->
    @if (tab() === 'downloads') {
      @if (downloads(); as d) {
        <section class="card mb-6 p-4 sm:p-5">
          <h2 class="mb-3 font-semibold text-white">Download client</h2>
          <div class="grid gap-3 sm:grid-cols-2">
            @for (opt of clientOptions; track opt.id) {
              <label class="flex cursor-pointer gap-3 rounded-lg border p-4 transition"
                     [class]="d.client === opt.id ? 'border-accent-500 bg-accent-500/5' : 'border-surface-700 hover:border-surface-600'">
                <input type="radio" name="client" class="mt-1 accent-accent-500" [value]="opt.id" [(ngModel)]="d.client" (ngModelChange)="clientTest.set(null)" />
                <span>
                  <span class="block text-sm font-medium text-white">{{ opt.label }}</span>
                  <span class="block text-xs text-slate-400">{{ opt.description }}</span>
                </span>
              </label>
            }
          </div>
        </section>

        @if (d.client === 'QBittorrent') {
          <section class="card grid gap-4 p-4 sm:p-5 sm:grid-cols-2">
            <h2 class="font-semibold text-white sm:col-span-2">qBittorrent</h2>
            <div class="sm:col-span-2">
              <label class="label">WebUI URL</label>
              <input class="input font-mono" [(ngModel)]="d.qBittorrent.url" placeholder="http://qbittorrent:8080" />
            </div>
            <div><label class="label">Username</label><input class="input" [(ngModel)]="d.qBittorrent.username" autocomplete="off" /></div>
            <div><label class="label">Password</label><input class="input" type="password" [(ngModel)]="d.qBittorrent.password" autocomplete="new-password" /></div>
            <div>
              <label class="label">Tag</label>
              <input class="input" [(ngModel)]="d.qBittorrent.tag" />
              <p class="hint">Krakarr tags every torrent it adds and only shows and manages torrents with this tag.</p>
            </div>
            <div>
              <label class="label">Category (optional)</label>
              <input class="input" [(ngModel)]="d.qBittorrent.category" placeholder="per download (movies, manual…)" />
            </div>
            <div class="sm:col-span-2">
              <label class="label">Save path (optional)</label>
              <input class="input font-mono" [(ngModel)]="d.qBittorrent.savePath" placeholder="qBittorrent default" />
            </div>
            <div>
              <label class="label">Remote path (as qBittorrent sees it)</label>
              <input class="input font-mono" [(ngModel)]="d.qBittorrent.remotePathPrefix" placeholder="/downloads" />
            </div>
            <div>
              <label class="label">Local path (as Krakarr sees it)</label>
              <input class="input font-mono" [(ngModel)]="d.qBittorrent.localPathPrefix" placeholder="/data/downloads" />
            </div>
            <p class="hint sm:col-span-2">
              Only needed when qBittorrent runs in another container and sees the downloads under a different path.
            </p>
            @if (clientTest(); as t) {
              <div class="rounded-lg px-3 py-2 text-sm sm:col-span-2" [class]="t.success ? 'bg-emerald-500/10 text-emerald-300' : 'bg-rose-500/10 text-rose-300'">
                @if (t.success) { Connected to {{ t.version }} · {{ t.message }} } @else { {{ t.message }} }
              </div>
            }
            <div class="flex justify-end gap-2 sm:col-span-2">
              <button class="btn-secondary" (click)="testClient(d)" [disabled]="testingClient()">{{ testingClient() ? 'Testing…' : 'Test' }}</button>
              <button class="btn-primary" (click)="save('downloads', d)">Save</button>
            </div>
          </section>
        } @else {
        <section class="card grid gap-4 p-4 sm:p-5 sm:grid-cols-2">
          <h2 class="font-semibold text-white sm:col-span-2">Built-in torrent client</h2>
          <div class="sm:col-span-2"><label class="label">Download directory</label><input class="input font-mono" [(ngModel)]="d.downloadDirectory" [placeholder]="status()?.downloadDirectory ?? ''" />
            <p class="hint">For hardlinks to work, downloads and your library must be on the same filesystem.</p></div>
          <div><label class="label">Listening port</label><input class="input" type="number" [(ngModel)]="d.listenPort" /></div>
          <div><label class="label">Max connections</label><input class="input" type="number" [(ngModel)]="d.maxConnections" /></div>
          <div><label class="label">Download limit (KiB/s, 0 = ∞)</label><input class="input" type="number" min="0" [(ngModel)]="d.maxDownloadRateKiB" /></div>
          <div><label class="label">Upload limit (KiB/s, 0 = ∞)</label><input class="input" type="number" min="0" [(ngModel)]="d.maxUploadRateKiB" /></div>
          <label class="flex items-center gap-2 text-sm"><input type="checkbox" class="accent-accent-500" [(ngModel)]="d.enableDht" /> Enable DHT</label>
          <label class="flex items-center gap-2 text-sm"><input type="checkbox" class="accent-accent-500" [(ngModel)]="d.enablePortForwarding" /> UPnP / NAT-PMP port forwarding</label>
          <div class="flex justify-end sm:col-span-2"><button class="btn-primary" (click)="save('downloads', d)">Save</button></div>
        </section>
        }
        <section class="card mt-6 grid gap-4 p-4 sm:p-5 sm:grid-cols-2">
          <h2 class="font-semibold text-white sm:col-span-2">Seeding</h2>
          <div><label class="label">Seed ratio limit (0 = none)</label><input class="input" type="number" step="0.1" min="0" [(ngModel)]="d.seedRatioLimit" /></div>
          <div><label class="label">Seed time limit (minutes, 0 = none)</label><input class="input" type="number" min="0" [(ngModel)]="d.seedTimeLimitMinutes" /></div>
          <div class="sm:col-span-2">
            <label class="label">When the limit is reached</label>
            <select class="input" [(ngModel)]="d.seedLimitAction">
              <option value="Remove">Remove from the download client</option>
              <option value="Stop">Stop seeding and keep it in the download client</option>
            </select>
            <p class="hint">
              Applies once a download has finished and its movie or episodes have been imported. Removing an imported download also deletes
              its downloaded files (the library keeps its copy); other downloads keep their files.
              @if (d.client === 'QBittorrent') { With both limits at 0, qBittorrent's own share limits apply. }
            </p>
          </div>
          <div class="flex justify-end sm:col-span-2"><button class="btn-primary" (click)="save('downloads', d)">Save</button></div>
        </section>
      }
      @if (automation(); as a) {
        <section class="card mt-6 grid gap-4 p-4 sm:p-5 sm:grid-cols-2">
          <h2 class="font-semibold text-white sm:col-span-2">Automation</h2>
          <div><label class="label">RSS sync interval (minutes, 0 = off)</label><input class="input" type="number" min="0" [(ngModel)]="a.rssSyncIntervalMinutes" /></div>
          <div class="flex justify-end sm:col-span-2"><button class="btn-primary" (click)="save('automation', a)">Save</button></div>
        </section>
      }
    }

    <!-- Subtitles -->
    @if (tab() === 'subtitles') {
      @if (subtitles(); as st) {
        <div class="space-y-6">
          <section class="card space-y-4 p-4 sm:p-5">
            <label class="flex items-center gap-2 text-sm font-medium text-white"><input type="checkbox" class="accent-accent-500" [(ngModel)]="st.enabled" /> Download subtitles automatically</label>
            <div>
              <label class="label">Languages</label>
              <div class="flex flex-wrap items-center gap-2">
                @for (code of st.languages; track code) {
                  <span class="badge bg-accent-500/15 text-accent-300">{{ languageName(code) }}
                    <button class="ml-1 hover:text-white" (click)="st.languages = removeLanguage(st.languages, code)" aria-label="Remove">✕</button>
                  </span>
                }
                <select class="input w-full sm:w-56" (change)="st.languages = addLanguage(st.languages, $any($event.target).value); $any($event.target).value = ''">
                  <option value="">Add language…</option>
                  @for (l of languages(); track l.code) {
                    @if (!st.languages.includes(l.code)) { <option [value]="l.code">{{ l.name }}</option> }
                  }
                </select>
              </div>
              <p class="hint">Subtitles are stored next to the video as e.g. “Movie (2020).en.srt”. Existing files like that are detected automatically.</p>
            </div>
            <div class="grid gap-4 sm:grid-cols-2">
              <div>
                <label class="label">Hearing impaired</label>
                <select class="input" [(ngModel)]="st.hearingImpaired">
                  <option value="PreferNormal">Prefer normal subtitles</option>
                  <option value="PreferHearingImpaired">Prefer hearing impaired (SDH)</option>
                  <option value="ExcludeHearingImpaired">Never hearing impaired</option>
                </select>
              </div>
              <div><label class="label">Retry missing every (hours)</label><input class="input" type="number" min="1" [(ngModel)]="st.searchIntervalHours" /></div>
              <div><label class="label">Minimum score · movies</label><input class="input" type="number" min="0" max="100" [(ngModel)]="st.minimumScoreMovies" /></div>
              <div><label class="label">Minimum score · episodes</label><input class="input" type="number" min="0" max="100" [(ngModel)]="st.minimumScoreEpisodes" /></div>
            </div>
            <label class="flex items-center gap-2 text-sm"><input type="checkbox" class="accent-accent-500" [(ngModel)]="st.includeAiTranslated" /> Allow AI / machine translated subtitles</label>
            <p class="hint">Score: 100 for a file-hash match; otherwise title, year or season/episode, source, resolution, release group and codec of the subtitle's release are compared with your file.</p>
          </section>
          <section class="card grid gap-4 p-4 sm:p-5 sm:grid-cols-2">
            <h2 class="font-semibold text-white sm:col-span-2">OpenSubtitles.com</h2>
            <div class="sm:col-span-2">
              <label class="label">API key</label>
              <input class="input font-mono" type="password" autocomplete="off" [(ngModel)]="st.openSubtitles.apiKey" />
              <p class="hint">Create a consumer at opensubtitles.com → API consumers.</p>
            </div>
            <div><label class="label">Username (optional)</label><input class="input" autocomplete="off" [(ngModel)]="st.openSubtitles.username" /></div>
            <div><label class="label">Password</label><input class="input" type="password" autocomplete="new-password" [(ngModel)]="st.openSubtitles.password" /></div>
            <p class="hint sm:col-span-2">Without an account, downloads are limited to a few per day; a free account raises the quota.</p>
            @if (subtitleTest(); as t) {
              <div class="rounded-lg px-3 py-2 text-sm sm:col-span-2" [class]="t.success ? 'bg-emerald-500/10 text-emerald-300' : 'bg-rose-500/10 text-rose-300'">{{ t.message }}</div>
            }
            <div class="flex justify-end gap-2 sm:col-span-2">
              <button class="btn-secondary" (click)="testSubtitles(st)" [disabled]="testingSubtitles()">{{ testingSubtitles() ? 'Testing…' : 'Test' }}</button>
              <button class="btn-primary" (click)="save('subtitles', st)">Save</button>
            </div>
          </section>
        </div>
      }
    }

    <!-- Metadata -->
    @if (tab() === 'metadata') {
      @if (metadata(); as m) {
        <section class="card space-y-4 p-4 sm:p-5">
          <div>
            <label class="label">TMDB API key or read access token</label>
            <input class="input font-mono" type="password" autocomplete="off" [(ngModel)]="m.tmdbApiKey" />
            <p class="hint">Free at themoviedb.org → Settings → API. Always used for movies.</p>
          </div>
          <div class="max-w-xs">
            <label class="label">Series metadata source</label>
            <select class="input" [(ngModel)]="m.seriesSource">
              <option value="Tmdb">TMDB</option>
              <option value="Tvdb">TheTVDB</option>
            </select>
            <p class="hint">Where shows, seasons and episodes come from. Existing series switch on their next refresh.</p>
          </div>
          @if (m.seriesSource === 'Tvdb') {
            <div>
              <label class="label">TheTVDB API key</label>
              <input class="input font-mono" type="password" autocomplete="off" [(ngModel)]="m.tvdbApiKey" />
              <p class="hint">From thetvdb.com → Dashboard → API keys.</p>
            </div>
            <div class="max-w-xs"><label class="label">TheTVDB subscriber PIN (optional)</label><input class="input" [(ngModel)]="m.tvdbPin" /></div>
          }
          <div class="grid gap-4 sm:grid-cols-2">
            <div><label class="label">Language</label><input class="input" [(ngModel)]="m.language" placeholder="en-US" /></div>
            <div>
              <label class="label">Release region</label>
              <input class="input uppercase" maxlength="2" [(ngModel)]="m.releaseRegion" [placeholder]="'e.g. ' + (regionFromLanguage(m.language) || 'US')" />
              <p class="hint">Two-letter country code (ES, US, GB…). The calendar shows this country's cinema, digital and physical dates, and falls back to the earliest date worldwide. Leave empty to always use the earliest.</p>
            </div>
          </div>
          <div class="flex justify-end"><button class="btn-primary" (click)="save('metadata', m)">Save</button></div>
        </section>
      }
    }

    @if (tab() === 'account') { <kr-account-settings /> }
    @if (tab() === 'users') { <kr-users-settings /> }

    <!-- Tasks -->
    @if (tab() === 'tasks') {
      <section class="card p-4 sm:p-5">
        <h2 class="font-semibold text-white">Scheduled tasks</h2>
        <p class="hint mb-3">Tasks run on their own schedule. <span class="font-medium text-slate-300">Run now</span> starts one immediately and counts its next run from now.</p>
        <div class="overflow-x-auto">
          <table class="table">
            <thead><tr><th>Task</th><th class="max-sm:hidden">Interval</th><th>Last run</th><th class="max-md:hidden">Duration</th><th class="max-sm:hidden">Next run</th><th></th></tr></thead>
            <tbody>
              @for (t of tasks(); track t.id) {
                <tr>
                  <td class="text-slate-200">{{ t.name }}</td>
                  <td class="whitespace-nowrap text-slate-400 max-sm:hidden">{{ t.intervalSeconds ? 'Every ' + duration(t.intervalSeconds) : 'Disabled' }}</td>
                  <td class="whitespace-nowrap">
                    @if (t.isRunning) {
                      <span class="text-accent-300">Running…</span>
                    } @else if (t.lastRun) {
                      <span [class]="t.lastSucceeded === false ? 'text-red-400' : 'text-slate-300'" [title]="t.lastError ?? ''">
                        {{ ago(t.lastRun) }}{{ t.lastSucceeded === false ? ' (failed)' : '' }}
                      </span>
                    } @else {
                      <span class="text-slate-500">Never</span>
                    }
                  </td>
                  <td class="whitespace-nowrap text-slate-400 max-md:hidden">{{ t.lastDurationSeconds == null ? '—' : duration(t.lastDurationSeconds, true) }}</td>
                  <td class="whitespace-nowrap text-slate-400 max-sm:hidden">{{ t.isRunning ? '—' : t.nextRun ? when(t.nextRun) : '—' }}</td>
                  <td class="text-right">
                    <button class="btn-secondary whitespace-nowrap px-3 py-1 text-xs" [disabled]="t.isRunning || starting().has(t.id)" (click)="runTask(t)">Run now</button>
                  </td>
                </tr>
              } @empty {
                <tr><td colspan="6" class="text-slate-500">No scheduled tasks.</td></tr>
              }
            </tbody>
          </table>
        </div>
      </section>
    }

    <!-- General -->
    @if (tab() === 'general') {
      <div class="space-y-6">
        @if (general(); as g) {
          <section class="card space-y-4 p-4 sm:p-5">
            <h2 class="font-semibold text-white">Security</h2>
            <div>
              <label class="label">API key</label>
              <div class="flex gap-2">
                <input class="input font-mono" readonly [value]="g.apiKey" />
                <button class="btn-secondary" (click)="copy(g.apiKey)">Copy</button>
                <button class="btn-secondary" (click)="regenerate()">Regenerate</button>
              </div>
              <p class="hint">Send as <code>X-Api-Key</code> header for automation.</p>
            </div>
          </section>
        }
        @if (status(); as s) {
          <section class="card p-4 sm:p-5">
            <h2 class="mb-3 font-semibold text-white">About</h2>
            <dl class="grid gap-x-8 gap-y-2 text-sm sm:grid-cols-[auto_1fr]">
              <dt class="text-slate-500">Version</dt><dd>{{ s.version }}</dd>
              <dt class="text-slate-500">Runtime</dt><dd>{{ s.runtime }}</dd>
              <dt class="text-slate-500">OS</dt><dd>{{ s.os }} {{ s.isDocker ? '(Docker)' : '' }}</dd>
              <dt class="text-slate-500">Database</dt><dd>{{ s.databaseProvider }}</dd>
              <dt class="text-slate-500">Config</dt><dd class="font-mono">{{ s.configDirectory }}</dd>
              <dt class="text-slate-500">Downloads</dt><dd class="font-mono">{{ s.downloadDirectory }}</dd>
            </dl>
          </section>
        }
      </div>
    }

    <!-- Profile editor -->
    @if (profileForm(); as p) {
      <kr-modal [title]="p.id ? 'Edit profile' : 'New profile'" width="max-w-2xl" (closed)="profileForm.set(null)">
        <div class="space-y-4">
          <div><label class="label">Name</label><input class="input" [(ngModel)]="p.name" /></div>
          <div>
            <label class="label">Allowed qualities</label>
            <div class="grid max-h-64 grid-cols-2 gap-1 overflow-y-auto rounded-lg border border-surface-700 p-2 sm:grid-cols-3">
              @for (q of selectableQualities(); track q.id) {
                <label class="flex items-center gap-2 rounded px-2 py-1 text-sm hover:bg-surface-800">
                  <input type="checkbox" class="accent-accent-500" [checked]="p.allowedQualityIds.includes(q.id)" (change)="toggleQuality(p, q.id)" /> {{ q.name }}
                </label>
              }
            </div>
          </div>
          <div class="grid gap-4 sm:grid-cols-3">
            <div>
              <label class="label">Upgrade until</label>
              <select class="input" [(ngModel)]="p.cutoffQualityId">
                @for (q of qualitiesFor(p); track q.id) { <option [ngValue]="q.id">{{ q.name }}</option> }
              </select>
            </div>
            <div><label class="label">Min size (MB)</label><input class="input" type="number" min="0" [(ngModel)]="p.minSizeMb" /></div>
            <div><label class="label">Max size (MB)</label><input class="input" type="number" min="0" [(ngModel)]="p.maxSizeMb" /></div>
          </div>
          <label class="flex items-center gap-2 text-sm"><input type="checkbox" class="accent-accent-500" [(ngModel)]="p.upgradeAllowed" /> Upgrades allowed</label>
        </div>
        <ng-container footer>
          @if (p.id) { <button class="btn-danger mr-auto" (click)="deleteProfile(p)">Delete</button> }
          <button class="btn-ghost" (click)="profileForm.set(null)">Cancel</button>
          <button class="btn-primary" (click)="saveProfile(p)">Save</button>
        </ng-container>
      </kr-modal>
    }

    <!-- Folder browser -->
    @if (browser(); as b) {
      <kr-modal title="Add root folder" width="max-w-xl" (closed)="browser.set(null)">
        <div class="mb-3 flex gap-2">
          <select class="input w-36" [(ngModel)]="rootMediaType"><option value="Movie">Movies</option><option value="Series">Series</option></select>
          <input class="input font-mono" [(ngModel)]="browsePath" (keyup.enter)="browse(browsePath)" />
        </div>
        <div class="max-h-72 overflow-y-auto rounded-lg border border-surface-700">
          @if (b.parent) {
            <button class="block w-full px-3 py-1.5 text-left font-mono text-sm text-slate-400 hover:bg-surface-800" (click)="browse(b.parent)">..</button>
          }
          @for (d of b.directories; track d.path) {
            <button class="block w-full px-3 py-1.5 text-left font-mono text-sm hover:bg-surface-800" (click)="browse(d.path)">📁 {{ d.name }}</button>
          }
        </div>
        <ng-container footer>
          <button class="btn-ghost" (click)="browser.set(null)">Cancel</button>
          <button class="btn-primary" (click)="addRoot()">Use {{ browsePath }}</button>
        </ng-container>
      </kr-modal>
    }
  `,
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
  protected readonly languages = signal<Language[]>([]);
  protected readonly subtitleTest = signal<{ success: boolean; message: string } | null>(null);
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
    const [subs, langs] = await Promise.all([this.api.settings('subtitles'), this.api.subtitleLanguages()]);
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

  async testSubtitles(settings: SubtitleSettings) {
    this.testingSubtitles.set(true);
    try {
      const results = await this.api.testSubtitleProviders(settings);
      const r = results[0];
      this.subtitleTest.set(r ? { success: r.success, message: r.message } : { success: false, message: 'No providers' });
    } catch (e) {
      this.subtitleTest.set({ success: false, message: errorMessage(e) });
    } finally {
      this.testingSubtitles.set(false);
    }
  }

  protected qualitiesFor(p: QualityProfile) {
    return this.qualities().filter(q => p.allowedQualityIds.includes(q.id)).sort((a, b) => b.weight - a.weight);
  }

  async save<K extends 'media' | 'downloads' | 'automation' | 'metadata' | 'subtitles'>(section: K, value: Parameters<Api['saveSettings']>[1] & object) {
    try {
      await this.api.saveSettings(section, value as never);
      this.toasts.success('Settings saved');
    } catch (e) {
      this.toasts.error(errorMessage(e));
    }
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
