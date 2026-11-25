import { Component, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { Api, errorMessage } from '../../core/api';
import { BestSubtitleMatches, ScoredSubtitle, SubtitleFileRef } from '../../core/models';
import { SubtitleLanguages } from '../../core/subtitles';
import { Toasts } from '../../core/toast';
import { Modal } from '../modal/modal';

/** A file to offer best-match subtitles for. */
export type BestMatchItem = SubtitleFileRef & { label: string };

/**
 * Walks through files one at a time, showing the best subtitle for each missing language even when it scores below
 * the minimum automatic downloads need, and downloads only what the user confirms for that file.
 */
@Component({
  selector: 'kr-subtitle-best-match',
  imports: [Modal],
  templateUrl: './subtitle-best-match.html',
  styleUrl: './subtitle-best-match.scss',
})
export class SubtitleBestMatch implements OnInit {
  readonly items = input.required<BestMatchItem[]>();
  readonly closed = output<void>();
  readonly changed = output<void>();

  private readonly api = inject(Api);
  private readonly toasts = inject(Toasts);
  private readonly subtitleLanguages = inject(SubtitleLanguages);

  protected readonly index = signal(0);
  protected readonly item = computed<BestMatchItem | undefined>(() => this.items()[this.index()]);
  protected readonly loading = signal(false);
  protected readonly match = signal<BestSubtitleMatches | null>(null);
  /** Languages whose best match the user wants downloaded; all of them by default. */
  protected readonly selected = signal<ReadonlySet<string>>(new Set());
  protected readonly downloading = signal(false);
  private downloaded = 0;
  private markedNotFound = 0;

  protected readonly rows = computed(() => {
    const m = this.match();
    if (!m) return [];
    const best = new Map(m.results.map(r => [r.candidate.language, r]));
    return m.missingLanguages.map(language => ({ language, best: best.get(language) }));
  });

  ngOnInit() {
    this.subtitleLanguages.load().catch(() => {});
    void this.load();
  }

  protected languageName(code: string) { return this.subtitleLanguages.name(code); }

  protected belowMinimum(r: ScoredSubtitle) { return r.score < (this.match()?.minimumScore ?? 0); }

  protected toggle(language: string, on: boolean) {
    const next = new Set(this.selected());
    if (on) next.add(language); else next.delete(language);
    this.selected.set(next);
  }

  private async load() {
    const item = this.item();
    if (!item) { this.finish(); return; }
    this.match.set(null);
    this.loading.set(true);
    try {
      const m = await this.api.bestSubtitleMatches({ movieFileId: item.movieFileId, episodeFileId: item.episodeFileId });
      this.match.set(m);
      this.selected.set(new Set(m.results.map(r => r.candidate.language)));
    } catch (e) {
      this.match.set({ label: item.label, missingLanguages: [], minimumScore: 0, results: [], errors: [errorMessage(e)] });
      this.selected.set(new Set());
    } finally {
      this.loading.set(false);
    }
  }

  /** Downloads the selected matches for this file, then moves on. */
  async confirm() {
    const item = this.item();
    const m = this.match();
    if (!item || !m) return;
    this.downloading.set(true);
    try {
      for (const r of m.results.filter(r => this.selected().has(r.candidate.language))) {
        try {
          await this.api.downloadSubtitle({ movieFileId: item.movieFileId, episodeFileId: item.episodeFileId }, r.candidate, r.score);
          this.downloaded++;
          this.changed.emit();
        } catch (e) {
          this.toasts.error(`${this.languageName(r.candidate.language)} for ${item.label}: ${errorMessage(e)}`);
        }
      }
    } finally {
      this.downloading.set(false);
    }
    await this.next();
  }

  /** Rejects what is on offer: marks the file "not found" so the scheduled search stops retrying it, then moves on. */
  async markNotFound() {
    const item = this.item();
    if (!item) return;
    this.downloading.set(true);
    try {
      await this.api.markSubtitlesNotFound({ movieFileId: item.movieFileId, episodeFileId: item.episodeFileId });
      this.markedNotFound++;
      this.changed.emit();
    } catch (e) {
      this.toasts.error(`${item.label}: ${errorMessage(e)}`);
    } finally {
      this.downloading.set(false);
    }
    await this.next();
  }

  /** Leaves this file as it is and moves on. */
  async next() {
    this.index.update(i => i + 1);
    await this.load();
  }

  /** Stops, leaving the remaining files untouched. */
  finish() {
    const done = [
      this.downloaded > 0 ? `downloaded ${this.downloaded} subtitle(s)` : '',
      this.markedNotFound > 0 ? `marked ${this.markedNotFound} file(s) not found` : '',
    ].filter(Boolean).join(', ');
    if (done) this.toasts.success(done[0].toUpperCase() + done.slice(1));
    this.closed.emit();
  }
}
