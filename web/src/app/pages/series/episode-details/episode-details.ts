import { Component, computed, effect, inject, input, output, signal, untracked } from '@angular/core';
import { DatePipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { Api, errorMessage } from '../../../core/api';
import { Auth } from '../../../core/auth';
import { BytesPipe } from '../../../core/format';
import { Episode, EpisodeDetails, MediaInfo, SubtitleFile } from '../../../core/models';
import { Modal } from '../../../shared/modal/modal';
import { MoreMenu } from '../../../shared/more-menu/more-menu';
import { CreditsView } from '../../../shared/credits/credits';
import { ExternalLinks } from '../../../shared/external-links/external-links';
import { Progress } from '../../../shared/progress/progress';
import { SubtitleList } from '../../../shared/subtitle-list/subtitle-list';
import { MediaInfoView } from '../../../shared/media-info/media-info';

/**
 * Dialog with everything about one library episode: what the library stores (file, subtitles, status) plus TMDB's
 * still, score, guest stars, crew and links, fetched when it opens.
 */
@Component({
  selector: 'kr-episode-details',
  imports: [RouterLink, Modal, MoreMenu, CreditsView, ExternalLinks, Progress, SubtitleList, MediaInfoView, DatePipe, BytesPipe],
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
  /** Opens the subtitle dialog, searching the given missing language first (or every missing one). */
  readonly manageSubtitles = output<string | null>();
  /** Asks to delete the episode's file. */
  readonly deleteFile = output<void>();
  /** A subtitle was deleted from the dialog. */
  readonly subtitlesChanged = output<void>();

  protected readonly details = signal<EpisodeDetails | null>(null);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);
  /** Media info of the episode's file (null while loading, when there is no file or it couldn't be read). */
  protected readonly mediaInfo = signal<MediaInfo | null>(null);
  /** The episode's page on Jellyfin, once it is downloaded and Jellyfin is set up. */
  protected readonly jellyfinUrl = signal<string | null>(null);
  private readonly fileId = computed(() => this.episode().episodeFile?.id);

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
    // The file's media info, only when the file changes (a computed id ignores refreshes of the same episode).
    effect(() => {
      const seriesId = this.seriesId(), fileId = this.fileId();
      untracked(() => {
        this.loadMediaInfo(seriesId, fileId);
        this.loadJellyfin(seriesId, fileId);
      });
    });
  }

  private async loadMediaInfo(seriesId: number, fileId: number | undefined) {
    this.mediaInfo.set(null);
    if (fileId == null) return;
    try {
      const info = await this.api.episodeFileMediaInfo(seriesId, fileId);
      if (fileId === this.episode().episodeFile?.id) this.mediaInfo.set(info ?? null);
    } catch {
      // Optional detail: the dialog works without it.
    }
  }

  /** Looks the episode up on Jellyfin when its file changes. Failures just hide the button. */
  private async loadJellyfin(seriesId: number, fileId: number | undefined) {
    this.jellyfinUrl.set(null);
    if (fileId == null) return;
    const episodeId = this.episode().id;
    try {
      const link = await this.api.episodeJellyfinLink(seriesId, episodeId);
      if (episodeId === this.episode().id && fileId === this.fileId()) this.jellyfinUrl.set(link?.url ?? null);
    } catch {
      // Not configured, unreachable or unknown to Jellyfin: no button.
    }
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
