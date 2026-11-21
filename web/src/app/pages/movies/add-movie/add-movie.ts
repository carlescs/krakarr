import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { Api, errorMessage } from '../../../core/api';
import { MovieLookup } from '../../../core/models';
import { AddMovieModal } from '../../../shared/add-movie-modal/add-movie-modal';
import { Poster } from '../../../shared/poster/poster';

@Component({
  selector: 'kr-add-movie',
  imports: [FormsModule, RouterLink, Poster, AddMovieModal],
  templateUrl: './add-movie.html',
  styleUrl: './add-movie.scss',
})
export class AddMoviePage {
  private readonly api = inject(Api);
  protected readonly router = inject(Router);

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
