import { Component, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { DatePipe } from '@angular/common';
import { Api, errorMessage } from '../../../core/api';
import { Auth } from '../../../core/auth';
import { BytesPipe } from '../../../core/format';
import { Episode, EpisodeDetails, SubtitleFile } from '../../../core/models';
import { Modal } from '../../../shared/modal/modal';
import { CreditsView } from '../../../shared/credits/credits';
import { ExternalLinks } from '../../../shared/external-links/external-links';
import { Progress } from '../../../shared/progress/progress';

/**
 * Dialog with everything about one library episode: what the library stores (file, subtitles, status) plus TMDB's
 * still, score, guest stars, crew and links, fetched when it opens.
 */
@Component({
  selector: 'kr-episode-details',
  imports: [Modal, CreditsView, ExternalLinks, Progress, DatePipe, BytesPipe],
  templateUrl: './episode-details.html',
  styleUrl: './episode-details.scss',
})
export class EpisodeDetailsDialog {
  private readonly api = inject(Api);
  protected readonly auth = inject(Auth);

  readonly seriesId = input.required<number>();
  readonly episode = input.required<Episode>();
  /** Subtitles of the episode's file. */
  readonly subtitles = input<SubtitleFile[]>([]);

  readonly closed = output<void>();
  readonly search = output<void>();
  readonly interactive = output<void>();
  readonly manageSubtitles = output<void>();

  protected readonly details = signal<EpisodeDetails | null>(null);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);

  protected readonly code = computed(() => {
    const e = this.episode();
    return `S${String(e.seasonNumber).padStart(2, '0')}E${String(e.episodeNumber).padStart(2, '0')}`;
  });
  protected readonly title = computed(() => this.episode().title || this.details()?.title || 'TBA');
  protected readonly overview = computed(() => this.episode().overview || this.details()?.overview);
  protected readonly runtime = computed(() => this.episode().runtime || this.details()?.runtime || 0);

  constructor() {
    // Reload only when another episode is shown, not when the same one is refreshed (download progress).
    effect(() => {
      const seriesId = this.seriesId(), episodeId = this.episode().id;
      untracked(() => this.load(seriesId, episodeId));
    });
  }

  private async load(seriesId: number, episodeId: number) {
    this.loading.set(true);
    this.error.set(null);
    this.details.set(null);
    try {
      const details = await this.api.episodeDetails(seriesId, episodeId);
      if (episodeId === this.episode().id) this.details.set(details ?? null);
    } catch (e) {
      // A missing TMDB key is reported in Settings; the library's own data is still worth showing.
      const message = errorMessage(e);
      if (episodeId === this.episode().id) this.error.set(message.includes('API key') ? null : message);
    } finally {
      if (episodeId === this.episode().id) this.loading.set(false);
    }
  }
}
