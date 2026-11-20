import { Component, inject, input, output } from '@angular/core';
import { Auth } from '../core/auth';
import { DiscoverItem, MediaRequest } from '../core/models';
import { AddMovieModal } from './add-movie-modal';
import { AddSeriesModal } from './add-series-modal';
import { RequestModal } from './request-modal';

/**
 * Opens the matching add dialog for a discovered item and reports the item, now linked to its library id.
 * Plain users can't add titles, so they get the request dialog instead.
 */
@Component({
  selector: 'kr-discover-add',
  imports: [AddMovieModal, AddSeriesModal, RequestModal],
  template: `
    @let d = item();
    @if (!staff()) {
      <kr-request-modal [item]="d" (closed)="closed.emit()" (requested)="requested.emit($event)" />
    } @else if (d.kind === 'movie') {
      <kr-add-movie-modal [movie]="d.item" (closed)="closed.emit()" (added)="added.emit({ kind: 'movie', item: { ...d.item, movieId: $event.id } })" />
    } @else {
      <kr-add-series-modal [show]="d.item" (closed)="closed.emit()" (added)="added.emit({ kind: 'series', item: { ...d.item, seriesId: $event.id } })" />
    }
  `,
})
export class DiscoverAdd {
  protected readonly staff = inject(Auth).isStaff;
  readonly item = input.required<DiscoverItem>();
  readonly added = output<DiscoverItem>();
  readonly requested = output<MediaRequest>();
  readonly closed = output<void>();
}
