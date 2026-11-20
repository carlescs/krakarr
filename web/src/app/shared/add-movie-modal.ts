import { Component, OnInit, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Api, errorMessage } from '../core/api';
import { AVAILABILITY_OPTIONS } from '../core/calendar';
import { MediaRequest, MinimumAvailability, Movie, MovieLookup, QualityProfile, RootFolder } from '../core/models';
import { Toasts } from '../core/toast';
import { Modal } from './modal';

/** Root folder / profile / monitoring options for adding a TMDB movie to the library, directly or by approving a request. */
@Component({
  selector: 'kr-add-movie-modal',
  imports: [FormsModule, RouterLink, Modal],
  template: `
    <kr-modal [title]="(request() ? 'Approve ' : 'Add ') + movie().title" (closed)="closed.emit()">
      @if (request(); as r) { <p class="mb-4 text-sm text-slate-400">Requested by {{ r.requestedBy ?? 'a deleted user' }}. Approving adds it to the library.</p> }
      @if (rootFolders().length === 0) {
        <p class="text-sm text-amber-300">No root folders configured. <a routerLink="/settings" class="underline">Add one in Settings</a> first.</p>
      } @else {
        <div class="space-y-4">
          <div>
            <label class="label">Root folder</label>
            <select class="input" [(ngModel)]="rootFolder">
              @for (f of rootFolders(); track f.id) { <option [value]="f.path">{{ f.path }}</option> }
            </select>
          </div>
          <div>
            <label class="label">Quality profile</label>
            <select class="input" [(ngModel)]="profileId">
              @for (p of profiles(); track p.id) { <option [ngValue]="p.id">{{ p.name }}</option> }
            </select>
          </div>
          <div>
            <label class="label">Minimum availability</label>
            <select class="input" [(ngModel)]="minimumAvailability">
              @for (o of availabilityOptions; track o.value) { <option [value]="o.value">{{ o.label }}</option> }
            </select>
            <p class="hint">Automatic searches wait until the movie reaches this point.</p>
          </div>
          <label class="flex items-center gap-2 text-sm"><input type="checkbox" [(ngModel)]="monitored" class="accent-accent-500" /> Monitored</label>
          <label class="flex items-center gap-2 text-sm"><input type="checkbox" [(ngModel)]="searchNow" class="accent-accent-500" /> Start search for movie</label>
        </div>
      }
      <ng-container footer>
        <button class="btn-ghost" (click)="closed.emit()">Cancel</button>
        <button class="btn-primary" [disabled]="adding() || rootFolders().length === 0" (click)="add()">{{ adding() ? 'Adding…' : request() ? 'Approve and add' : 'Add movie' }}</button>
      </ng-container>
    </kr-modal>
  `,
})
export class AddMovieModal implements OnInit {
  private readonly api = inject(Api);
  private readonly toasts = inject(Toasts);

  readonly movie = input.required<Pick<MovieLookup, 'tmdbId' | 'title'>>();
  /** When set, approves this request with the chosen options instead of adding the movie directly. */
  readonly request = input<MediaRequest | null>(null);
  readonly added = output<Movie>();
  readonly approved = output<MediaRequest>();
  readonly closed = output<void>();

  protected readonly adding = signal(false);
  protected readonly rootFolders = signal<RootFolder[]>([]);
  protected readonly profiles = signal<QualityProfile[]>([]);
  protected rootFolder = '';
  protected profileId = 0;
  protected readonly availabilityOptions = AVAILABILITY_OPTIONS;
  protected monitored = true;
  protected searchNow = true;
  protected minimumAvailability: MinimumAvailability = 'Released';

  async ngOnInit() {
    const [folders, profiles] = await Promise.all([this.api.rootFolders(), this.api.qualityProfiles()]);
    this.rootFolders.set(folders.filter(f => f.mediaType === 'Movie'));
    this.profiles.set(profiles);
    this.rootFolder = this.rootFolders()[0]?.path ?? '';
    this.profileId = profiles[0]?.id ?? 0;
  }

  async add() {
    this.adding.set(true);
    try {
      const request = this.request();
      if (request) {
        const approved = await this.api.approveRequest(request.id, {
          qualityProfileId: this.profileId, rootFolderPath: this.rootFolder, monitored: this.monitored, searchNow: this.searchNow,
          minimumAvailability: this.minimumAvailability,
        });
        this.toasts.success(`Approved ${approved.title}`);
        this.approved.emit(approved);
        return;
      }
      const created = await this.api.addMovie({
        tmdbId: this.movie().tmdbId, qualityProfileId: this.profileId, rootFolderPath: this.rootFolder,
        monitored: this.monitored, searchForMovie: this.searchNow, minimumAvailability: this.minimumAvailability,
      });
      this.toasts.success(`Added ${created.title}`);
      this.added.emit(created);
    } catch (e) {
      this.toasts.error(errorMessage(e));
    } finally {
      this.adding.set(false);
    }
  }
}
