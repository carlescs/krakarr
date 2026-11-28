import { Component, ElementRef, afterNextRender, computed, effect, inject, input, signal, untracked, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { Api, errorMessage } from '../../core/api';
import { Auth } from '../../core/auth';
import { movieItems, seriesItems } from '../../core/discover';
import { DiscoverItem, KeywordLookup, PersonLookup } from '../../core/models';
import { DiscoverAdd } from '../../shared/discover-add/discover-add';
import { discoverKey, detailRoute, libraryRoute } from '../../shared/media-card/media-card';
import { MediaRow } from '../../shared/media-row/media-row';
import { Poster } from '../../shared/poster/poster';

export type SearchTab = 'all' | 'movies' | 'series' | 'people' | 'keywords';
const TABS: readonly SearchTab[] = ['all', 'movies', 'series', 'people', 'keywords'];

/** One kind of result: null until searched, then the hits or why the lookup failed. */
interface Section<T> { items: T[]; error: string | null; }

/** How many people and keywords the "All" tab previews; the tabs list them all. */
const PREVIEW = 12;

/**
 * One search box for everything TMDB (and TheTVDB, for series) knows: movies, series, people and keywords, looked
 * up in parallel. "All" previews each kind; the tabs list one kind in full. The term and tab live in the URL so a
 * search survives going back from a result.
 */
@Component({
  selector: 'kr-unified-search',
  imports: [FormsModule, RouterLink, Poster, MediaRow, DiscoverAdd],
  templateUrl: './unified-search.html',
  styleUrl: './unified-search.scss',
})
export class UnifiedSearchPage {
  private readonly api = inject(Api);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  protected readonly auth = inject(Auth);

  /** Query parameters, bound by the router. */
  readonly q = input<string>();
  readonly type = input<string>();

  protected readonly key = discoverKey;
  protected readonly detailRoute = detailRoute;
  protected readonly libraryRoute = libraryRoute;
  protected readonly preview = PREVIEW;

  protected term = '';
  private readonly termInput = viewChild<ElementRef<HTMLInputElement>>('termInput');
  protected readonly tab = signal<SearchTab>('all');
  protected readonly searched = signal('');
  protected readonly searching = signal(false);
  protected readonly movies = signal<Section<DiscoverItem> | null>(null);
  protected readonly series = signal<Section<DiscoverItem> | null>(null);
  protected readonly people = signal<Section<PersonLookup> | null>(null);
  protected readonly keywords = signal<Section<KeywordLookup> | null>(null);

  protected readonly tabs = computed(() => [
    { id: 'all' as const, label: 'All', count: null },
    { id: 'movies' as const, label: 'Movies', count: this.movies()?.items.length ?? null },
    { id: 'series' as const, label: 'Series', count: this.series()?.items.length ?? null },
    { id: 'people' as const, label: 'People', count: this.people()?.items.length ?? null },
    { id: 'keywords' as const, label: 'Keywords', count: this.keywords()?.items.length ?? null },
  ]);

  /** Every lookup came back empty (and none failed). */
  protected readonly nothing = computed(() => {
    const sections = [this.movies(), this.series(), this.people(), this.keywords()];
    return sections.every(s => s && s.items.length === 0 && !s.error);
  });

  /** Error worth a settings link: TMDB or TheTVDB not configured or unreachable. */
  protected readonly setupError = computed(() =>
    [this.movies(), this.series(), this.people(), this.keywords()].map(s => s?.error).find(e => e && /TMDB|TheTVDB/.test(e)) ?? null);

  /** The title being added or requested, and the titles requested from this page (plain users). */
  protected readonly adding = signal<DiscoverItem | null>(null);
  protected readonly requested = signal<ReadonlySet<string>>(new Set());

  /** Bumped on every search; responses for an older one are dropped. */
  private generation = 0;

  constructor() {
    // `autofocus` only applies on a full page load, not when the router (e.g. the top bar's search button) brings us here.
    afterNextRender(() => { if (!this.q()) this.termInput()?.nativeElement.focus(); });
    // URL → state: on first load, and on back/forward.
    effect(() => {
      const q = (this.q() ?? '').trim();
      const type = this.type();
      untracked(() => {
        this.tab.set(TABS.includes(type as SearchTab) ? type as SearchTab : 'all');
        if (q && q !== this.searched()) {
          this.term = q;
          this.run(q);
        }
      });
    });
  }

  search() {
    const term = this.term.trim();
    if (!term) return;
    this.run(term);
    this.navigate();
  }

  protected setTab(tab: SearchTab) {
    if (tab === this.tab()) return;
    this.tab.set(tab);
    this.navigate();
  }

  protected initials(name: string) {
    return name.split(/\s+/).filter(Boolean).slice(0, 2).map(p => p[0]).join('').toUpperCase();
  }

  protected isRequested(d: DiscoverItem) {
    return this.requested().has(discoverKey(d));
  }

  protected markAdded(d: DiscoverItem) {
    this.adding.set(null);
    const replace = (s: Section<DiscoverItem> | null) => s && { ...s, items: s.items.map(x => discoverKey(x) === discoverKey(d) ? d : x) };
    if (d.kind === 'movie') this.movies.update(replace);
    else this.series.update(replace);
  }

  protected markRequested() {
    const d = this.adding();
    this.adding.set(null);
    if (d) this.requested.update(keys => new Set(keys).add(discoverKey(d)));
  }

  private async run(term: string) {
    const generation = ++this.generation;
    this.searched.set(term);
    this.searching.set(true);
    for (const s of [this.movies, this.series, this.people, this.keywords]) s.set(null);

    const settle = async <T, R>(lookup: Promise<T[]>, map: (items: T[]) => R[], target: { set(v: Section<R>): void }) => {
      let section: Section<R>;
      try {
        section = { items: map(await lookup), error: null };
      } catch (e) {
        section = { items: [], error: errorMessage(e) };
      }
      if (generation === this.generation) target.set(section);
    };
    await Promise.all([
      settle(this.api.lookupMovies(term), movieItems, this.movies),
      settle(this.api.lookupSeries(term), seriesItems, this.series),
      settle(this.api.lookupPeople(term), x => x, this.people),
      settle(this.api.lookupKeywords(term), x => x, this.keywords),
    ]);
    if (generation === this.generation) this.searching.set(false);
  }

  private navigate() {
    this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { q: this.searched() || null, type: this.tab() === 'all' ? null : this.tab() },
      queryParamsHandling: 'merge',
    });
  }
}
