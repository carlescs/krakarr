import { Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Api, errorMessage } from '../../core/api';
import { Auth } from '../../core/auth';
import { Events } from '../../core/events';
import { MediaRequest, RequestStatus } from '../../core/models';
import { canDeleteRequest, requestBadge, requestRoute } from '../../core/requests';
import { Toasts } from '../../core/toast';
import { AddMovieModal } from '../../shared/add-movie-modal/add-movie-modal';
import { AddSeriesModal } from '../../shared/add-series-modal/add-series-modal';
import { Modal } from '../../shared/modal/modal';
import { Poster } from '../../shared/poster/poster';

type Filter = RequestStatus | 'All';

/**
 * Requests for movies and shows. Plain users see and withdraw their own; admins and managers see everyone's and
 * approve (choosing root folder, profile and monitoring) or decline them.
 */
@Component({
  selector: 'kr-requests',
  imports: [DatePipe, FormsModule, RouterLink, Poster, Modal, AddMovieModal, AddSeriesModal],
  templateUrl: './requests.html',
  styleUrl: './requests.scss',
})
export class RequestsPage implements OnInit {
  private readonly api = inject(Api);
  private readonly toasts = inject(Toasts);
  private readonly events = inject(Events);
  private readonly destroyRef = inject(DestroyRef);
  protected readonly staff = inject(Auth).isStaff;

  protected readonly filters: Filter[] = ['Pending', 'Approved', 'Declined', 'All'];
  /** Staff land on the approval queue; users on everything they asked for. */
  protected readonly filter = signal<Filter>(this.staff() ? 'Pending' : 'All');
  protected readonly pageNumber = signal(1);
  protected readonly page = signal<{ results: MediaRequest[]; totalPages: number } | null>(null);
  protected readonly items = computed(() => this.page()?.results ?? []);
  protected readonly error = signal<string | null>(null);
  protected readonly busy = signal<number | null>(null);
  protected readonly approving = signal<MediaRequest | null>(null);
  protected readonly declining = signal<MediaRequest | null>(null);
  protected declineReason = '';

  protected readonly badgeFor = requestBadge;
  protected readonly route = requestRoute;
  protected canDelete(r: MediaRequest) { return canDeleteRequest(r, this.staff()); }

  ngOnInit() {
    this.load();
    this.markSeen();
    this.events.requestsUpdated.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(() => {
      this.load();
      this.markSeen();
    });
  }

  /** Approvals and declines show up right here, so they no longer need a notification. */
  private markSeen() {
    this.api.markRequestsSeen().catch(() => undefined);
  }

  protected setFilter(f: Filter) {
    this.filter.set(f);
    this.pageNumber.set(1);
    this.page.set(null);
    this.load();
  }

  protected go(page: number) {
    this.pageNumber.set(page);
    this.load();
  }

  async load() {
    const filter = this.filter(), page = this.pageNumber();
    try {
      const result = await this.api.requests(filter === 'All' ? undefined : filter, page);
      if (this.filter() !== filter || this.pageNumber() !== page) return;
      this.page.set(result);
      this.pageNumber.set(result.page);
      this.error.set(null);
    } catch (e) {
      this.error.set(errorMessage(e));
    }
  }

  protected startDecline(r: MediaRequest) {
    this.declineReason = '';
    this.declining.set(r);
  }

  async decline(r: MediaRequest) {
    this.busy.set(r.id);
    try {
      await this.api.declineRequest(r.id, this.declineReason.trim());
      this.toasts.success(`Declined ${r.title}`);
      this.declining.set(null);
      await this.load();
    } catch (e) {
      this.toasts.error(errorMessage(e));
    } finally {
      this.busy.set(null);
    }
  }

  async remove(r: MediaRequest) {
    if (!confirm(this.staff() ? `Delete the request for ${r.title}?` : `Withdraw your request for ${r.title}?`)) return;
    this.busy.set(r.id);
    try {
      await this.api.deleteRequest(r.id);
      await this.load();
    } catch (e) {
      this.toasts.error(errorMessage(e));
    } finally {
      this.busy.set(null);
    }
  }
}
