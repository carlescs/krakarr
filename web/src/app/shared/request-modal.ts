import { Component, inject, input, output, signal } from '@angular/core';
import { Api, errorMessage } from '../core/api';
import { DiscoverItem, MediaRequest } from '../core/models';
import { Toasts } from '../core/toast';
import { Modal } from './modal';
import { Poster } from './poster';

/** Asks an admin or manager to add a movie or show that is not in the library (for plain users). */
@Component({
  selector: 'kr-request-modal',
  imports: [Modal, Poster],
  template: `
    @let d = item();
    <kr-modal [title]="'Request ' + d.item.title" (closed)="closed.emit()">
      <div class="flex gap-4">
        <kr-poster class="w-24 shrink-0" [url]="d.item.posterUrl" [title]="d.item.title" />
        <div class="min-w-0 text-sm">
          <div class="font-semibold text-white">{{ d.item.title }} @if (d.item.year) { <span class="font-normal text-slate-400">({{ d.item.year }})</span> }</div>
          <div class="mt-0.5 text-slate-400">{{ d.kind === 'movie' ? 'Movie' : 'Series (all seasons)' }}</div>
          <p class="mt-3 text-slate-300">An admin or manager will review your request. Once approved, it is added to the library and downloaded automatically.</p>
        </div>
      </div>
      <ng-container footer>
        <button class="btn-ghost" (click)="closed.emit()">Cancel</button>
        <button class="btn-primary" [disabled]="sending() || d.item.tmdbId == null" (click)="send()">{{ sending() ? 'Requesting…' : 'Request' }}</button>
      </ng-container>
    </kr-modal>
  `,
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
