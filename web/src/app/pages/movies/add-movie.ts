import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { Api, errorMessage } from '../../core/api';
import { MovieLookup, QualityProfile, RootFolder } from '../../core/models';
import { Toasts } from '../../core/toast';
import { Modal } from '../../shared/modal';
import { Poster } from '../../shared/poster';

@Component({
  selector: 'kr-add-movie',
  imports: [FormsModule, RouterLink, Poster, Modal],
  template: `
    <h1 class="page-title mb-6">Add movie</h1>
    <form class="mb-6 flex gap-2 sm:mb-8 sm:gap-3" (ngSubmit)="search()">
      <input class="input min-w-0 flex-1 !py-3 !text-base" type="search" enterkeyhint="search" name="term" [(ngModel)]="term" placeholder="Search TMDB, e.g. “The Matrix 1999”" autofocus />
      <button class="btn-primary px-4 sm:px-6" [disabled]="searching() || !term.trim()">{{ searching() ? 'Searching…' : 'Search' }}</button>
    </form>

    @if (error()) {
      <div class="card mb-6 border-rose-500/30 px-5 py-4 text-sm text-rose-300">
        {{ error() }} @if (error()!.includes('TMDB')) { — <a routerLink="/settings" class="underline">open settings</a> }
      </div>
    }

    <div class="space-y-3">
      @for (r of results(); track r.tmdbId) {
        <div class="card flex gap-3 p-3 sm:gap-4 sm:p-4">
          <kr-poster class="w-20 shrink-0 sm:w-24" [url]="r.posterUrl" [title]="r.title" />
          <div class="min-w-0 flex-1">
            <div class="flex flex-wrap items-baseline gap-x-2">
              <h2 class="text-base font-semibold text-white sm:text-lg">{{ r.title }}</h2>
              <span class="text-slate-400">{{ r.year || '' }}</span>
            </div>
            @if (r.originalTitle && r.originalTitle !== r.title) {
              <div class="text-xs text-slate-500">{{ r.originalTitle }}</div>
            }
            <p class="mt-2 line-clamp-3 text-sm text-slate-400">{{ r.overview }}</p>
          </div>
          <div class="shrink-0 self-center">
            @if (r.movieId) {
              <a [routerLink]="['/movies', r.movieId]" class="btn-secondary">In library</a>
            } @else {
              <button class="btn-primary" (click)="selected.set(r)">Add</button>
            }
          </div>
        </div>
      }
    </div>

    @if (selected(); as movie) {
      <kr-modal [title]="'Add ' + movie.title" (closed)="selected.set(null)">
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
            <label class="flex items-center gap-2 text-sm"><input type="checkbox" [(ngModel)]="monitored" class="accent-accent-500" /> Monitored</label>
            <label class="flex items-center gap-2 text-sm"><input type="checkbox" [(ngModel)]="searchNow" class="accent-accent-500" /> Start search for movie</label>
          </div>
        }
        <ng-container footer>
          <button class="btn-ghost" (click)="selected.set(null)">Cancel</button>
          <button class="btn-primary" [disabled]="adding() || rootFolders().length === 0" (click)="add(movie)">{{ adding() ? 'Adding…' : 'Add movie' }}</button>
        </ng-container>
      </kr-modal>
    }
  `,
})
export class AddMoviePage implements OnInit {
  private readonly api = inject(Api);
  private readonly toasts = inject(Toasts);
  private readonly router = inject(Router);

  protected term = '';
  protected readonly searching = signal(false);
  protected readonly adding = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly results = signal<MovieLookup[]>([]);
  protected readonly selected = signal<MovieLookup | null>(null);
  protected readonly rootFolders = signal<RootFolder[]>([]);
  protected readonly profiles = signal<QualityProfile[]>([]);
  protected rootFolder = '';
  protected profileId = 0;
  protected monitored = true;
  protected searchNow = true;

  async ngOnInit() {
    const [folders, profiles] = await Promise.all([this.api.rootFolders(), this.api.qualityProfiles()]);
    this.rootFolders.set(folders.filter(f => f.mediaType === 'Movie'));
    this.profiles.set(profiles);
    this.rootFolder = this.rootFolders()[0]?.path ?? '';
    this.profileId = profiles[0]?.id ?? 0;
  }

  async search() {
    if (!this.term.trim()) return;
    this.searching.set(true);
    this.error.set(null);
    try {
      this.results.set(await this.api.lookupMovies(this.term.trim()));
      if (this.results().length === 0) this.error.set('No movies found.');
    } catch (e) {
      this.error.set(errorMessage(e));
    } finally {
      this.searching.set(false);
    }
  }

  async add(movie: MovieLookup) {
    this.adding.set(true);
    try {
      const created = await this.api.addMovie({
        tmdbId: movie.tmdbId, qualityProfileId: this.profileId, rootFolderPath: this.rootFolder,
        monitored: this.monitored, searchForMovie: this.searchNow,
      });
      this.toasts.success(`Added ${created.title}`);
      this.router.navigate(['/movies', created.id]);
    } catch (e) {
      this.toasts.error(errorMessage(e));
    } finally {
      this.adding.set(false);
    }
  }
}
