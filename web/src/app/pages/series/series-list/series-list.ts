import { Component, DestroyRef, computed, inject, linkedSignal, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Auth } from '../../../core/auth';
import { Library } from '../../../core/library';
import { SeriesListItem } from '../../../core/models';
import { NearViewport } from '../../../shared/near-viewport/near-viewport';
import { Poster } from '../../../shared/poster/poster';
import { GRID_CHUNK, POSTER_GRID, PosterGridSkeleton } from '../../../shared/poster-grid/poster-grid';
import { MoreMenu } from '../../../shared/more-menu/more-menu';
import { Progress } from '../../../shared/progress/progress';

type Filter = 'all' | 'missing' | 'continuing' | 'ended' | 'unmonitored';

@Component({
  selector: 'kr-series-list',
  imports: [RouterLink, FormsModule, DatePipe, Poster, Progress, NearViewport, PosterGridSkeleton, MoreMenu],
  templateUrl: './series-list.html',
  styleUrl: './series-list.scss',
})
export class SeriesListPage {
  protected readonly auth = inject(Auth);
  private readonly cache = inject(Library).series;

  protected readonly grid = POSTER_GRID;
  protected readonly loading = computed(() => this.cache.items() === null);
  protected readonly series = computed(() => this.cache.items() ?? []);
  protected readonly query = signal('');
  protected readonly filter = signal<Filter>('all');

  protected readonly filtered = computed(() => {
    const q = this.query().toLowerCase().trim();
    const filter = this.filter();
    return this.series().filter(s => {
      if (q && !s.title.toLowerCase().includes(q)) return false;
      switch (filter) {
        case 'missing': return s.monitored && s.episodeFileCount < s.episodeCount;
        case 'continuing': return s.status !== 'Ended';
        case 'ended': return s.status === 'Ended';
        case 'unmonitored': return !s.monitored;
        default: return true;
      }
    });
  });

  /** Cards rendered so far: more are added as the end of the grid scrolls into view, and a new filter starts over. */
  private readonly limit = linkedSignal({ source: () => [this.query(), this.filter()], computation: () => GRID_CHUNK });
  protected readonly visible = computed(() => this.filtered().slice(0, this.limit()));

  constructor() {
    inject(DestroyRef).onDestroy(this.cache.attach());
  }

  protected percent(s: SeriesListItem) { return s.episodeCount === 0 ? 0 : (s.episodeFileCount / s.episodeCount) * 100; }

  protected showMore() { this.limit.update(n => n + GRID_CHUNK); }
}
