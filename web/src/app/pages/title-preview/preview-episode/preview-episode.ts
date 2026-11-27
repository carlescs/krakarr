import { Component, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { DatePipe } from '@angular/common';
import { Api, errorMessage } from '../../../core/api';
import { EpisodeDetails, SeasonEpisode } from '../../../core/models';
import { Modal } from '../../../shared/modal/modal';
import { CreditsView } from '../../../shared/credits/credits';
import { ExternalLinks } from '../../../shared/external-links/external-links';

/**
 * Dialog with one episode of a show that is not in the library: what the season listing already knows, plus guest
 * stars, crew and links fetched from TMDB when it opens.
 */
@Component({
  selector: 'kr-preview-episode',
  imports: [Modal, CreditsView, ExternalLinks, DatePipe],
  templateUrl: './preview-episode.html',
  styleUrl: './preview-episode.scss',
})
export class PreviewEpisodeDialog {
  private readonly api = inject(Api);

  readonly tmdbId = input.required<number>();
  readonly seasonNumber = input.required<number>();
  readonly episode = input.required<SeasonEpisode>();

  readonly closed = output<void>();

  protected readonly details = signal<EpisodeDetails | null>(null);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);

  protected readonly code = computed(() =>
    `S${String(this.seasonNumber()).padStart(2, '0')}E${String(this.episode().episodeNumber).padStart(2, '0')}`);
  protected readonly title = computed(() => this.episode().title || this.details()?.title || 'TBA');
  protected readonly overview = computed(() => this.episode().overview || this.details()?.overview);
  protected readonly runtime = computed(() => this.episode().runtime || this.details()?.runtime || 0);
  protected readonly stillUrl = computed(() => this.details()?.stillUrl ?? this.episode().stillUrl);
  protected readonly rating = computed(() => this.details()?.rating ?? this.episode().rating);

  constructor() {
    effect(() => {
      const tmdbId = this.tmdbId(), season = this.seasonNumber(), episode = this.episode().episodeNumber;
      untracked(() => this.load(tmdbId, season, episode));
    });
  }

  private current(tmdbId: number, season: number, episode: number) {
    return this.tmdbId() === tmdbId && this.seasonNumber() === season && this.episode().episodeNumber === episode;
  }

  private async load(tmdbId: number, season: number, episode: number) {
    this.loading.set(true);
    this.error.set(null);
    this.details.set(null);
    try {
      const details = await this.api.tmdbEpisode(tmdbId, season, episode);
      if (this.current(tmdbId, season, episode)) this.details.set(details);
    } catch (e) {
      if (this.current(tmdbId, season, episode)) this.error.set(errorMessage(e));
    } finally {
      if (this.current(tmdbId, season, episode)) this.loading.set(false);
    }
  }
}
