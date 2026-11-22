import { Component, computed, input } from '@angular/core';
import { IndexerQueryStatus } from '../../core/models';

/** Which indexers a search has queried, which are still running, and why any failed. */
@Component({
  selector: 'kr-indexer-progress',
  templateUrl: './indexer-progress.html',
  styleUrl: './indexer-progress.scss',
})
export class IndexerProgress {
  readonly indexers = input.required<IndexerQueryStatus[]>();
  readonly searching = input(false);

  protected readonly finished = computed(() => this.indexers().filter(s => s.state !== 'Pending').length);
  protected readonly failed = computed(() => this.indexers().filter(s => s.state === 'Failed'));
  protected readonly pending = computed(() => this.indexers().filter(s => s.state === 'Pending'));

  protected readonly summary = computed(() => {
    const all = this.indexers();
    if (this.searching()) {
      return all.length === 0 ? 'Searching indexers…' : `Searching indexers… ${this.finished()} of ${all.length} done`;
    }
    if (all.length === 0) return 'No enabled indexers to search';
    const failed = this.failed().length;
    return `${all.length} indexer${all.length === 1 ? '' : 's'} queried` + (failed ? ` · ${failed} failed` : '');
  });

  protected chipClass(s: IndexerQueryStatus): string {
    switch (s.state) {
      case 'Done': return 'bg-emerald-500/10 text-emerald-300';
      case 'Failed': return 'bg-rose-500/15 text-rose-300';
      default: return 'bg-surface-700 text-slate-400 animate-pulse';
    }
  }

  protected chipTitle(s: IndexerQueryStatus): string {
    const took = s.seconds != null ? ` in ${s.seconds.toFixed(1)} s` : '';
    switch (s.state) {
      case 'Done': return `${s.releases ?? 0} result(s)${took}`;
      case 'Failed': return `Failed${took}: ${s.message ?? 'unknown error'}`;
      default: return 'Still searching…';
    }
  }
}
