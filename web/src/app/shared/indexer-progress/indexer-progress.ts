import { Component, computed, input, output } from '@angular/core';
import { IndexerQueryStatus } from '../../core/models';

/**
 * Which indexers a search has queried, which are still running, and why any failed. Bind `selected` (and handle
 * `toggled`/`cleared`) to let the chips of indexers with results filter the results by indexer.
 */
@Component({
  selector: 'kr-indexer-progress',
  templateUrl: './indexer-progress.html',
  styleUrl: './indexer-progress.scss',
})
export class IndexerProgress {
  readonly indexers = input.required<IndexerQueryStatus[]>();
  readonly searching = input(false);
  /** The indexers the results are filtered to; `null` makes the chips plain labels. */
  readonly selected = input<ReadonlySet<number> | null>(null);
  readonly toggled = output<number>();
  readonly cleared = output<void>();

  /** Chips filter (also while results still stream in) once more than one indexer returned results. */
  protected readonly filterable = computed(() =>
    this.selected() !== null && this.indexers().filter(s => (s.releases ?? 0) > 0).length > 1);

  protected readonly finished = computed(() => this.indexers().filter(s => s.state !== 'Pending').length);
  protected readonly failed = computed(() => this.indexers().filter(s => s.state === 'Failed'));
  /** Left out because they keep failing; the Indexers page shows until when. */
  protected readonly skipped = computed(() => this.indexers().filter(s => s.state === 'Skipped').length);
  protected readonly pending = computed(() => this.indexers().filter(s => s.state === 'Pending'));

  protected readonly summary = computed(() => {
    const all = this.indexers();
    if (this.searching()) {
      return all.length === 0 ? 'Searching indexers…' : `Searching indexers… ${this.finished()} of ${all.length} done`;
    }
    if (all.length === 0) return 'No enabled indexers to search';
    const failed = this.failed().length;
    const skipped = this.skipped();
    const queried = all.length - skipped;
    return `${queried} indexer${queried === 1 ? '' : 's'} queried` + (failed ? ` · ${failed} failed` : '')
      + (skipped ? ` · ${skipped} skipped after failing` : '');
  });

  protected canFilter(s: IndexerQueryStatus): boolean {
    return this.filterable() && s.state === 'Done' && (s.releases ?? 0) > 0;
  }

  protected isSelected(s: IndexerQueryStatus): boolean {
    return this.selected()?.has(s.indexerId) ?? false;
  }

  protected chipClass(s: IndexerQueryStatus): string {
    if (this.canFilter(s)) {
      const selected = this.isSelected(s);
      return 'cursor-pointer transition-colors ' + (selected
        ? 'bg-emerald-500/30 text-emerald-100 ring-1 ring-emerald-400'
        : (this.selected()?.size ? 'bg-surface-700 text-slate-400 hover:text-slate-200' : 'bg-emerald-500/10 text-emerald-300 hover:bg-emerald-500/20'));
    }
    switch (s.state) {
      case 'Done': return 'bg-emerald-500/10 text-emerald-300';
      case 'Failed': return 'bg-rose-500/15 text-rose-300';
      case 'Skipped': return 'bg-surface-700 text-slate-500 line-through';
      default: return 'bg-surface-700 text-slate-400 animate-pulse';
    }
  }

  protected chipTitle(s: IndexerQueryStatus): string {
    const took = s.seconds != null ? ` in ${s.seconds.toFixed(1)} s` : '';
    switch (s.state) {
      case 'Done': return `${s.releases ?? 0} result(s)${took}` + (this.canFilter(s) ? (this.isSelected(s) ? ' · click to stop filtering by it' : ' · click to filter by it') : '');
      case 'Failed': return `Failed${took}: ${s.message ?? 'unknown error'}`;
      case 'Skipped': return s.message ?? 'Skipped after failing';
      default: return 'Still searching…';
    }
  }
}
