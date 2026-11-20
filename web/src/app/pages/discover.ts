import { Component, OnInit, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Api, errorMessage } from '../core/api';
import { DiscoverListInfo, MOVIE_LISTS, SERIES_LISTS, interleave, movieItems, seriesItems } from '../core/discover';
import { DiscoverItem } from '../core/models';
import { MediaRow } from '../shared/media-row';

interface RowState { key: string; title: string; subtitle: string; link: string[]; items: DiscoverItem[]; loading: boolean; error: string | null; }

/** Seerr-style discovery: personal picks from the library plus TMDB's trending, popular and upcoming lists. */
@Component({
  selector: 'kr-discover',
  imports: [RouterLink, MediaRow],
  template: `
    <div class="mb-6 flex flex-wrap items-center gap-3">
      <h1 class="page-title mr-auto">Discover</h1>
      <a routerLink="/movies/add" class="btn-secondary">Search movies</a>
      <a routerLink="/series/add" class="btn-secondary">Search series</a>
    </div>

    @if (setupError()) {
      <div class="card mb-6 border-amber-500/30 px-5 py-4 text-sm text-amber-200">
        {{ setupError() }} — recommendations come from TMDB. <a routerLink="/settings" class="underline">Add a TMDB API key in Settings</a>.
      </div>
    }

    @if (!setupError()) {
      @if (forYou().loading || forYou().items.length > 0) {
        <kr-media-row title="Recommended for you" subtitle="Based on what you added recently"
                      [items]="forYou().items" [loading]="forYou().loading" [error]="forYou().error" />
      }
      @for (row of rows(); track row.key) {
        <kr-media-row [title]="row.title" [subtitle]="row.subtitle" [link]="row.link"
                      [items]="row.items" [loading]="row.loading" [error]="row.error" />
      }
    }
  `,
})
export class DiscoverPage implements OnInit {
  private readonly api = inject(Api);

  protected readonly setupError = signal<string | null>(null);
  protected readonly forYou = signal<{ items: DiscoverItem[]; loading: boolean; error: string | null }>({ items: [], loading: true, error: null });
  protected readonly rows = signal<RowState[]>([
    ...this.initial('movies', MOVIE_LISTS.filter(l => l.id !== 'now-playing')),
    ...this.initial('series', SERIES_LISTS.filter(l => l.id !== 'airing-today')),
  ].sort((a, b) => ORDER.indexOf(a.key) - ORDER.indexOf(b.key)));

  ngOnInit() {
    this.loadForYou();
    for (const row of this.rows()) this.loadRow(row.key);
  }

  private initial(media: 'movies' | 'series', lists: DiscoverListInfo<string>[]): RowState[] {
    return lists.map(l => ({ key: `${media}/${l.id}`, title: l.title, subtitle: l.subtitle, link: ['/discover', media, l.id], items: [], loading: true, error: null }));
  }

  private async loadForYou() {
    const [movies, series] = await Promise.allSettled([this.api.moviesForYou(), this.api.seriesForYou()]);
    const items = interleave(
      movies.status === 'fulfilled' ? movieItems(movies.value) : [],
      series.status === 'fulfilled' ? seriesItems(series.value) : [],
    );
    const failure = [movies, series].find(r => r.status === 'rejected') as PromiseRejectedResult | undefined;
    this.forYou.set({ items, loading: false, error: items.length === 0 && failure ? errorMessage(failure.reason) : null });
  }

  private async loadRow(key: string) {
    const [media, list] = key.split('/');
    try {
      const items = media === 'movies'
        ? movieItems((await this.api.discoverMovies(list as never)).results)
        : seriesItems((await this.api.discoverSeries(list as never)).results);
      this.patch(key, { items, loading: false });
    } catch (e) {
      const message = errorMessage(e);
      if (message.includes('API key')) this.setupError.set(message);
      this.patch(key, { loading: false, error: message });
    }
  }

  private patch(key: string, change: Partial<RowState>) {
    this.rows.update(rows => rows.map(r => r.key === key ? { ...r, ...change } : r));
  }
}

/** Row order on the page, mixing movies and series like Seerr does. */
const ORDER = ['movies/trending', 'series/trending', 'movies/popular', 'series/popular', 'movies/upcoming', 'series/upcoming', 'movies/top-rated', 'series/top-rated'];
