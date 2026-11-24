import { Component, computed, effect, inject, input, signal, untracked } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Api, errorMessage } from '../../core/api';
import { interleave, movieItems, seriesItems } from '../../core/discover';
import { DiscoverItem, KeywordLookup } from '../../core/models';
import { DiscoverAdd } from '../../shared/discover-add/discover-add';
import { MediaCard, discoverKey } from '../../shared/media-card/media-card';

export type KeywordMedia = 'all' | 'movies' | 'series';

/** Matches the server's cap: more keywords combined with AND rarely match anything. */
export const MAX_KEYWORDS = 10;

/** Distinct positive ids from the `keywords` query parameter ("4379,9882"). */
export function parseKeywordIds(value: string | null | undefined): number[] {
  const ids = (value ?? '').split(',').map(s => Number(s.trim())).filter(n => Number.isInteger(n) && n > 0);
  return [...new Set(ids)].slice(0, MAX_KEYWORDS);
}

/**
 * Finds movies and shows by TMDB keyword ("time travel", "heist"…): search keywords by name, pick one or more, and
 * browse the titles tagged with all of them. The picked keywords and media filter live in the URL so results can be
 * shared and survive going back from a title.
 */
@Component({
  selector: 'kr-keyword-search',
  imports: [FormsModule, RouterLink, MediaCard, DiscoverAdd],
  templateUrl: './keyword-search.html',
  styleUrl: './keyword-search.scss',
})
export class KeywordSearchPage {
  private readonly api = inject(Api);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  /** Query parameters, bound by the router. */
  readonly keywords = input<string>();
  readonly media = input<string>();

  protected readonly key = discoverKey;
  protected readonly maxKeywords = MAX_KEYWORDS;
  protected readonly filters: { id: KeywordMedia; label: string }[] = [
    { id: 'all', label: 'All' },
    { id: 'movies', label: 'Movies' },
    { id: 'series', label: 'Series' },
  ];

  protected term = '';
  protected readonly searching = signal(false);
  protected readonly searchError = signal<string | null>(null);
  protected readonly suggestions = signal<KeywordLookup[] | null>(null);

  protected readonly selected = signal<KeywordLookup[]>([]);
  protected readonly filter = signal<KeywordMedia>('all');
  protected readonly selectedIds = computed(() => new Set(this.selected().map(k => k.id)));

  protected readonly items = signal<DiscoverItem[]>([]);
  protected readonly page = signal(0);
  private readonly movieTotal = signal(1);
  private readonly seriesTotal = signal(1);
  protected readonly loading = signal(false);
  protected readonly error = signal<string | null>(null);
  protected readonly picked = signal<DiscoverItem | null>(null);

  protected readonly hasMore = computed(() => {
    const f = this.filter();
    const totals = [f !== 'series' ? this.movieTotal() : 0, f !== 'movies' ? this.seriesTotal() : 0];
    return this.page() > 0 && this.page() < Math.max(...totals);
  });

  /** Names seen in suggestions or resolved by id, so chips show names without asking TMDB again. */
  private readonly names = new Map<number, string>();
  /** Bumped whenever the query changes; responses for an older query are dropped. */
  private generation = 0;

  constructor() {
    // URL → state: on first load, and on back/forward or a shared link.
    effect(() => {
      const ids = parseKeywordIds(this.keywords());
      const media = this.media();
      untracked(() => {
        const filter: KeywordMedia = media === 'movies' || media === 'series' ? media : 'all';
        // Our own navigation echoes back here with the state it was made from: nothing to do.
        if (filter === this.filter() && sameIds(ids, this.selected().map(k => k.id))) return;
        this.filter.set(filter);
        this.selected.set(ids.map(id => ({ id, name: this.names.get(id) ?? '' })));
        this.resolveNames();
        this.restart();
      });
    });
  }

  async search() {
    const term = this.term.trim();
    if (!term) return;
    this.searching.set(true);
    this.searchError.set(null);
    try {
      const found = await this.api.lookupKeywords(term);
      for (const k of found) this.names.set(k.id, k.name);
      this.suggestions.set(found);
      if (found.length === 0) this.searchError.set('No keywords found.');
    } catch (e) {
      this.searchError.set(errorMessage(e));
    } finally {
      this.searching.set(false);
    }
  }

  protected toggle(k: KeywordLookup) {
    this.names.set(k.id, k.name);
    const current = this.selected();
    if (current.some(s => s.id === k.id)) this.select(current.filter(s => s.id !== k.id));
    else if (current.length < MAX_KEYWORDS) this.select([...current, k]);
  }

  protected clear() {
    this.select([]);
  }

  protected setFilter(media: KeywordMedia) {
    if (media === this.filter()) return;
    this.filter.set(media);
    this.restart();
    this.navigate();
  }

  async load(page: number) {
    const ids = this.selected().map(k => k.id);
    if (ids.length === 0) return;
    const generation = this.generation;
    const f = this.filter();
    const wantMovies = f !== 'series' && page <= this.movieTotal();
    const wantSeries = f !== 'movies' && page <= this.seriesTotal();
    this.loading.set(true);
    this.error.set(null);
    const [movies, series] = await Promise.allSettled([
      wantMovies ? this.api.moviesByKeywords(ids, page) : Promise.resolve(null),
      wantSeries ? this.api.seriesByKeywords(ids, page) : Promise.resolve(null),
    ]);
    if (generation !== this.generation) return;
    this.loading.set(false);

    const failure = [movies, series].find(r => r.status === 'rejected') as PromiseRejectedResult | undefined;
    const movieResult = movies.status === 'fulfilled' ? movies.value : null;
    const seriesResult = series.status === 'fulfilled' ? series.value : null;
    if (failure && !movieResult && !seriesResult) {
      this.error.set(errorMessage(failure.reason));
      return;
    }
    if (failure) this.error.set(errorMessage(failure.reason));
    // A failed side stops paging; the other keeps going.
    if (wantMovies) this.movieTotal.set(movieResult?.totalPages ?? 0);
    if (wantSeries) this.seriesTotal.set(seriesResult?.totalPages ?? 0);

    const fresh = interleave(movieItems(movieResult?.results ?? []), seriesItems(seriesResult?.results ?? []));
    // TMDB pages can overlap while popularity shifts; keep the first occurrence.
    const seen = new Set(this.items().map(discoverKey));
    this.items.update(items => [...items, ...fresh.filter(d => !seen.has(discoverKey(d)))]);
    this.page.set(page);
  }

  protected markAdded(d: DiscoverItem) {
    this.picked.set(null);
    this.items.update(items => items.map(x => discoverKey(x) === discoverKey(d) ? d : x));
  }

  private select(keywords: KeywordLookup[]) {
    this.selected.set(keywords);
    this.restart();
    this.navigate();
  }

  private restart() {
    this.generation++;
    this.items.set([]);
    this.page.set(0);
    this.movieTotal.set(1);
    this.seriesTotal.set(1);
    this.error.set(null);
    this.loading.set(false);
    this.load(1);
  }

  private navigate() {
    const ids = this.selected().map(k => k.id);
    this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { keywords: ids.length ? ids.join(',') : null, media: this.filter() === 'all' ? null : this.filter() },
      queryParamsHandling: 'merge',
    });
  }

  /** Fills in chip names for keywords that came from the URL. */
  private async resolveNames() {
    const missing = this.selected().filter(k => !k.name);
    await Promise.all(missing.map(async k => {
      try {
        const found = await this.api.keyword(k.id);
        this.names.set(k.id, found.name);
      } catch {
        this.names.set(k.id, `Keyword ${k.id}`);
      }
      this.selected.update(list => list.map(s => s.id === k.id ? { ...s, name: this.names.get(k.id)! } : s));
    }));
  }
}

function sameIds(a: readonly number[], b: readonly number[]): boolean {
  return a.length === b.length && a.every((id, i) => id === b[i]);
}
