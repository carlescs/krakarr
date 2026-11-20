import { Component, input, output } from '@angular/core';
import { DiscoverItem } from '../core/models';
import { AddMovieModal } from './add-movie-modal';
import { AddSeriesModal } from './add-series-modal';

/** Opens the matching add dialog for a discovered item and reports the item, now linked to its library id. */
@Component({
  selector: 'kr-discover-add',
  imports: [AddMovieModal, AddSeriesModal],
  template: `
    @let d = item();
    @if (d.kind === 'movie') {
      <kr-add-movie-modal [movie]="d.item" (closed)="closed.emit()" (added)="added.emit({ kind: 'movie', item: { ...d.item, movieId: $event.id } })" />
    } @else {
      <kr-add-series-modal [show]="d.item" (closed)="closed.emit()" (added)="added.emit({ kind: 'series', item: { ...d.item, seriesId: $event.id } })" />
    }
  `,
})
export class DiscoverAdd {
  readonly item = input.required<DiscoverItem>();
  readonly added = output<DiscoverItem>();
  readonly closed = output<void>();
}
