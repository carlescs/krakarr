import { Component, OnInit, computed, inject, input, output, signal } from '@angular/core';
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
  imports: [AgoPipe],
  templateUrl: './subtitle-list.html',
  styleUrl: './subtitle-list.scss',
})
export class SubtitleList implements OnInit {
  readonly files = input<SubtitleFile[]>([]);
  /** Shows a delete button on external subtitles (tracks inside the video can't be deleted). */
  readonly deletable = input(false);
  readonly changed = output<void>();

  private readonly api = inject(Api);
  private readonly toasts = inject(Toasts);
  protected readonly languages = inject(SubtitleLanguages);
  protected readonly deleting = signal<number | null>(null);

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
