import { Component, DestroyRef, OnInit, computed, effect, inject, input, numberAttribute, signal, untracked } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { DatePipe, NgTemplateOutlet } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { Api, errorMessage } from '../../../core/api';
import { Auth } from '../../../core/auth';
import { Events } from '../../../core/events';
import { AgoPipe, BytesPipe } from '../../../core/format';
import { Episode, EpisodeRelease, HistoryItem, OrganizePreview, QualityProfile, SeasonInfo, Series, SubtitleFile } from '../../../core/models';
import { Toasts } from '../../../core/toast';
import { IndexerSearch } from '../../../core/indexer-search';
import { Languages } from '../../../core/languages';
import { LanguageOverride } from '../../../shared/language-override/language-override';
import { IndexerProgress } from '../../../shared/indexer-progress/indexer-progress';
import { Modal } from '../../../shared/modal/modal';
import { TableSort } from '../../../core/table-sort';
import { SortHeader } from '../../../shared/sort-header/sort-header';
import { Poster } from '../../../shared/poster/poster';
import { LibraryCredits } from '../../../shared/library-credits/library-credits';
import { RelatedTitles } from '../../../shared/related-titles/related-titles';
import { Progress } from '../../../shared/progress/progress';
import { SubtitleManager } from '../../../shared/subtitle-manager/subtitle-manager';
import { SubscribeButton } from '../../../shared/subscribe-button/subscribe-button';
import { ExternalLinks } from '../../../shared/external-links/external-links';

const ICON_SEARCH = 'm21 21-5.197-5.197m0 0A7.5 7.5 0 1 0 5.196 5.196a7.5 7.5 0 0 0 10.607 10.607Z';
const ICON_LIST = 'M8.25 6.75h12M8.25 12h12m-12 5.25h12M3.75 6.75h.007v.008H3.75V6.75Zm.375 0a.375.375 0 1 1-.75 0 .375.375 0 0 1 .75 0ZM3.75 12h.007v.008H3.75V12Zm.375 0a.375.375 0 1 1-.75 0 .375.375 0 0 1 .75 0Zm-.375 5.25h.007v.008H3.75v-.008Zm.375 0a.375.375 0 1 1-.75 0 .375.375 0 0 1 .75 0Z';
const ICON_CC = 'M7.5 8.25h9m-9 3H12m-9.75 1.51c0 1.6 1.123 2.994 2.707 3.227 1.129.166 2.27.293 3.423.379.35.026.67.21.865.501L12 21l2.755-4.133a1.14 1.14 0 0 1 .865-.501 48.172 48.172 0 0 0 3.423-.379c1.584-.233 2.707-1.626 2.707-3.228V6.741c0-1.602-1.123-2.995-2.707-3.228A48.394 48.394 0 0 0 12 3c-2.392 0-4.744.175-7.043.513C3.373 3.746 2.25 5.14 2.25 6.741v6.018Z';
const ICON_BOOKMARK = 'M17.593 3.322c1.1.128 1.907 1.077 1.907 2.185V21L12 17.25 4.5 21V5.507c0-1.108.806-2.057 1.907-2.185a48.507 48.507 0 0 1 11.186 0Z';

interface ReleaseTarget { label: string; season?: number; episodeId?: number; }

@Component({
  selector: 'kr-series-detail',
  imports: [ExternalLinks, SubscribeButton, LibraryCredits, RelatedTitles, LanguageOverride, FormsModule, DatePipe, NgTemplateOutlet, Poster, Progress, Modal, IndexerProgress, BytesPipe, AgoPipe, SubtitleManager, SortHeader],
  templateUrl: './series-detail.html',
  styleUrl: './series-detail.scss',
})
export class SeriesDetailPage implements OnInit {
  readonly id = input.required({ transform: numberAttribute });

  protected readonly auth = inject(Auth);
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
  /** The series' page on Jellyfin, once an episode is downloaded and Jellyfin is set up. */
  protected readonly jellyfinUrl = signal<string | null>(null);
  private jellyfinCheckedFor: number | null = null;
  protected readonly episodes = signal<Episode[]>([]);
  protected readonly history = signal<HistoryItem[]>([]);
  protected readonly profiles = signal<QualityProfile[]>([]);
  protected readonly expanded = signal(new Set<number>());
  protected readonly releaseTarget = signal<ReleaseTarget | null>(null);
  protected readonly releases = signal<EpisodeRelease[]>([]);
  /** The API returns releases best first. */
  protected readonly releaseSort = new TableSort(this.releases, {
    release: r => r.release.title,
    // Season packs first, then by episode label.
    episodes: r => (r.seasonPack ? '0 ' : '1 ') + (r.episodes ?? ''),
    indexer: r => r.release.indexerName,
    size: { value: r => r.release.size, desc: true },
    peers: { value: r => r.release.seeders, desc: true },
    quality: { value: r => r.qualityWeight, desc: true },
    language: r => this.languages.describe(r.languages),
  });
  protected readonly indexerSearch = new IndexerSearch();
  protected readonly searching = signal(false);
  protected readonly grabbing = signal<string | null>(null);
  protected readonly editing = signal(false);
  protected readonly confirmDelete = signal(false);
  protected readonly organizing = signal(false);
  protected readonly organizePreview = signal<OrganizePreview | null>(null);
  protected readonly organizableCount = computed(() => this.organizePreview()?.files.filter(f => !f.problem).length ?? 0);
  protected organizeFolder = true;
  protected editMonitored = true;
  protected editSeasonFolder = true;
  protected editProfileId = 0;
  protected editLanguages: string[] | null = null;
  protected readonly languages = inject(Languages);
  protected deleteFiles = false;
  private expandedInitialised = false;

  protected readonly seasonsDesc = computed(() => [...(this.series()?.seasons ?? [])].sort((a, b) =>
    a.seasonNumber === 0 ? 1 : b.seasonNumber === 0 ? -1 : b.seasonNumber - a.seasonNumber));
  protected readonly profileName = computed(() => this.profiles().find(p => p.id === this.series()?.qualityProfileId)?.name ?? '');

  constructor() {
    // The router reuses this component when navigating between series (/series/1 → /series/2),
    // so (re)load whenever the id changes rather than only once in ngOnInit.
    effect(() => {
      const id = this.id();
      untracked(() => {
        this.reset();
        this.load(id);
      });
    });

    // Live download progress while torrents for this show are active.
    let lastActive = false;
    effect(() => {
      const active = this.events.downloads().some(d => d.seriesId === this.id() && !d.imported);
      if (active || lastActive) untracked(() => this.loadEpisodes());
      lastActive = active;
    });
  }

  ngOnInit() {
    this.api.qualityProfiles().then(p => this.profiles.set(p));
    this.languages.load().catch(() => {});
    this.events.seriesUpdated.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(e => { if (e.id === this.id()) this.load(); });
    this.events.historyAdded.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(h => { if (h.seriesId === this.id()) this.loadHistory(); });
    this.events.subtitlesUpdated.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(e => { if (e.seriesId === this.id()) this.loadSubtitles(); });
  }

  async loadSubtitles() {
    const id = this.id();
    const files = await this.api.subtitleFiles({ seriesId: id });
    if (id === this.id()) this.subtitles.set(files);
  }

  /** Clears everything tied to the previous series so nothing leaks across navigations. */
  private reset() {
    this.series.set(null);
    this.jellyfinUrl.set(null);
    this.jellyfinCheckedFor = null;
    this.episodes.set([]);
    this.history.set([]);
    this.subtitles.set([]);
    this.subtitleEpisode.set(null);
    this.expanded.set(new Set<number>());
    this.expandedInitialised = false;
    this.releaseTarget.set(null);
    this.releases.set([]);
    this.indexerSearch.reset();
    this.searching.set(false);
    this.grabbing.set(null);
    this.editing.set(false);
    this.confirmDelete.set(false);
    this.organizing.set(false);
    this.organizePreview.set(null);
    this.deleteFiles = false;
  }

  private async load(id = this.id()) {
    try {
      const [s] = await Promise.all([this.api.series(id), this.loadEpisodes(), this.loadHistory(), this.loadSubtitles()]);
      if (id !== this.id()) return; // navigated to another series meanwhile
      this.series.set(s);
      this.editMonitored = s.monitored;
      this.editSeasonFolder = s.seasonFolder;
      this.editProfileId = s.qualityProfileId;
      this.editLanguages = s.languages ? [...s.languages] : null;
      this.loadJellyfin(id, s.episodeFileCount > 0);
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

  /** Series id whose episodes are being fetched, so overlapping refreshes for the same series are skipped. */
  private loadingEpisodesFor: number | null = null;
  private async loadEpisodes() {
    const id = this.id();
    if (this.loadingEpisodesFor === id) return;
    this.loadingEpisodesFor = id;
    try {
      const episodes = await this.api.episodes(id);
      if (id === this.id()) this.episodes.set(episodes);
    } finally {
      if (this.loadingEpisodesFor === id) this.loadingEpisodesFor = null;
    }
  }

  /** Looks the title up on Jellyfin once per page, as soon as something is downloaded. Failures just hide the button. */
  private async loadJellyfin(id: number, hasFile: boolean) {
    if (!hasFile || this.jellyfinCheckedFor === id) return;
    this.jellyfinCheckedFor = id;
    try {
      const link = await this.api.seriesJellyfinLink(id);
      if (id === this.id()) this.jellyfinUrl.set(link?.url ?? null);
    } catch {
      // Not configured, unreachable or unknown to Jellyfin: no button.
    }
  }

  private async loadHistory() {
    if (!this.auth.isStaff()) return;
    const id = this.id();
    const page = await this.api.history(undefined, 1, 50, id);
    if (id === this.id()) this.history.set(page.items);
  }

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
    await this.api.updateSeries(s.id, !s.monitored, s.qualityProfileId, s.seasonFolder, s.languages ?? null);
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

  async searchSubtitles() {
    await this.api.searchSubtitles({ seriesId: this.id() });
    this.toasts.show('Searching subtitles for every episode file…');
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

  async openOrganize() {
    this.organizePreview.set(null);
    this.organizeFolder = true;
    this.organizing.set(true);
    const id = this.id();
    try {
      const preview = await this.api.organizePreview(id);
      if (id === this.id()) this.organizePreview.set(preview);
    } catch (e) {
      this.organizing.set(false);
      this.toasts.error(errorMessage(e));
    }
  }

  protected canOrganize() {
    const p = this.organizePreview();
    return !!p && (this.organizableCount() > 0 || (!!p.newPath && this.organizeFolder));
  }

  async organize() {
    const p = this.organizePreview();
    try {
      await this.api.organizeSeries(this.id(), !!p?.newPath && this.organizeFolder);
      this.organizing.set(false);
      this.toasts.show('Organizing files…');
    } catch (e) {
      this.toasts.error(errorMessage(e));
    }
  }

  async interactive(target: ReleaseTarget) {
    const id = this.id();
    this.releaseTarget.set(target);
    this.releases.set([]);
    this.searching.set(true);
    const searchId = this.indexerSearch.start();
    try {
      const res = await this.api.seriesReleases(id, target.season, target.episodeId, searchId);
      if (id !== this.id()) return;
      this.releases.set(res.releases);
      this.indexerSearch.finish(res.indexers);
    } catch (e) {
      this.indexerSearch.finish();
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
      await this.api.updateSeries(this.id(), this.editMonitored, this.editProfileId, this.editSeasonFolder, this.editLanguages);
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
