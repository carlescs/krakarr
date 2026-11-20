import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api, errorMessage } from '../core/api';
import { AgoPipe, BytesPipe } from '../core/format';
import { IndexerError, SearchResult, SearchType } from '../core/models';
import { Toasts } from '../core/toast';
import { MediaLink, MediaLinkPicker, linkFields } from '../shared/media-link-picker';
import { Modal } from '../shared/modal';

@Component({
  selector: 'kr-search',
  imports: [FormsModule, BytesPipe, AgoPipe, Modal, MediaLinkPicker],
  template: `
    <h1 class="page-title mb-6">Search indexers</h1>
    <form class="mb-6 flex flex-wrap gap-3" (ngSubmit)="search()">
      <input class="input min-w-0 flex-1 max-sm:basis-full" name="q" [(ngModel)]="query" placeholder="Search all enabled indexers…" />
      <select class="input flex-1 sm:w-36 sm:flex-none" name="type" [(ngModel)]="type">
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
          <thead><tr><th>Title</th><th class="max-md:hidden">Indexer</th><th class="max-sm:hidden">Age</th><th class="max-sm:hidden">Size</th><th class="max-sm:hidden">Peers</th><th class="max-sm:hidden">Quality</th><th></th></tr></thead>
          <tbody>
            @for (r of results(); track r.release.guid) {
              <tr>
                <td class="w-full max-w-0">
                  <div class="break-all sm:truncate sm:break-normal" [title]="r.release.title">{{ r.release.title }}</div>
                  <div class="mt-1 flex flex-wrap items-center gap-x-2 gap-y-1 text-xs text-slate-400 sm:hidden">
                    <span class="badge bg-surface-700 text-slate-200">{{ r.quality }}</span>
                    <span>{{ r.release.size | bytes }}</span>
                    <span><span class="text-emerald-400">{{ r.release.seeders ?? '?' }}</span> / {{ r.release.peers ?? '?' }}</span>
                    <span>{{ r.release.publishDate | ago }}</span>
                    <span>{{ r.release.indexerName }}</span>
                  </div>
                </td>
                <td class="text-slate-400 max-md:hidden">{{ r.release.indexerName }}</td>
                <td class="whitespace-nowrap text-slate-400 max-sm:hidden">{{ r.release.publishDate | ago }}</td>
                <td class="whitespace-nowrap max-sm:hidden">{{ r.release.size | bytes }}</td>
                <td class="whitespace-nowrap max-sm:hidden"><span class="text-emerald-400">{{ r.release.seeders ?? '?' }}</span> / {{ r.release.peers ?? '?' }}</td>
                <td class="max-sm:hidden"><span class="badge bg-surface-700 text-slate-200">{{ r.quality }}</span></td>
                <td class="text-right"><button class="btn-secondary btn-sm" (click)="open(r)" title="Download"><span class="max-sm:hidden">Download</span><span class="sm:hidden" aria-hidden="true">↓</span></button></td>
              </tr>
            }
          </tbody>
        </table>
      </div>
    } @else if (searched() && !busy()) {
      <p class="text-slate-500">No results.</p>
    }

    @if (grabbing(); as r) {
      <kr-modal title="Download release" (closed)="grabbing.set(null)">
        <div class="space-y-4">
          <div>
            <p class="break-all text-sm text-slate-100">{{ r.release.title }}</p>
            <p class="mt-1 text-xs text-slate-400">{{ r.quality }} · {{ r.release.size | bytes }} · {{ r.release.indexerName }}</p>
          </div>
          <kr-media-link-picker [hint]="r.release.title" [(link)]="link" />
        </div>
        <ng-container footer>
          <button class="btn-ghost" (click)="grabbing.set(null)">Cancel</button>
          <button class="btn-primary" (click)="grab(r)" [disabled]="sending()">{{ sending() ? 'Sending…' : 'Download' }}</button>
        </ng-container>
      </kr-modal>
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

  protected readonly grabbing = signal<SearchResult | null>(null);
  protected readonly link = signal<MediaLink | null>(null);
  protected readonly sending = signal(false);

  protected open(r: SearchResult) {
    this.link.set(null);
    this.grabbing.set(r);
  }

  async grab(r: SearchResult) {
    const link = this.link();
    this.sending.set(true);
    try {
      // Linked releases use the movie/series category; plain ones keep "manual".
      await this.api.grabRelease(r.release, link ? undefined : 'manual', linkFields(link));
      this.toasts.success(link ? `Sent to downloads for ${link.title}` : 'Sent to downloads');
      this.grabbing.set(null);
    } catch (e) {
      this.toasts.error(errorMessage(e));
    } finally {
      this.sending.set(false);
    }
  }
}
