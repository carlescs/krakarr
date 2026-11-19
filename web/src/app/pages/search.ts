import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api, errorMessage } from '../core/api';
import { AgoPipe, BytesPipe } from '../core/format';
import { IndexerError, SearchResult, SearchType } from '../core/models';
import { Toasts } from '../core/toast';

@Component({
  selector: 'kr-search',
  imports: [FormsModule, BytesPipe, AgoPipe],
  template: `
    <h1 class="page-title mb-6">Search indexers</h1>
    <form class="mb-6 flex flex-wrap gap-3" (ngSubmit)="search()">
      <input class="input min-w-64 flex-1" name="q" [(ngModel)]="query" placeholder="Search all enabled indexers…" />
      <select class="input w-36" name="type" [(ngModel)]="type">
        <option value="Generic">All</option><option value="Movie">Movies</option><option value="Tv">TV</option>
      </select>
      <button class="btn-primary" [disabled]="busy()">{{ busy() ? 'Searching…' : 'Search' }}</button>
    </form>
    @for (e of errors(); track e.indexerId) {
      <p class="mb-2 rounded bg-amber-500/10 px-3 py-2 text-xs text-amber-300">{{ e.indexerName }}: {{ e.message }}</p>
    }
    @if (results().length > 0) {
      <div class="card overflow-x-auto">
        <table class="table">
          <thead><tr><th>Title</th><th>Indexer</th><th>Age</th><th>Size</th><th>Peers</th><th>Quality</th><th></th></tr></thead>
          <tbody>
            @for (r of results(); track r.release.guid) {
              <tr>
                <td class="w-full max-w-0"><div class="truncate" [title]="r.release.title">{{ r.release.title }}</div></td>
                <td class="text-slate-400">{{ r.release.indexerName }}</td>
                <td class="whitespace-nowrap text-slate-400">{{ r.release.publishDate | ago }}</td>
                <td class="whitespace-nowrap">{{ r.release.size | bytes }}</td>
                <td class="whitespace-nowrap"><span class="text-emerald-400">{{ r.release.seeders ?? '?' }}</span> / {{ r.release.peers ?? '?' }}</td>
                <td><span class="badge bg-surface-700 text-slate-200">{{ r.quality }}</span></td>
                <td class="text-right"><button class="btn-secondary btn-sm" (click)="grab(r)">Download</button></td>
              </tr>
            }
          </tbody>
        </table>
      </div>
    } @else if (searched() && !busy()) {
      <p class="text-slate-500">No results.</p>
    }
  `,
})
export class SearchPage {
  private readonly api = inject(Api);
  private readonly toasts = inject(Toasts);
  protected query = '';
  protected type: SearchType = 'Generic';
  protected readonly busy = signal(false);
  protected readonly searched = signal(false);
  protected readonly results = signal<SearchResult[]>([]);
  protected readonly errors = signal<IndexerError[]>([]);

  async search() {
    this.busy.set(true);
    try {
      const res = await this.api.search(this.query, this.type);
      this.results.set(res.results);
      this.errors.set(res.errors);
      this.searched.set(true);
    } catch (e) {
      this.toasts.error(errorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }

  async grab(r: SearchResult) {
    try {
      await this.api.grabRelease(r.release, 'manual');
      this.toasts.success('Sent to downloads');
    } catch (e) {
      this.toasts.error(errorMessage(e));
    }
  }
}
