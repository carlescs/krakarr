import { Component, effect, inject, input, numberAttribute, signal, untracked } from '@angular/core';
import { DatePipe } from '@angular/common';
import { Title } from '@angular/platform-browser';
import { Router, RouterLink } from '@angular/router';
import { Api, errorMessage } from '../../core/api';
import { Auth } from '../../core/auth';
import { airsOn } from '../../core/format';
import { CastMember, CrewMember, DiscoverItem, MediaRequest, MovieDetails, MovieRelease, SeasonDetails, SeasonEpisode, SeasonSummary, SeriesDetails } from '../../core/models';
import { CreditsView } from '../../shared/credits/credits';
import { DiscoverAdd } from '../../shared/discover-add/discover-add';
import { Poster } from '../../shared/poster/poster';
import { Overview } from '../../shared/overview/overview';
import { RelatedTitles } from '../../shared/related-titles/related-titles';
import { ExternalLinks } from '../../shared/external-links/external-links';
import { MovieReleases } from '../../shared/movie-releases/movie-releases';
import { PreviewEpisodeDialog } from './preview-episode/preview-episode';

type Kind = 'movie' | 'series';

/** What the page shows, common to movies and shows. */
interface Preview {
  item: DiscoverItem;
  title: string;
  year: number;
  overview?: string;
  tagline?: string;
  posterUrl?: string;
  fanartUrl?: string;
  rating?: number;
  facts: string[];
  genres: string[];
  imdbId?: string;
  tvdbId?: number;
  releases: MovieRelease[];
  releaseRegion?: string | null;
  cast: CastMember[];
  crew: CrewMember[];
  seasons: SeasonSummary[];
}

/**
 * Detail page for any TMDB movie or show that is not in the library (discovery, recommendations, a person's credits).
 * Titles that are in the library redirect to their library page.
 */
@Component({
  selector: 'kr-title-preview',
  imports: [ExternalLinks, MovieReleases, DatePipe, RouterLink, Poster, Overview, CreditsView, RelatedTitles, DiscoverAdd, PreviewEpisodeDialog],
  templateUrl: './title-preview.html',
  styleUrl: './title-preview.scss',
})
export class TitlePreviewPage {
  private readonly api = inject(Api);
  private readonly router = inject(Router);
  private readonly title = inject(Title);

  /** Route data. */
  readonly kind = input<Kind>('movie');
  /** Route parameter. */
  readonly tmdbId = input.required({ transform: numberAttribute });

  /** Adding to the library is for admins and managers; plain users request titles instead. */
  protected readonly canAdd = inject(Auth).isStaff;
  protected readonly preview = signal<Preview | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly adding = signal(false);
  /** Pending request for this title, by anyone. */
  protected readonly openRequest = signal<MediaRequest | null>(null);
  /** Season whose episodes are listed (series), by number. */
  protected readonly selectedSeason = signal<number | null>(null);
  /** The selected season's episodes, once loaded. */
  protected readonly season = signal<SeasonDetails | null>(null);
  protected readonly seasonLoading = signal(false);
  protected readonly seasonError = signal<string | null>(null);
  /** Episode open in the details dialog. */
  protected readonly episode = signal<SeasonEpisode | null>(null);

  constructor() {
    // The component is reused when a related title links to another preview, so reload on every id change.
    effect(() => {
      const kind = this.kind(), id = this.tmdbId();
      untracked(() => this.load(kind, id));
    });
  }

  private async load(kind: Kind, tmdbId: number) {
    this.preview.set(null);
    this.error.set(null);
    this.adding.set(false);
    this.openRequest.set(null);
    this.selectedSeason.set(null);
    this.season.set(null);
    this.seasonError.set(null);
    this.episode.set(null);
    try {
      const preview = kind === 'movie' ? fromMovie(await this.api.tmdbMovie(tmdbId)) : fromSeries(await this.api.tmdbSeries(tmdbId));
      if (this.kind() !== kind || this.tmdbId() !== tmdbId) return;
      const libraryId = preview.item.kind === 'movie' ? preview.item.item.movieId : preview.item.item.seriesId;
      if (libraryId) {
        this.router.navigate([kind === 'movie' ? '/movies' : '/series', libraryId], { replaceUrl: true });
        return;
      }
      this.preview.set(preview);
      this.title.setTitle(`${preview.title} · Krakarr`);
      const request = await this.api.requestFor(kind === 'movie' ? 'Movie' : 'Series', tmdbId).catch(() => null);
      if (this.kind() === kind && this.tmdbId() === tmdbId) this.openRequest.set(request);
    } catch (e) {
      this.error.set(errorMessage(e));
    }
  }

  /** Lists a season's episodes, or hides them when it is already shown. */
  protected async toggleSeason(seasonNumber: number) {
    this.episode.set(null);
    this.seasonError.set(null);
    if (this.selectedSeason() === seasonNumber) {
      this.selectedSeason.set(null);
      this.season.set(null);
      return;
    }
    this.selectedSeason.set(seasonNumber);
    this.season.set(null);
    this.seasonLoading.set(true);
    const tmdbId = this.tmdbId();
    const current = () => this.selectedSeason() === seasonNumber && this.tmdbId() === tmdbId;
    try {
      const season = await this.api.tmdbSeason(tmdbId, seasonNumber);
      if (current()) this.season.set(season);
    } catch (e) {
      if (current()) this.seasonError.set(errorMessage(e));
    } finally {
      if (current()) this.seasonLoading.set(false);
    }
  }

  protected seasonName(s: { seasonNumber: number; name?: string }) {
    return s.name ?? (s.seasonNumber === 0 ? 'Specials' : 'Season ' + s.seasonNumber);
  }

  protected openLibrary(d: DiscoverItem) {
    this.adding.set(false);
    const id = d.kind === 'movie' ? d.item.movieId : d.item.seriesId;
    if (id) this.router.navigate([d.kind === 'movie' ? '/movies' : '/series', id], { replaceUrl: true });
  }

  protected requested(r: MediaRequest) {
    this.adding.set(false);
    this.openRequest.set(r);
  }

  protected back() { history.back(); }
}

function fromMovie(d: MovieDetails): Preview {
  const m = d.movie;
  const facts: string[] = [];
  if (d.releaseDate) facts.push(new Date(d.releaseDate + 'T00:00:00').toLocaleDateString(undefined, { dateStyle: 'medium' }));
  if (m.runtime) facts.push(`${m.runtime} min`);
  if (m.status && m.status !== 'Released') facts.push(m.status);
  return {
    item: { kind: 'movie', item: m }, title: m.title, year: m.year, overview: m.overview, tagline: d.tagline, posterUrl: m.posterUrl,
    fanartUrl: m.fanartUrl, rating: m.rating, facts, genres: d.genres, imdbId: m.imdbId,
    releases: d.releases ?? [], releaseRegion: d.releaseRegion,
    cast: d.cast, crew: d.crew, seasons: [],
  };
}

function fromSeries(d: SeriesDetails): Preview {
  const s = d.show;
  const facts: string[] = [];
  if (s.network) facts.push(airsOn(s.network, s.status));
  if (d.runtime) facts.push(`${d.runtime} min`);
  if (s.status) facts.push(s.status);
  if (d.episodeCount) facts.push(`${d.episodeCount} episodes`);
  return {
    item: { kind: 'series', item: s }, title: s.title, year: s.year, overview: s.overview, tagline: d.tagline, posterUrl: s.posterUrl,
    fanartUrl: d.fanartUrl, rating: s.rating, facts, genres: d.genres, imdbId: d.imdbId, tvdbId: s.tvdbId,
    releases: [],
    cast: d.cast, crew: d.crew, seasons: d.seasons,
  };
}
