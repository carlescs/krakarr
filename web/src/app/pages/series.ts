import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api, errorMessage } from '../core/api';
import { SeriesLookup } from '../core/models';
import { Poster } from '../shared/poster';

@Component({
  selector: 'kr-series',
  imports: [FormsModule, Poster],
  template: `
    <h1 class="page-title mb-2">Series</h1>
    <p class="mb-6 text-sm text-slate-400">TV library management (Sonarr replacement) is coming in the next milestone. You can already look up shows on TheTVDB.</p>
    <form class="mb-6 flex gap-3" (ngSubmit)="search()">
      <input class="input flex-1" name="term" [(ngModel)]="term" placeholder="Search TheTVDB…" />
      <button class="btn-secondary" [disabled]="busy()">Search</button>
    </form>
    @if (error()) { <p class="mb-4 text-sm text-rose-300">{{ error() }}</p> }
    <div class="grid grid-cols-2 gap-4 sm:grid-cols-4 lg:grid-cols-6">
      @for (s of results(); track s.tvdbId) {
        <div>
          <kr-poster [url]="s.posterUrl" [title]="s.title" />
          <div class="mt-2 truncate text-sm font-medium">{{ s.title }}</div>
          <div class="text-xs text-slate-500">{{ s.year || '' }} {{ s.network ? '· ' + s.network : '' }}</div>
        </div>
      }
    </div>
  `,
})
export class SeriesPage {
  private readonly api = inject(Api);
  protected term = '';
  protected readonly busy = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly results = signal<SeriesLookup[]>([]);

  async search() {
    this.busy.set(true);
    this.error.set(null);
    try { this.results.set(await this.api.lookupSeries(this.term)); }
    catch (e) { this.error.set(errorMessage(e)); }
    finally { this.busy.set(false); }
  }
}
