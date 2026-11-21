import { Component, inject, input, output, signal } from '@angular/core';
import { Api, errorMessage } from '../../core/api';
import { DiscoverItem, MediaRequest } from '../../core/models';
import { Toasts } from '../../core/toast';
import { Modal } from '../modal/modal';
import { Poster } from '../poster/poster';

/** Asks an admin or manager to add a movie or show that is not in the library (for plain users). */
@Component({
  selector: 'kr-request-modal',
  imports: [Modal, Poster],
  templateUrl: './request-modal.html',
  styleUrl: './request-modal.scss',
})
export class RequestModal {
  private readonly api = inject(Api);
  private readonly toasts = inject(Toasts);

  readonly item = input.required<DiscoverItem>();
  readonly requested = output<MediaRequest>();
  readonly closed = output<void>();

  protected readonly sending = signal(false);

  async send() {
    const d = this.item();
    if (d.item.tmdbId == null) return;
    this.sending.set(true);
    try {
      const request = await this.api.createRequest(d.kind === 'movie' ? 'Movie' : 'Series', d.item.tmdbId);
      this.toasts.success(`Requested ${request.title}`);
      this.requested.emit(request);
    } catch (e) {
      this.toasts.error(errorMessage(e));
    } finally {
      this.sending.set(false);
    }
  }
}
