import { Component, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api, errorMessage } from '../../core/api';
import { AgoPipe } from '../../core/format';
import { SubtitleFile } from '../../core/models';
import { SubtitleLanguages, isEmbedded, subtitleLabel, subtitleSource } from '../../core/subtitles';
import { Toasts } from '../../core/toast';

/**
 * A file's subtitles in detail (language, flags, source, score, release, age) plus which wanted languages are covered
 * and which are still missing. Used by the movie page, the episode dialog and the subtitle manager.
 */
@Component({
  selector: 'kr-subtitle-list',
  imports: [AgoPipe, FormsModule],
  templateUrl: './subtitle-list.html',
  styleUrl: './subtitle-list.scss',
})
export class SubtitleList implements OnInit {
  readonly files = input<SubtitleFile[]>([]);
  /**
   * Shows delete and timing tools (sync against the video, shift by hand) on external subtitles; tracks inside the
   * video can't be changed.
   */
  readonly deletable = input(false);
  readonly changed = output<void>();

  private readonly api = inject(Api);
  private readonly toasts = inject(Toasts);
  protected readonly languages = inject(SubtitleLanguages);
  protected readonly deleting = signal<number | null>(null);
  /** The subtitle whose timing tools are open. */
  protected readonly timing = signal<number | null>(null);
  protected readonly busy = signal(false);
  /** Shift to apply, in milliseconds (negative = earlier). */
  protected offset = 0;

  /** Embedded tracks after external files, each group by language. */
  protected readonly sorted = computed(() => [...this.files()].sort((a, b) =>
    Number(isEmbedded(a)) - Number(isEmbedded(b)) || a.languageName.localeCompare(b.languageName)));
  protected readonly missing = computed(() => this.languages.missing(this.files()));
  /** Wanted languages with whether the file has them, in the configured order. */
  protected readonly coverage = computed(() => {
    const wanted = this.languages.wanted();
    if (!wanted?.enabled) return [];
    const missing = new Set(this.missing());
    return wanted.languages.map(code => ({ code, name: this.languages.name(code), present: !missing.has(code) }));
  });

  protected readonly label = subtitleLabel;
  protected readonly source = subtitleSource;
  protected readonly embedded = isEmbedded;

  ngOnInit() { this.languages.load().catch(() => {}); }

  protected scoreClass(score: number) {
    return score >= 80 ? 'bg-emerald-500/15 text-emerald-300' : score >= 60 ? 'bg-amber-500/15 text-amber-300' : 'bg-rose-500/15 text-rose-300';
  }

  protected toggleTiming(s: SubtitleFile) {
    this.offset = 0;
    this.timing.set(this.timing() === s.id ? null : s.id);
  }

  protected nudge(ms: number) { this.offset = Math.round((Number(this.offset) || 0) + ms); }

  async shift(s: SubtitleFile) {
    const offset = Math.round(Number(this.offset) || 0);
    if (offset === 0) return;
    this.busy.set(true);
    try {
      await this.api.shiftSubtitle(s.id, offset);
      this.toasts.success(`Shifted ${subtitleLabel(s)} subtitle by ${offset > 0 ? '+' : ''}${offset} ms`);
      this.offset = 0;
      this.changed.emit();
    } catch (e) {
      this.toasts.error(errorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }

  async sync(s: SubtitleFile) {
    this.busy.set(true);
    try {
      await this.api.syncSubtitle(s.id);
      this.toasts.show(`Syncing ${subtitleLabel(s)} subtitle — the result will show here when it is done`);
      this.timing.set(null);
    } catch (e) {
      this.toasts.error(errorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }

  async remove(s: SubtitleFile) {
    this.deleting.set(s.id);
    try {
      await this.api.deleteSubtitle(s.id);
      this.toasts.success(`Deleted ${subtitleLabel(s)} subtitle`);
      this.changed.emit();
    } catch (e) {
      this.toasts.error(errorMessage(e));
    } finally {
      this.deleting.set(null);
    }
  }
}
