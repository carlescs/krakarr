import { Component, computed, input } from '@angular/core';

/** Grid classes shared by the library pages and their loading skeleton. */
export const POSTER_GRID = 'grid grid-cols-3 gap-x-3 gap-y-4 sm:gap-4 md:grid-cols-4 lg:grid-cols-5 xl:grid-cols-6 2xl:grid-cols-8';

/** How many cards a library grid renders at first and adds each time the end comes into view. */
export const GRID_CHUNK = 60;

/** Placeholder cards shown while a library list loads for the first time. */
@Component({
  selector: 'kr-poster-grid-skeleton',
  template: `
    <div [class]="grid" aria-busy="true" aria-label="Loading">
      @for (i of cards(); track i) {
        <div class="animate-pulse">
          <div class="aspect-[2/3] rounded-lg bg-surface-800"></div>
          <div class="mt-2 h-3 w-3/4 rounded bg-surface-800"></div>
          <div class="mt-1.5 h-2.5 w-1/3 rounded bg-surface-800"></div>
        </div>
      }
    </div>
  `,
})
export class PosterGridSkeleton {
  readonly count = input(24);
  protected readonly grid = POSTER_GRID;
  protected readonly cards = computed(() => Array.from({ length: this.count() }, (_, i) => i));
}
