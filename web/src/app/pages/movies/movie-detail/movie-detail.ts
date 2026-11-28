import { Component, DestroyRef, OnInit, computed, effect, inject, input, numberAttribute, signal, untracked, viewChild } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { Api, errorMessage } from '../../../core/api';
import { Auth } from '../../../core/auth';
import { Events } from '../../../core/events';
import { AgoPipe, BytesPipe, scoreClass } from '../../../core/format';
import { AVAILABILITY_OPTIONS } from '../../../core/calendar';
import { HistoryItem, MinimumAvailability, Movie, PlayState, QualityProfile, ReleaseDecision, SubtitleFile } from '../../../core/models';
import { Toasts } from '../../../core/toast';
import { ReleaseSearch } from '../../../core/indexer-search';
import { Languages } from '../../../core/languages';
import { JellyfinLink } from '../../../core/jellyfin-link';
import { LanguageOverride } from '../../../shared/language-override/language-override';
import { AlternateTitles } from '../../../shared/alternate-titles/alternate-titles';
import { IndexerProgress } from '../../../shared/indexer-progress/indexer-progress';
import { Modal } from '../../../shared/modal/modal';
import { TableSort } from '../../../core/table-sort';
import { SortHeader } from '../../../shared/sort-header/sort-header';
import { Poster } from '../../../shared/poster/poster';
import { Overview } from '../../../shared/overview/overview';
import { LibraryCredits } from '../../../shared/library-credits/library-credits';
import { RelatedTitles } from '../../../shared/related-titles/related-titles';
import { SubtitleManager } from '../../../shared/subtitle-manager/subtitle-manager';
import { SubtitleList } from '../../../shared/subtitle-list/subtitle-list';
import { MediaInfoView } from '../../../shared/media-info/media-info';
import { MoreMenu } from '../../../shared/more-menu/more-menu';
import { SubscribeButton } from '../../../shared/subscribe-button/subscribe-button';
import { ExternalLinks } from '../../../shared/external-links/external-links';
import { OmdbFacts } from '../../../shared/omdb-facts/omdb-facts';
import { OmdbState } from '../../../core/omdb';
import { MovieReleases } from '../../../shared/movie-releases/movie-releases';
import { MediaDownloads } from '../../../shared/media-downloads/media-downloads';
import { DeleteFilesDialog } from '../../../shared/delete-files/delete-files';
import { PlayStatus } from '../../../shared/play-status/play-status';

@Component({
  selector: 'kr-movie-detail',
  imports: [DeleteFilesDialog, PlayStatus, MoreMenu, MediaDownloads, ExternalLinks, OmdbFacts, SubscribeButton, LibraryCredits, RelatedTitles, LanguageOverride, AlternateTitles, MovieReleases, FormsModule, DatePipe, Poster, Overview, Modal, IndexerProgress, SubtitleManager, SubtitleList, MediaInfoView, SortHeader, BytesPipe, AgoPipe],
  templateUrl: './movie-detail.html',
  styleUrl: './movie-detail.scss',
})
export class MovieDetailPage implements OnInit {
  readonly id = input.required({ transform: numberAttribute });

  protected readonly auth = inject(Auth);
  protected readonly icons = { search: 'm21 21-5.197-5.197m0 0A7.5 7.5 0 1 0 5.196 5.196a7.5 7.5 0 0 0 10.607 10.607Z' };
  private readonly api = inject(Api);
  private readonly toasts = inject(Toasts);
  private readonly events = inject(Events);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly movie = signal<Movie | null>(null);
  /** Stored OMDb data, or what the external links fetched when the movie has none yet. */
  protected readonly omdb = new OmdbState(() => this.movie());
  protected readonly history = signal<HistoryItem[]>([]);
  protected readonly profiles = signal<QualityProfile[]>([]);
  protected readonly releaseSearch = new ReleaseSearch<ReleaseDecision>();
  /** Best first, re-ranked as indexers answer. */
  protected readonly releases = this.releaseSearch.results;
  protected readonly scoreClass = scoreClass;
  protected readonly releaseSort = new TableSort(this.releaseSearch.filtered, {
    release: r => r.release.title,
    indexer: r => r.release.indexerName,
    size: { value: r => r.release.size, desc: true },
    peers: { value: r => r.release.seeders, desc: true },
    quality: { value: r => r.qualityWeight, desc: true },
    language: r => this.languages.describe(r.languages),
    score: r => r.customFormatScore ?? 0,
  });
  protected readonly releasesOpen = signal(false);
  protected readonly searching = this.releaseSearch.running;
  protected readonly grabbing = signal<string | null>(null);
  protected readonly editing = signal(false);
  private readonly alternateTitles = viewChild(AlternateTitles);
  protected readonly confirmDelete = signal(false);
  protected readonly confirmDeleteFile = signal(false);
  protected readonly subtitles = signal<SubtitleFile[]>([]);
  protected readonly subtitlesOpen = signal(false);
  /** Language the subtitle dialog searches first (a missing one that was clicked); null = every missing language. */
  protected readonly subtitleLanguage = signal<string | null>(null);
  /** The movie's page on Jellyfin, once it's downloaded and Jellyfin has scanned it. */
  private readonly jellyfin = new JellyfinLink();
  protected readonly jellyfinUrl = this.jellyfin.url;
  /** Watched state on each media server set up to report it (empty until downloaded, or when none is). */
  protected readonly playStatus = signal<PlayState[]>([]);
  /** The file the play status was read for, so reloads of the same movie don't ask again. */
  private playStatusKey: string | null = null;
  protected editMonitored = true;
  protected editProfileId = 0;
  protected editAvailability: MinimumAvailability = 'Released';
  protected editLanguages: string[] | null = null;
  protected readonly languages = inject(Languages);
  protected readonly availabilityOptions = AVAILABILITY_OPTIONS;
  protected deleteFiles = false;

  protected readonly downloads = computed(() => this.events.downloads().filter(d => d.movieId === this.id()));
  protected readonly profileName = computed(() => this.profiles().find(p => p.id === this.movie()?.qualityProfileId)?.name ?? '');

  constructor() {
    this.destroyRef.onDestroy(() => this.jellyfin.reset());
    // The router reuses this component when navigating between movies (/movies/1 → /movies/2),
    // so (re)load whenever the id changes rather than only once in ngOnInit.
    effect(() => {
      const id = this.id();
      untracked(() => {
        this.reset();
        this.load(id);
      });
    });
  }

  ngOnInit() {
    this.languages.load().catch(() => {});
    this.api.qualityProfiles().then(p => this.profiles.set(p));
    this.events.movieUpdated.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(e => { if (e.id === this.id()) this.load(); });
    this.events.historyAdded.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(h => { if (h.movieId === this.id()) this.loadHistory(); });
    this.events.subtitlesUpdated.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(e => { if (e.movieId === this.id()) this.loadSubtitles(); });
  }

  protected availabilityLabel(value: MinimumAvailability) { return AVAILABILITY_OPTIONS.find(o => o.value === value)?.label ?? value; }

  async loadSubtitles() {
    const id = this.id();
    const files = await this.api.subtitleFiles({ movieId: id });
    if (id === this.id()) this.subtitles.set(files);
  }

  /** Clears everything tied to the previous movie so nothing leaks across navigations. */
  private reset() {
    this.movie.set(null);
    this.history.set([]);
    this.subtitles.set([]);
    this.releaseSearch.reset();
    this.releasesOpen.set(false);
    this.grabbing.set(null);
    this.editing.set(false);
    this.confirmDelete.set(false);
    this.confirmDeleteFile.set(false);
    this.subtitlesOpen.set(false);
    this.jellyfin.reset();
    this.playStatus.set([]);
    this.playStatusKey = null;
    this.deleteFiles = false;
  }

  private async load(id = this.id()) {
    try {
      const m = await this.api.movie(id);
      if (id !== this.id()) return; // navigated to another movie meanwhile
      this.movie.set(m);
      this.editMonitored = m.monitored;
      this.editProfileId = m.qualityProfileId;
      this.editAvailability = m.minimumAvailability;
      this.editLanguages = m.languages ? [...m.languages] : null;
      this.jellyfin.watch(m.movieFile ? `${id}:${m.movieFile.id}` : null, () => this.api.movieJellyfinLink(id));
      this.loadPlayStatus(id, m.movieFile ? `${id}:${m.movieFile.id}` : null);
      await Promise.all([this.loadHistory(), this.loadSubtitles()]);
    } catch (e) {
      this.toasts.error(errorMessage(e));
    }
  }

  private async loadPlayStatus(id: number, key: string | null) {
    if (key === this.playStatusKey) return;
    this.playStatusKey = key;
    this.playStatus.set([]);
    if (key == null) return;
    try {
      const states = await this.api.moviePlayStatus(id);
      if (key === this.playStatusKey) this.playStatus.set(states ?? []);
    } catch {
      // A convenience: an unreachable media server just shows nothing.
    }
  }

  private async loadHistory() {
    if (!this.auth.isStaff()) return;
    const id = this.id();
    const page = await this.api.history(id, 1, 50);
    if (id === this.id()) this.history.set(page.items);
  }

  async autoSearch() {
    await this.api.searchMovie(this.id());
    this.toasts.show('Search started — results will appear in History');
  }

  protected openSubtitles(language: string | null) {
    this.subtitleLanguage.set(language);
    this.subtitlesOpen.set(true);
  }

  async searchSubtitles() {
    await this.api.searchSubtitles({ movieId: this.id() });
    this.toasts.show('Searching subtitles — new ones will appear under Subtitles');
  }

  async refresh() {
    await this.api.refreshMovie(this.id());
    this.toasts.show('Refreshing metadata and scanning disk…');
  }

  async interactiveSearch() {
    const id = this.id();
    this.releasesOpen.set(true);
    try {
      await this.releaseSearch.run((onEvent, signal) => this.api.movieReleasesStream(id, onEvent, signal));
    } catch (e) {
      this.toasts.error(errorMessage(e));
    }
  }

  /** Closing the dialog stops the search. */
  protected closeReleases() {
    this.releaseSearch.cancel();
    this.releasesOpen.set(false);
  }

  async grab(r: ReleaseDecision) {
    this.grabbing.set(r.release.guid);
    try {
      await this.api.grabMovieRelease(this.id(), r.release);
      this.toasts.success('Release sent to the download client');
      this.closeReleases();
    } catch (e) {
      this.toasts.error(errorMessage(e));
    } finally {
      this.grabbing.set(null);
    }
  }

  async toggleMonitored() {
    const m = this.movie()!;
    await this.api.updateMovie(m.id, !m.monitored, m.qualityProfileId, m.minimumAvailability, m.languages ?? null);
    this.movie.set({ ...m, monitored: !m.monitored });
    this.editMonitored = !m.monitored;
  }

  async saveEdit() {
    try {
      await this.api.updateMovie(this.id(), this.editMonitored, this.editProfileId, this.editAvailability, this.editLanguages);
      await this.alternateTitles()?.save();
      this.editing.set(false);
      await this.load();
    } catch (e) {
      this.toasts.error(errorMessage(e));
    }
  }

  protected readonly deleteFile = (unmonitor: boolean) => this.api.deleteMovieFile(this.movie()!.movieFile!.id, unmonitor);

  protected async fileDeleted() {
    this.confirmDeleteFile.set(false);
    this.toasts.success('File deleted');
    await this.load();
  }

  async delete() {
    try {
      await this.api.deleteMovie(this.id(), this.deleteFiles);
      this.toasts.success('Movie removed');
      this.router.navigateByUrl('/movies');
    } catch (e) {
      this.toasts.error(errorMessage(e));
    }
  }
}
