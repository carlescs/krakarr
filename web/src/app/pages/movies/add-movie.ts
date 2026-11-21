import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { Api, errorMessage } from '../../core/api';
import { MovieLookup } from '../../core/models';
import { AddMovieModal } from '../../shared/add-movie-modal';
import { detailRoute } from '../../shared/media-card';
import { Poster } from '../../shared/poster';

@Component({
  selector: 'kr-add-movie',
  imports: [FormsModule, RouterLink, Poster, AddMovieModal],
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
        @let detail = detailRoute({ kind: 'movie', item: r });
        <div class="card flex gap-3 p-3 sm:gap-4 sm:p-4">
          @if (detail) {
            <a [routerLink]="detail" class="w-20 shrink-0 sm:w-24" [attr.aria-label]="r.title"><kr-poster [url]="r.posterUrl" [title]="r.title" /></a>
          } @else {
            <kr-poster class="w-20 shrink-0 sm:w-24" [url]="r.posterUrl" [title]="r.title" />
          }
          <div class="min-w-0 flex-1">
            <div class="flex flex-wrap items-baseline gap-x-2">
              <h2 class="text-base font-semibold text-white sm:text-lg">
                @if (detail) { <a [routerLink]="detail" class="hover:text-accent-400 hover:underline">{{ r.title }}</a> } @else { {{ r.title }} }
              </h2>
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
      <kr-add-movie-modal [movie]="movie" (closed)="selected.set(null)" (added)="router.navigate(['/movies', $event.id])" />
    }
  `,
})
export class AddMoviePage {
  private readonly api = inject(Api);
  protected readonly router = inject(Router);
  protected readonly detailRoute = detailRoute;

  protected term = '';
  protected readonly searching = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly results = signal<MovieLookup[]>([]);
  protected readonly selected = signal<MovieLookup | null>(null);

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
}
