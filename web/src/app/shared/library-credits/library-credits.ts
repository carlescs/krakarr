import { Component, effect, inject, input, signal, untracked } from '@angular/core';
import { Api, errorMessage } from '../../core/api';
import { CastMember, CrewMember } from '../../core/models';
import { CreditsView } from '../credits/credits';

/** Cast and crew of a library movie or show, fetched from TMDB. */
@Component({
  selector: 'kr-library-credits',
  imports: [CreditsView],
  templateUrl: './library-credits.html',
  styleUrl: './library-credits.scss',
  host: { class: 'block mt-8' },
})
export class LibraryCredits {
  private readonly api = inject(Api);

  readonly kind = input.required<'movie' | 'series'>();
  /** Local library id. */
  readonly id = input.required<number>();

  protected readonly cast = signal<CastMember[]>([]);
  protected readonly crew = signal<CrewMember[]>([]);
  protected readonly loading = signal(true);
  protected readonly error = signal<string | null>(null);

  constructor() {
    effect(() => {
      const kind = this.kind(), id = this.id();
      untracked(() => this.load(kind, id));
    });
  }

  private async load(kind: 'movie' | 'series', id: number) {
    this.loading.set(true);
    this.error.set(null);
    try {
      const credits = await (kind === 'movie' ? this.api.movieCredits(id) : this.api.seriesCredits(id));
      this.cast.set(credits.cast);
      this.crew.set(credits.crew);
    } catch (e) {
      // A missing TMDB key is reported in Settings; hide the section rather than nag on every detail page.
      const message = errorMessage(e);
      this.cast.set([]);
      this.crew.set([]);
      this.error.set(message.includes('API key') ? null : message);
    } finally {
      this.loading.set(false);
    }
  }
}
