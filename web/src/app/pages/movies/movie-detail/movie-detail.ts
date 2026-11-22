import { Component, DestroyRef, OnInit, computed, effect, inject, input, numberAttribute, signal, untracked } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { Api, errorMessage } from '../../../core/api';
import { Auth } from '../../../core/auth';
import { Events } from '../../../core/events';
import { AgoPipe, BytesPipe, DurationPipe, SpeedPipe } from '../../../core/format';
import { AVAILABILITY_OPTIONS, RELEASE_LABELS } from '../../../core/calendar';
import { HistoryItem, MinimumAvailability, Movie, QualityProfile, ReleaseDateType, ReleaseDecision, SubtitleFile } from '../../../core/models';
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
import { StateBadge } from '../../../shared/state-badge/state-badge';
import { SubtitleManager } from '../../../shared/subtitle-manager/subtitle-manager';
import { SubscribeButton } from '../../../shared/subscribe-button/subscribe-button';
import { ExternalLinks } from '../../../shared/external-links/external-links';

@Component({
  selector: 'kr-movie-detail',
  imports: [ExternalLinks, SubscribeButton, LibraryCredits, RelatedTitles, LanguageOverride, FormsModule, DatePipe, Poster, Progress, Modal, IndexerProgress, StateBadge, SubtitleManager, SortHeader, BytesPipe, SpeedPipe, DurationPipe, AgoPipe],
  templateUrl: './movie-detail.html',
  styleUrl: './movie-detail.scss',
})
export class MovieDetailPage implements OnInit {
  readonly id = input.required({ transform: numberAttribute });

  protected readonly auth = inject(Auth);
  private readonly api = inject(Api);
  private readonly toasts = inject(Toasts);
  private readonly events = inject(Events);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly movie = signal<Movie | null>(null);
  protected readonly history = signal<HistoryItem[]>([]);
  protected readonly profiles = signal<QualityProfile[]>([]);
  protected readonly releases = signal<ReleaseDecision[]>([]);
  /** The API returns releases best first. */
  protected readonly releaseSort = new TableSort(this.releases, {
    release: r => r.release.title,
    indexer: r => r.release.indexerName,
    size: { value: r => r.release.size, desc: true },
    peers: { value: r => r.release.seeders, desc: true },
    quality: { value: r => r.qualityWeight, desc: true },
    language: r => this.languages.describe(r.languages),
  });
  protected readonly indexerSearch = new IndexerSearch();
  protected readonly releasesOpen = signal(false);
  protected readonly searching = signal(false);
  protected readonly grabbing = signal<string | null>(null);
  protected readonly editing = signal(false);
  protected readonly confirmDelete = signal(false);
  protected readonly subtitles = signal<SubtitleFile[]>([]);
  protected readonly subtitlesOpen = signal(false);
  /** The movie's page on Jellyfin, once it's downloaded and Jellyfin is set up. */
  protected readonly jellyfinUrl = signal<string | null>(null);
  private jellyfinCheckedFor: number | null = null;
  protected editMonitored = true;
  protected editProfileId = 0;
  protected editAvailability: MinimumAvailability = 'Released';
  protected editLanguages: string[] | null = null;
  protected readonly languages = inject(Languages);
  protected readonly availabilityOptions = AVAILABILITY_OPTIONS;
  protected readonly releaseLabels = RELEASE_LABELS;
  protected readonly releaseBar: Record<ReleaseDateType, string> = {
    Theatrical: 'border-l-amber-400', Digital: 'border-l-violet-400', Physical: 'border-l-pink-400',
  };
  protected deleteFiles = false;

  protected readonly downloads = computed(() => this.events.downloads().filter(d => d.movieId === this.id() && !d.imported));
  protected readonly profileName = computed(() => this.profiles().find(p => p.id === this.movie()?.qualityProfileId)?.name ?? '');

  constructor() {
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
    this.releases.set([]);
    this.indexerSearch.reset();
    this.releasesOpen.set(false);
    this.searching.set(false);
    this.grabbing.set(null);
    this.editing.set(false);
    this.confirmDelete.set(false);
    this.subtitlesOpen.set(false);
    this.jellyfinUrl.set(null);
    this.jellyfinCheckedFor = null;
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
      this.loadJellyfin(id, m.hasFile);
      await Promise.all([this.loadHistory(), this.loadSubtitles()]);
    } catch (e) {
      this.toasts.error(errorMessage(e));
    }
  }

  /** Looks the title up on Jellyfin once per page, as soon as something is downloaded. Failures just hide the button. */
  private async loadJellyfin(id: number, hasFile: boolean) {
    if (!hasFile || this.jellyfinCheckedFor === id) return;
    this.jellyfinCheckedFor = id;
    try {
      const link = await this.api.movieJellyfinLink(id);
      if (id === this.id()) this.jellyfinUrl.set(link?.url ?? null);
    } catch {
      // Not configured, unreachable or unknown to Jellyfin: no button.
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

  async searchSubtitles() {
    await this.api.searchSubtitles({ movieId: this.id() });
    this.toasts.show('Searching subtitles — new ones will appear under File');
  }

  async interactiveSearch() {
    const id = this.id();
    this.releasesOpen.set(true);
    this.releases.set([]);
    this.searching.set(true);
    const searchId = this.indexerSearch.start();
    try {
      const res = await this.api.movieReleases(id, searchId);
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

  async grab(r: ReleaseDecision) {
    this.grabbing.set(r.release.guid);
    try {
      await this.api.grabMovieRelease(this.id(), r.release);
      this.toasts.success('Release sent to the download client');
      this.releasesOpen.set(false);
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
      this.editing.set(false);
      await this.load();
    } catch (e) {
      this.toasts.error(errorMessage(e));
    }
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
