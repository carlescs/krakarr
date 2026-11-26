import { Component, inject, input, output } from '@angular/core';
import { ScoredSubtitle } from '../../core/models';
import { SubtitleLanguages } from '../../core/subtitles';
import { TableSort } from '../../core/table-sort';
import { SortHeader } from '../sort-header/sort-header';

/** Stable key of a provider result. */
export const resultKey = (r: ScoredSubtitle) => `${r.candidate.provider}:${r.candidate.payload}`;

/**
 * Provider results for one file, sortable, each with a download button. Shared by the subtitle manager (every result)
 * and the best-match review ("other options" for one language). The parent downloads; this only reports the pick.
 */
@Component({
  selector: 'kr-subtitle-results',
  imports: [SortHeader],
  templateUrl: './subtitle-results.html',
  styleUrl: './subtitle-results.scss',
})
export class SubtitleResults {
  readonly results = input<ScoredSubtitle[]>([]);
  /** Hides the language column (every row is the same language). */
  readonly singleLanguage = input(false);
  /** Key of the result being downloaded. */
  readonly downloading = input<string | null>(null);
  /** Keys of results already downloaded from this list. */
  readonly downloaded = input<ReadonlySet<string>>(new Set());
  /** Label of the row button. */
  readonly action = input('Download');
  readonly pick = output<ScoredSubtitle>();

  private readonly languages = inject(SubtitleLanguages);
  /** The API returns results best first. */
  protected readonly sort = new TableSort(this.results, {
    release: r => r.candidate.releaseNames[0] ?? r.candidate.fileName,
    language: r => this.languageName(r.candidate.language),
    score: { value: r => r.score, desc: true },
    downloads: { value: r => r.candidate.downloads, desc: true },
  });

  protected readonly key = resultKey;

  protected languageName(code: string) { return this.languages.name(code); }

  protected scoreClass(score: number) {
    return score >= 80 ? 'bg-emerald-500/15 text-emerald-300' : score >= 60 ? 'bg-amber-500/15 text-amber-300' : 'bg-rose-500/15 text-rose-300';
  }
}
