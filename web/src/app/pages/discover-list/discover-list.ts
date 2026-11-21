import { Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Api, errorMessage } from '../../core/api';
import { MOVIE_LISTS, SERIES_LISTS, movieItems, seriesItems } from '../../core/discover';
import { DiscoverItem, MovieDiscoverList, SeriesDiscoverList } from '../../core/models';
import { DiscoverAdd } from '../../shared/discover-add/discover-add';
import { MediaCard, discoverKey } from '../../shared/media-card/media-card';

/** Full, paged grid of one discovery list ("See all"). */
@Component({
  selector: 'kr-discover-list',
  imports: [RouterLink, MediaCard, DiscoverAdd],
  templateUrl: './discover-list.html',
  styleUrl: './discover-list.scss',
})
export class DiscoverListPage {
  private readonly api = inject(Api);

  readonly media = input.required<'movies' | 'series'>();
  readonly list = input.required<string>();

  protected readonly key = discoverKey;
  protected readonly lists = computed(() => this.media() === 'series' ? SERIES_LISTS : MOVIE_LISTS);
  protected readonly info = computed(() => this.lists().find(l => l.id === this.list()));
  protected readonly items = signal<DiscoverItem[]>([]);
  protected readonly page = signal(0);
  protected readonly totalPages = signal(1);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly selected = signal<DiscoverItem | null>(null);

  constructor() {
    // Restart from page 1 whenever the route switches list.
    effect(() => {
      this.media();
      this.list();
      untracked(() => {
        this.items.set([]);
        this.page.set(0);
        this.totalPages.set(1);
        this.load(1);
      });
    });
  }

  async load(page: number) {
    const media = this.media(), list = this.list();
    this.loading.set(true);
    this.error.set(null);
    try {
      const result = media === 'series'
        ? await this.api.discoverSeries(list as SeriesDiscoverList, page)
        : await this.api.discoverMovies(list as MovieDiscoverList, page);
      if (media !== this.media() || list !== this.list()) return;
      const fresh = media === 'series' ? seriesItems(result.results as never) : movieItems(result.results as never);
      // TMDB pages can overlap while lists shift; keep the first occurrence.
      const seen = new Set(this.items().map(discoverKey));
      this.items.update(items => [...items, ...fresh.filter(d => !seen.has(discoverKey(d)))]);
      this.page.set(result.page);
      this.totalPages.set(result.totalPages);
    } catch (e) {
      this.error.set(errorMessage(e));
    } finally {
      this.loading.set(false);
    }
  }

  protected markAdded(d: DiscoverItem) {
    this.selected.set(null);
    this.items.update(items => items.map(x => discoverKey(x) === discoverKey(d) ? d : x));
  }
}
