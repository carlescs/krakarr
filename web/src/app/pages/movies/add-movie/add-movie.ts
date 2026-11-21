import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { Api, errorMessage } from '../../../core/api';
import { Auth } from '../../../core/auth';
import { DiscoverItem, MediaRequest, MovieLookup } from '../../../core/models';
import { RequestModal } from '../../../shared/request-modal/request-modal';
import { AddMovieModal } from '../../../shared/add-movie-modal/add-movie-modal';
import { detailRoute } from '../../../shared/media-card/media-card';
import { Poster } from '../../../shared/poster/poster';

@Component({
  selector: 'kr-add-movie',
  imports: [FormsModule, RouterLink, Poster, AddMovieModal, RequestModal],
  templateUrl: './add-movie.html',
  styleUrl: './add-movie.scss',
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
  /** Plain users request titles instead of adding them. */
  protected readonly auth = inject(Auth);
  protected readonly requesting = signal<MovieLookup | null>(null);
  protected readonly requestItem = computed<DiscoverItem | null>(() => {
    const r = this.requesting();
    return r ? { kind: 'movie', item: r } : null;
  });
  /** TMDB ids requested from this page. */
  protected readonly requested = signal<ReadonlySet<number>>(new Set());

  protected markRequested(r: MediaRequest) {
    this.requesting.set(null);
    this.requested.update(ids => new Set(ids).add(r.tmdbId));
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
}
