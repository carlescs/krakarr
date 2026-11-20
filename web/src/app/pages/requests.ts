import { Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Api, errorMessage } from '../core/api';
import { Auth } from '../core/auth';
import { Events } from '../core/events';
import { MediaRequest, RequestStatus } from '../core/models';
import { canDeleteRequest, requestBadge, requestRoute } from '../core/requests';
import { Toasts } from '../core/toast';
import { AddMovieModal } from '../shared/add-movie-modal';
import { AddSeriesModal } from '../shared/add-series-modal';
import { Modal } from '../shared/modal';
import { Poster } from '../shared/poster';

type Filter = RequestStatus | 'All';

/**
 * Requests for movies and shows. Plain users see and withdraw their own; admins and managers see everyone's and
 * approve (choosing root folder, profile and monitoring) or decline them.
 */
@Component({
  selector: 'kr-requests',
  imports: [DatePipe, FormsModule, RouterLink, Poster, Modal, AddMovieModal, AddSeriesModal],
  template: `
    <div class="mb-4 flex flex-wrap items-center gap-3 sm:mb-6">
      <h1 class="page-title mr-auto">{{ staff() ? 'Requests' : 'My requests' }}</h1>
      <div class="flex gap-1 rounded-lg bg-surface-900 p-1 ring-1 ring-surface-700">
        @for (f of filters; track f) {
          <button class="btn-sm rounded-md font-medium transition" [class]="filter() === f ? 'bg-surface-700 text-white' : 'text-slate-400 hover:text-white'"
                  (click)="setFilter(f)">{{ f }}</button>
        }
      </div>
    </div>

    @if (error()) {
      <p class="text-rose-300">{{ error() }}</p>
    } @else if (page() === null) {
      <p class="text-slate-500">Loading…</p>
    } @else if (items().length === 0) {
      <div class="card px-5 py-10 text-center text-sm text-slate-500">
        @if (staff()) { No {{ filter() === 'All' ? '' : filter().toLowerCase() + ' ' }}requests. }
        @else { You haven't requested anything{{ filter() === 'All' ? '' : ' ' + filter().toLowerCase() }} yet. Find something in <a routerLink="/discover" class="text-accent-300 hover:underline">Discover</a>. }
      </div>
    } @else {
      <ul class="space-y-3">
        @for (r of items(); track r.id) {
          @let badge = badgeFor(r);
          <li class="card flex gap-3 p-3 sm:gap-4 sm:p-4">
            <a [routerLink]="route(r)" class="w-14 shrink-0 sm:w-16"><kr-poster [url]="r.posterUrl" [title]="r.title" /></a>
            <div class="min-w-0 flex-1">
              <div class="flex flex-wrap items-center gap-2">
                <a [routerLink]="route(r)" class="truncate font-semibold text-white hover:text-accent-300">{{ r.title }}</a>
                @if (r.year) { <span class="text-sm text-slate-400">({{ r.year }})</span> }
              </div>
              <div class="mt-1 flex flex-wrap items-center gap-2">
                <span class="badge" [class]="r.mediaType === 'Movie' ? 'bg-sky-500/15 text-sky-300' : 'bg-violet-500/15 text-violet-300'">{{ r.mediaType }}</span>
                <span class="badge" [class]="badge.classes">{{ badge.label }}</span>
              </div>
              <div class="mt-1.5 text-xs text-slate-500">
                @if (staff()) { Requested by <span class="text-slate-300">{{ r.requestedBy ?? 'deleted user' }}</span> · }
                {{ r.requestedAt | date: 'medium' }}
                @if (r.processedAt && r.status !== 'Pending') {
                  <br />{{ r.status }}{{ r.processedBy ? ' by ' + r.processedBy : '' }} · {{ r.processedAt | date: 'medium' }}
                }
              </div>
              @if (r.declineReason) { <p class="mt-1.5 text-sm text-rose-200/90">“{{ r.declineReason }}”</p> }
            </div>
            <div class="flex shrink-0 flex-col items-end gap-2 sm:flex-row sm:items-start">
              @if (staff() && r.status !== 'Approved') {
                <button class="btn-primary btn-sm" (click)="approving.set(r)">Approve</button>
              }
              @if (staff() && r.status === 'Pending') {
                <button class="btn-secondary btn-sm" (click)="startDecline(r)">Decline</button>
              }
              @if (canDelete(r)) {
                <button class="btn-ghost btn-sm" [disabled]="busy() === r.id" (click)="remove(r)">{{ staff() ? 'Delete' : 'Withdraw' }}</button>
              }
            </div>
          </li>
        }
      </ul>

      @if ((page()?.totalPages ?? 1) > 1) {
        <div class="mt-4 flex items-center justify-center gap-3 text-sm text-slate-400">
          <button class="btn-ghost btn-sm" [disabled]="pageNumber() <= 1" (click)="go(pageNumber() - 1)">‹ Previous</button>
          <span>Page {{ pageNumber() }} of {{ page()?.totalPages }}</span>
          <button class="btn-ghost btn-sm" [disabled]="pageNumber() >= (page()?.totalPages ?? 1)" (click)="go(pageNumber() + 1)">Next ›</button>
        </div>
      }
    }

    @if (approving(); as r) {
      @if (r.mediaType === 'Movie') {
        <kr-add-movie-modal [movie]="r" [request]="r" (closed)="approving.set(null)" (approved)="approving.set(null); load()" />
      } @else {
        <kr-add-series-modal [show]="{ tmdbId: r.tmdbId, tvdbId: r.tvdbId ?? undefined, title: r.title }" [request]="r"
                             (closed)="approving.set(null)" (approved)="approving.set(null); load()" />
      }
    }

    @if (declining(); as r) {
      <kr-modal [title]="'Decline ' + r.title" (closed)="declining.set(null)">
        <label class="label" for="decline-reason">Reason (optional)</label>
        <input id="decline-reason" class="input" maxlength="500" [(ngModel)]="declineReason" placeholder="Shown to {{ r.requestedBy ?? 'the requester' }}" />
        <ng-container footer>
          <button class="btn-ghost" (click)="declining.set(null)">Cancel</button>
          <button class="btn-danger" [disabled]="busy() === r.id" (click)="decline(r)">Decline</button>
        </ng-container>
      </kr-modal>
    }
  `,
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
    this.events.requestsUpdated.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(() => this.load());
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
