import { Component, inject, input, output } from '@angular/core';
import { Auth } from '../../core/auth';
import { DiscoverItem, MediaRequest } from '../../core/models';
import { AddMovieModal } from '../add-movie-modal/add-movie-modal';
import { AddSeriesModal } from '../add-series-modal/add-series-modal';
import { RequestModal } from '../request-modal/request-modal';

/**
 * Opens the matching add dialog for a discovered item and reports the item, now linked to its library id.
 * Plain users can't add titles, so they get the request dialog instead.
 */
@Component({
  selector: 'kr-discover-add',
  imports: [AddMovieModal, AddSeriesModal, RequestModal],
  templateUrl: './discover-add.html',
  styleUrl: './discover-add.scss',
})
export class DiscoverAdd {
  protected readonly staff = inject(Auth).isStaff;
  readonly item = input.required<DiscoverItem>();
  readonly added = output<DiscoverItem>();
  readonly requested = output<MediaRequest>();
  readonly closed = output<void>();
}
