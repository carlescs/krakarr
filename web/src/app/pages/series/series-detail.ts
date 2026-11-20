import { Component, DestroyRef, OnInit, computed, effect, inject, input, numberAttribute, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { Api, errorMessage } from '../../core/api';
import { Events } from '../../core/events';
import { AgoPipe, BytesPipe } from '../../core/format';
import { Episode, EpisodeRelease, HistoryItem, IndexerError, QualityProfile, SeasonInfo, Series, SubtitleFile } from '../../core/models';
import { Toasts } from '../../core/toast';
import { Modal } from '../../shared/modal';
import { Poster } from '../../shared/poster';
import { RelatedTitles } from '../../shared/related-titles';
import { Progress } from '../../shared/progress';
import { SubtitleManager } from '../../shared/subtitle-manager';

const ICON_SEARCH = 'm21 21-5.197-5.197m0 0A7.5 7.5 0 1 0 5.196 5.196a7.5 7.5 0 0 0 10.607 10.607Z';
const ICON_LIST = 'M8.25 6.75h12M8.25 12h12m-12 5.25h12M3.75 6.75h.007v.008H3.75V6.75Zm.375 0a.375.375 0 1 1-.75 0 .375.375 0 0 1 .75 0ZM3.75 12h.007v.008H3.75V12Zm.375 0a.375.375 0 1 1-.75 0 .375.375 0 0 1 .75 0Zm-.375 5.25h.007v.008H3.75v-.008Zm.375 0a.375.375 0 1 1-.75 0 .375.375 0 0 1 .75 0Z';
const ICON_CC = 'M7.5 8.25h9m-9 3H12m-9.75 1.51c0 1.6 1.123 2.994 2.707 3.227 1.129.166 2.27.293 3.423.379.35.026.67.21.865.501L12 21l2.755-4.133a1.14 1.14 0 0 1 .865-.501 48.172 48.172 0 0 0 3.423-.379c1.584-.233 2.707-1.626 2.707-3.228V6.741c0-1.602-1.123-2.995-2.707-3.228A48.394 48.394 0 0 0 12 3c-2.392 0-4.744.175-7.043.513C3.373 3.746 2.25 5.14 2.25 6.741v6.018Z';
const ICON_BOOKMARK = 'M17.593 3.322c1.1.128 1.907 1.077 1.907 2.185V21L12 17.25 4.5 21V5.507c0-1.108.806-2.057 1.907-2.185a48.507 48.507 0 0 1 11.186 0Z';

interface ReleaseTarget { label: string; season?: number; episodeId?: number; }

@Component({
  selector: 'kr-series-detail',
  imports: [RelatedTitles, FormsModule, DatePipe, Poster, Progress, Modal, BytesPipe, AgoPipe, SubtitleManager],
  template: `
    @if (series(); as s) {
      <!-- Hero -->
      <div class="relative -mx-4 -mt-6 mb-6 overflow-hidden lg:-mx-8">
        @if (s.fanartUrl) { <img [src]="s.fanartUrl" alt="" class="absolute inset-0 h-full w-full object-cover opacity-20" /> }
        <div class="absolute inset-0 bg-gradient-to-t from-surface-950 via-surface-950/80 to-surface-950/40"></div>
        <div class="relative flex flex-col gap-6 px-4 py-8 sm:flex-row lg:px-8">
          <kr-poster class="w-40 shrink-0 shadow-2xl sm:w-48" [url]="s.posterUrl" [title]="s.title" />
          <div class="min-w-0 flex-1">
            <h1 class="text-3xl font-bold tracking-tight text-white">{{ s.title }} @if (s.year) { <span class="font-normal text-slate-400">({{ s.year }})</span> }</h1>
            <div class="mt-2 flex flex-wrap items-center gap-2 text-sm text-slate-400">
              @if (s.network) { <span>{{ s.network }}</span><span>·</span> }
              @if (s.runtime) { <span>{{ s.runtime }} min</span><span>·</span> }
              <span>{{ s.status }}</span>
              @if (s.nextAiring) { <span>·</span><span>Next: {{ s.nextAiring | date: 'mediumDate' }}</span> }
              @if (s.tmdbId) { <span>·</span><a class="hover:text-white" [href]="'https://www.themoviedb.org/tv/' + s.tmdbId" target="_blank" rel="noopener">TMDB</a> }
              @if (s.tvdbId) { <span>·</span><a class="hover:text-white" [href]="'https://thetvdb.com/dereferrer/series/' + s.tvdbId" target="_blank" rel="noopener">TVDB</a> }
              @if (s.imdbId) { <span>·</span><a class="hover:text-white" [href]="'https://www.imdb.com/title/' + s.imdbId" target="_blank" rel="noopener">IMDb</a> }
            </div>
            <div class="mt-3 flex flex-wrap gap-2">
              <span class="badge" [class]="s.episodeFileCount >= s.episodeCount ? 'bg-emerald-500/15 text-emerald-300' : 'bg-rose-500/15 text-rose-300'">
                {{ s.episodeFileCount }}/{{ s.episodeCount }} episodes
              </span>
              <span class="badge" [class]="s.monitored ? 'bg-accent-500/15 text-accent-300' : 'bg-slate-500/15 text-slate-400'">{{ s.monitored ? 'Monitored' : 'Unmonitored' }}</span>
              <span class="badge bg-surface-700 text-slate-300">{{ profileName() }}</span>
              @if (s.sizeOnDisk) { <span class="badge bg-surface-700 text-slate-300">{{ s.sizeOnDisk | bytes }}</span> }
            </div>
            <p class="mt-4 line-clamp-4 max-w-3xl text-sm leading-relaxed text-slate-300">{{ s.overview }}</p>
            <p class="mt-3 font-mono text-xs text-slate-500">{{ s.path }}</p>
            <div class="mt-5 flex flex-wrap gap-2">
              <button class="btn-primary" (click)="searchMissing()">Search missing</button>
              <button class="btn-secondary" (click)="refresh()">Refresh</button>
              <button class="btn-secondary" (click)="toggleSeries()">{{ s.monitored ? 'Unmonitor' : 'Monitor' }}</button>
              <button class="btn-ghost" (click)="editing.set(true)">Edit</button>
              <button class="btn-ghost text-rose-300" (click)="confirmDelete.set(true)">Delete</button>
            </div>
          </div>
        </div>
      </div>

      <!-- Seasons -->
      <div class="space-y-3">
        @for (season of seasonsDesc(); track season.seasonNumber) {
          <section class="card overflow-hidden">
            <div class="flex flex-wrap items-center gap-3 px-4 py-3">
              <button class="text-slate-400 hover:text-white" (click)="toggleMonitorSeason(season)" [title]="season.monitored ? 'Unmonitor season' : 'Monitor season'">
                <svg class="size-5" viewBox="0 0 24 24" stroke-width="1.6" stroke="currentColor" [attr.fill]="season.monitored ? 'currentColor' : 'none'"><path stroke-linecap="round" stroke-linejoin="round" [attr.d]="icons.bookmark" /></svg>
              </button>
              <button class="flex flex-1 items-center gap-3 text-left" (click)="toggleExpanded(season.seasonNumber)">
                <h2 class="whitespace-nowrap font-semibold text-white">{{ season.seasonNumber === 0 ? 'Specials' : 'Season ' + season.seasonNumber }}</h2>
                <span class="badge" [class]="seasonBadge(season)">{{ season.episodeFileCount }}/{{ season.episodeCount }}</span>
                @if (season.sizeOnDisk) { <span class="text-xs text-slate-500">{{ season.sizeOnDisk | bytes }}</span> }
                <span class="ml-auto text-slate-500">{{ expanded().has(season.seasonNumber) ? '▾' : '▸' }}</span>
              </button>
              <button class="btn-ghost btn-sm" title="Search season" (click)="searchSeason(season.seasonNumber)">
                <svg class="size-4" fill="none" viewBox="0 0 24 24" stroke-width="2" stroke="currentColor"><path stroke-linecap="round" [attr.d]="icons.search" /></svg>
              </button>
              <button class="btn-ghost btn-sm" title="Interactive search" (click)="interactive({ label: seasonLabel(season.seasonNumber), season: season.seasonNumber })">
                <svg class="size-4" fill="none" viewBox="0 0 24 24" stroke-width="1.8" stroke="currentColor"><path stroke-linecap="round" [attr.d]="icons.list" /></svg>
              </button>
            </div>
            @if (expanded().has(season.seasonNumber)) {
              <div class="overflow-x-auto border-t border-surface-700">
                <table class="table">
                  <thead><tr><th class="w-10"></th><th class="w-12">#</th><th>Title</th><th class="w-32">Air date</th><th class="w-44">Status</th><th class="w-24"></th></tr></thead>
                  <tbody>
                    @for (e of episodesFor(season.seasonNumber); track e.id) {
                      <tr [class.opacity-60]="!e.monitored">
                        <td>
                          <button class="text-slate-400 hover:text-white" (click)="toggleMonitorEpisode(e)" [title]="e.monitored ? 'Unmonitor' : 'Monitor'">
                            <svg class="size-4" viewBox="0 0 24 24" stroke-width="1.6" stroke="currentColor" [attr.fill]="e.monitored ? 'currentColor' : 'none'"><path stroke-linecap="round" stroke-linejoin="round" [attr.d]="icons.bookmark" /></svg>
                          </button>
                        </td>
                        <td class="text-slate-400">{{ e.episodeNumber }}</td>
                        <td class="w-full max-w-0">
                          <div class="truncate" [title]="e.overview ?? ''">{{ e.title || 'TBA' }}</div>
                          @if (e.episodeFile; as f) {
                            <div class="flex items-center gap-1.5">
                              @for (s of subtitlesByFile().get(f.id) ?? []; track s.id) {
                                <span class="badge bg-surface-700 px-1.5 text-[10px] uppercase text-slate-300" [title]="s.languageName + (s.hearingImpaired ? ' (HI)' : '')">{{ s.language }}</span>
                              }
                              <span class="truncate font-mono text-xs text-slate-500" [title]="f.relativePath">{{ f.relativePath }}</span>
                            </div>
                          }
                        </td>
                        <td class="whitespace-nowrap text-slate-400">{{ e.airDate ? (e.airDate | date: 'mediumDate') : 'TBA' }}</td>
                        <td>
                          @if (e.downloadProgress != null) {
                            <div class="flex items-center gap-2"><kr-progress class="flex-1" [value]="e.downloadProgress" /><span class="text-xs text-accent-300">{{ e.downloadProgress.toFixed(0) }}%</span></div>
                          } @else if (e.episodeFile; as f) {
                            <span class="badge bg-emerald-500/15 text-emerald-300" [title]="(f.size | bytes)">{{ f.quality }}</span>
                          } @else if (!e.hasAired) {
                            <span class="badge bg-slate-500/15 text-slate-400">Unaired</span>
                          } @else if (e.monitored) {
                            <span class="badge bg-rose-500/15 text-rose-300">Missing</span>
                          } @else {
                            <span class="badge bg-slate-500/15 text-slate-400">Unmonitored</span>
                          }
                        </td>
                        <td class="whitespace-nowrap text-right">
                          @if (e.episodeFile) {
                            <button class="btn-ghost btn-sm" title="Subtitles" (click)="subtitleEpisode.set(e)">
                              <svg class="size-4" fill="none" viewBox="0 0 24 24" stroke-width="1.8" stroke="currentColor"><path stroke-linecap="round" stroke-linejoin="round" [attr.d]="icons.cc" /></svg>
                            </button>
                          }
                          <button class="btn-ghost btn-sm" title="Automatic search" (click)="searchEpisode(e)">
                            <svg class="size-4" fill="none" viewBox="0 0 24 24" stroke-width="2" stroke="currentColor"><path stroke-linecap="round" [attr.d]="icons.search" /></svg>
                          </button>
                          <button class="btn-ghost btn-sm" title="Interactive search" (click)="interactive({ label: code(e), episodeId: e.id })">
                            <svg class="size-4" fill="none" viewBox="0 0 24 24" stroke-width="1.8" stroke="currentColor"><path stroke-linecap="round" [attr.d]="icons.list" /></svg>
                          </button>
                        </td>
                      </tr>
                    }
                  </tbody>
                </table>
              </div>
            }
          </section>
        }
      </div>

      <!-- History -->
      <section class="card mt-6 p-5">
        <h2 class="mb-3 text-sm font-semibold uppercase tracking-wide text-slate-400">History</h2>
        @if (history().length === 0) {
          <p class="text-sm text-slate-500">Nothing yet.</p>
        } @else {
          <ul class="divide-y divide-surface-800 text-sm">
            @for (h of history(); track h.id) {
              <li class="flex flex-wrap items-center gap-3 py-2">
                <span class="badge w-24 justify-center"
                      [class]="h.eventType === 'Imported' ? 'bg-emerald-500/15 text-emerald-300' : h.eventType === 'Grabbed' ? 'bg-accent-500/15 text-accent-300' : h.eventType === 'SubtitleDownloaded' ? 'bg-violet-500/15 text-violet-300' : 'bg-rose-500/15 text-rose-300'">
                  {{ h.eventType === 'DownloadFailed' ? 'Failed' : h.eventType === 'FileDeleted' ? 'Deleted' : h.eventType === 'SubtitleDownloaded' ? 'Subtitle' : h.eventType }}
                </span>
                <span class="min-w-0 flex-1 truncate">{{ h.sourceTitle }}</span>
                <span class="text-xs text-slate-500">{{ h.quality }}</span>
                <span class="w-24 text-right text-xs text-slate-500" [title]="h.date">{{ h.date | ago }}</span>
                @if (h.message) { <span class="w-full pl-27 text-xs text-slate-500">{{ h.message }}</span> }
              </li>
            }
          </ul>
        }
      </section>

      <!-- Recommendations / similar (TMDB) -->
      <kr-related-titles kind="series" [id]="s.id" />

      <!-- Interactive search -->
      @if (releaseTarget(); as target) {
        <kr-modal [title]="'Interactive search · ' + target.label" width="max-w-6xl" (closed)="releaseTarget.set(null)">
          @if (searching()) {
            <p class="py-10 text-center text-slate-400">Searching indexers…</p>
          } @else {
            @for (e of searchErrors(); track e.indexerId) {
              <p class="mb-2 rounded bg-amber-500/10 px-3 py-2 text-xs text-amber-300">{{ e.indexerName }}: {{ e.message }}</p>
            }
            @if (releases().length === 0) {
              <p class="py-10 text-center text-slate-400">No results.</p>
            } @else {
              <div class="max-h-[65vh] overflow-auto">
                <table class="table">
                  <thead><tr><th>Release</th><th>Episodes</th><th>Indexer</th><th>Size</th><th>Peers</th><th>Quality</th><th></th></tr></thead>
                  <tbody>
                    @for (r of releases(); track r.release.guid) {
                      <tr [class.opacity-60]="!r.approved">
                        <td class="w-full max-w-0">
                          <div class="truncate" [title]="r.release.title">{{ r.release.title }}</div>
                          @if (!r.approved) { <div class="truncate text-xs text-rose-300" [title]="r.rejections.join(' · ')">{{ r.rejections.join(' · ') }}</div> }
                        </td>
                        <td class="whitespace-nowrap text-xs">
                          {{ r.episodes ?? '—' }} @if (r.seasonPack) { <span class="badge ml-1 bg-violet-500/15 text-violet-300">Pack</span> }
                        </td>
                        <td class="text-slate-400">{{ r.release.indexerName }}</td>
                        <td class="whitespace-nowrap">{{ r.release.size | bytes }}</td>
                        <td class="whitespace-nowrap"><span class="text-emerald-400">{{ r.release.seeders ?? '?' }}</span> / {{ r.release.peers ?? '?' }}</td>
                        <td><span class="badge bg-surface-700 text-slate-200">{{ r.quality }}</span></td>
                        <td class="text-right">
                          <button class="btn-sm" [class]="r.approved ? 'btn-primary' : 'btn-secondary'" (click)="grab(r)" [disabled]="!r.episodes || grabbing() === r.release.guid">
                            {{ grabbing() === r.release.guid ? '…' : 'Grab' }}
                          </button>
                        </td>
                      </tr>
                    }
                  </tbody>
                </table>
              </div>
            }
          }
        </kr-modal>
      }

      @if (subtitleEpisode(); as e) {
        <kr-subtitle-manager [fileRef]="{ episodeFileId: e.episodeFile!.id }" [label]="code(e) + (e.title ? ' · ' + e.title : '')"
                             [files]="subtitlesByFile().get(e.episodeFile!.id) ?? []"
                             (closed)="subtitleEpisode.set(null)" (changed)="loadSubtitles()" />
      }

      @if (editing()) {
        <kr-modal [title]="'Edit ' + s.title" (closed)="editing.set(false)">
          <div class="space-y-4">
            <label class="flex items-center gap-2 text-sm"><input type="checkbox" class="accent-accent-500" [(ngModel)]="editMonitored" /> Monitored</label>
            <label class="flex items-center gap-2 text-sm"><input type="checkbox" class="accent-accent-500" [(ngModel)]="editSeasonFolder" /> Use season folders</label>
            <div>
              <label class="label">Quality profile</label>
              <select class="input" [(ngModel)]="editProfileId">
                @for (p of profiles(); track p.id) { <option [ngValue]="p.id">{{ p.name }}</option> }
              </select>
            </div>
          </div>
          <ng-container footer>
            <button class="btn-ghost" (click)="editing.set(false)">Cancel</button>
            <button class="btn-primary" (click)="saveEdit()">Save</button>
          </ng-container>
        </kr-modal>
      }

      @if (confirmDelete()) {
        <kr-modal [title]="'Delete ' + s.title" (closed)="confirmDelete.set(false)">
          <p class="text-sm text-slate-300">Remove this series from the library?</p>
          <label class="mt-4 flex items-center gap-2 text-sm"><input type="checkbox" class="accent-rose-500" [(ngModel)]="deleteFiles" /> Also delete the series folder from disk</label>
          <ng-container footer>
            <button class="btn-ghost" (click)="confirmDelete.set(false)">Cancel</button>
            <button class="btn-danger" (click)="delete()">Delete</button>
          </ng-container>
        </kr-modal>
      }
    } @else {
      <p class="text-slate-500">Loading…</p>
    }
  `,
})
export class SeriesDetailPage implements OnInit {
  readonly id = input.required({ transform: numberAttribute });

  private readonly api = inject(Api);
  private readonly toasts = inject(Toasts);
  private readonly events = inject(Events);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly icons = { search: ICON_SEARCH, list: ICON_LIST, bookmark: ICON_BOOKMARK, cc: ICON_CC };
  protected readonly subtitles = signal<SubtitleFile[]>([]);
  protected readonly subtitleEpisode = signal<Episode | null>(null);
  protected readonly subtitlesByFile = computed(() => {
    const map = new Map<number, SubtitleFile[]>();
    for (const s of this.subtitles()) if (s.episodeFileId) map.set(s.episodeFileId, [...(map.get(s.episodeFileId) ?? []), s]);
    return map;
  });
  protected readonly series = signal<Series | null>(null);
  protected readonly episodes = signal<Episode[]>([]);
  protected readonly history = signal<HistoryItem[]>([]);
  protected readonly profiles = signal<QualityProfile[]>([]);
  protected readonly expanded = signal(new Set<number>());
  protected readonly releaseTarget = signal<ReleaseTarget | null>(null);
  protected readonly releases = signal<EpisodeRelease[]>([]);
  protected readonly searchErrors = signal<IndexerError[]>([]);
  protected readonly searching = signal(false);
  protected readonly grabbing = signal<string | null>(null);
  protected readonly editing = signal(false);
  protected readonly confirmDelete = signal(false);
  protected editMonitored = true;
  protected editSeasonFolder = true;
  protected editProfileId = 0;
  protected deleteFiles = false;
  private expandedInitialised = false;

  protected readonly seasonsDesc = computed(() => [...(this.series()?.seasons ?? [])].sort((a, b) =>
    a.seasonNumber === 0 ? 1 : b.seasonNumber === 0 ? -1 : b.seasonNumber - a.seasonNumber));
  protected readonly profileName = computed(() => this.profiles().find(p => p.id === this.series()?.qualityProfileId)?.name ?? '');

  constructor() {
    // Live download progress while torrents for this show are active.
    let lastActive = false;
    effect(() => {
      const active = this.events.downloads().some(d => d.seriesId === this.id() && !d.imported);
      if (active || lastActive) this.loadEpisodes();
      lastActive = active;
    });
  }

  ngOnInit() {
    this.load();
    this.api.qualityProfiles().then(p => this.profiles.set(p));
    this.events.seriesUpdated.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(e => { if (e.id === this.id()) this.load(); });
    this.events.historyAdded.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(h => { if (h.seriesId === this.id()) this.loadHistory(); });
    this.events.subtitlesUpdated.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(e => { if (e.seriesId === this.id()) this.loadSubtitles(); });
  }

  async loadSubtitles() { this.subtitles.set(await this.api.subtitleFiles({ seriesId: this.id() })); }

  private async load() {
    try {
      const [s] = await Promise.all([this.api.series(this.id()), this.loadEpisodes(), this.loadHistory(), this.loadSubtitles()]);
      this.series.set(s);
      this.editMonitored = s.monitored;
      this.editSeasonFolder = s.seasonFolder;
      this.editProfileId = s.qualityProfileId;
      if (!this.expandedInitialised) {
        // Open the latest regular season by default.
        const latest = Math.max(0, ...s.seasons.map(x => x.seasonNumber));
        this.expanded.set(new Set([latest]));
        this.expandedInitialised = true;
      }
    } catch (e) {
      this.toasts.error(errorMessage(e));
    }
  }

  private loadingEpisodes = false;
  private async loadEpisodes() {
    if (this.loadingEpisodes) return;
    this.loadingEpisodes = true;
    try { this.episodes.set(await this.api.episodes(this.id())); }
    finally { this.loadingEpisodes = false; }
  }

  private async loadHistory() { this.history.set((await this.api.history(undefined, 1, 50, this.id())).items); }

  protected episodesFor(season: number) {
    return this.episodes().filter(e => e.seasonNumber === season).sort((a, b) => b.episodeNumber - a.episodeNumber);
  }

  protected code(e: Episode) { return `S${String(e.seasonNumber).padStart(2, '0')}E${String(e.episodeNumber).padStart(2, '0')}`; }
  protected seasonLabel(n: number) { return n === 0 ? 'Specials' : `Season ${n}`; }
  protected seasonBadge(s: SeasonInfo) {
    if (!s.monitored) return 'bg-slate-500/15 text-slate-400';
    return s.episodeFileCount >= s.episodeCount ? 'bg-emerald-500/15 text-emerald-300' : 'bg-rose-500/15 text-rose-300';
  }

  toggleExpanded(n: number) {
    this.expanded.update(set => {
      const next = new Set(set);
      if (next.has(n)) next.delete(n); else next.add(n);
      return next;
    });
  }

  async toggleSeries() {
    const s = this.series()!;
    await this.api.updateSeries(s.id, !s.monitored, s.qualityProfileId, s.seasonFolder);
    await this.load();
  }

  async toggleMonitorSeason(season: SeasonInfo) {
    await this.api.monitorSeason(this.id(), season.seasonNumber, !season.monitored);
    await this.load();
  }

  async toggleMonitorEpisode(e: Episode) {
    await this.api.monitorEpisode(e.id, !e.monitored);
    this.episodes.update(list => list.map(x => (x.id === e.id ? { ...x, monitored: !e.monitored } : x)));
  }

  async searchMissing() {
    await this.api.searchSeries(this.id());
    this.toasts.show('Searching for missing episodes…');
  }

  async searchSeason(n: number) {
    await this.api.searchSeries(this.id(), n);
    this.toasts.show(`Searching ${this.seasonLabel(n)}…`);
  }

  async searchEpisode(e: Episode) {
    await this.api.searchSeries(this.id(), undefined, [e.id]);
    this.toasts.show(`Searching ${this.code(e)}…`);
  }

  async refresh() {
    await this.api.refreshSeries(this.id());
    this.toasts.show('Refreshing metadata…');
  }

  async interactive(target: ReleaseTarget) {
    this.releaseTarget.set(target);
    this.releases.set([]);
    this.searching.set(true);
    try {
      const res = await this.api.seriesReleases(this.id(), target.season, target.episodeId);
      this.releases.set(res.releases);
      this.searchErrors.set(res.errors);
    } catch (e) {
      this.toasts.error(errorMessage(e));
    } finally {
      this.searching.set(false);
    }
  }

  async grab(r: EpisodeRelease) {
    this.grabbing.set(r.release.guid);
    try {
      await this.api.grabSeriesRelease(this.id(), r.release);
      this.toasts.success(`Grabbed ${r.episodes}`);
      this.releaseTarget.set(null);
    } catch (e) {
      this.toasts.error(errorMessage(e));
    } finally {
      this.grabbing.set(null);
    }
  }

  async saveEdit() {
    try {
      await this.api.updateSeries(this.id(), this.editMonitored, this.editProfileId, this.editSeasonFolder);
      this.editing.set(false);
      await this.load();
    } catch (e) {
      this.toasts.error(errorMessage(e));
    }
  }

  async delete() {
    try {
      await this.api.deleteSeries(this.id(), this.deleteFiles);
      this.toasts.success('Series removed');
      this.router.navigateByUrl('/series');
    } catch (e) {
      this.toasts.error(errorMessage(e));
    }
  }
}
