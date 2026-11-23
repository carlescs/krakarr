import { Component, DestroyRef, OnInit, computed, effect, inject, input, model, signal } from '@angular/core';
import { Api, DownloadLink } from '../../core/api';

/** A library item a manual download can be linked to. */
export interface MediaLink { kind: 'movie' | 'series'; id: number; title: string; year: number; }

/** Lower-case words without accents or punctuation: "Amélie (2001)" → "amelie 2001". */
export function normalizeTitle(s: string): string {
  return s.normalize('NFD').replace(/\p{M}/gu, '').toLowerCase().replace(/&/g, ' and ').replace(/[^a-z0-9]+/g, ' ').trim();
}

/** Items whose title contains what the user typed; titles starting with it first. */
export function filterLinks(items: MediaLink[], text: string, limit = 8): MediaLink[] {
  const q = normalizeTitle(text);
  if (!q) return [];
  const scored = items
    .map(item => ({ item, title: normalizeTitle(item.title) }))
    .filter(x => x.title.includes(q) || `${x.title} ${x.item.year}`.includes(q))
    .sort((a, b) => Number(b.title.startsWith(q)) - Number(a.title.startsWith(q)) || a.title.localeCompare(b.title));
  return scored.slice(0, limit).map(x => x.item);
}

/**
 * Items a release name probably belongs to: release names start with the title ("The.Office.US.S02E01…").
 * Longer (more specific) titles and matching years rank first.
 */
export function suggestLinks(items: MediaLink[], releaseName: string, limit = 4): MediaLink[] {
  const name = ` ${normalizeTitle(releaseName)} `;
  if (!name.trim()) return [];
  return items
    .map(item => ({ item, title: normalizeTitle(item.title) }))
    .filter(x => x.title && name.startsWith(` ${x.title} `))
    .map(x => ({ ...x, score: x.title.length + (x.item.year > 0 && name.includes(` ${x.item.year} `) ? 100 : 0) }))
    .sort((a, b) => b.score - a.score)
    .slice(0, limit)
    .map(x => x.item);
}

const INITIAL_PAGE_SIZE = 200;
const SEARCH_PAGE_SIZE = 20;
const SEARCH_DEBOUNCE_MS = 200;

/** Optional "link this download to a movie or series" field for manual downloads. */
@Component({
  selector: 'kr-media-link-picker',
  host: { class: 'block' },
  templateUrl: './media-link-picker.html',
  styleUrl: './media-link-picker.scss',
})
export class MediaLinkPicker implements OnInit {
  private static nextId = 0;
  private readonly api = inject(Api);
  /** Release or torrent name used to suggest items before the user types. */
  readonly hint = input('');
  readonly link = model<MediaLink | null>(null);
  protected readonly inputId = `media-link-${MediaLinkPicker.nextId++}`;
  protected readonly text = signal('');
  protected readonly loading = signal(true);
  private readonly items = signal<MediaLink[]>([]);
  private readonly remoteItems = signal<MediaLink[]>([]);
  private moviesIncomplete = false;
  private seriesIncomplete = false;
  private searchGen = 0;
  private searchTimer?: ReturnType<typeof setTimeout>;

  protected readonly options = computed(() => {
    const query = this.text().trim();
    if (!query) return suggestLinks(this.items(), this.hint());
    const combined = new Map<string, MediaLink>();
    for (const item of this.items()) combined.set(`${item.kind}:${item.id}`, item);
    for (const item of this.remoteItems()) combined.set(`${item.kind}:${item.id}`, item);
    return filterLinks([...combined.values()], query);
  });

  constructor() {
    const destroyRef = inject(DestroyRef);
    destroyRef.onDestroy(() => clearTimeout(this.searchTimer));

    effect(() => {
      const q = this.text().trim();
      clearTimeout(this.searchTimer);
      if (!q || (!this.moviesIncomplete && !this.seriesIncomplete)) {
        this.remoteItems.set([]);
        return;
      }
      const gen = ++this.searchGen;
      this.searchTimer = setTimeout(() => {
        void this.searchRemote(q, gen);
      }, SEARCH_DEBOUNCE_MS);
    });
  }

  async ngOnInit() {
    const empty = { items: [], page: 1, pageSize: INITIAL_PAGE_SIZE, total: 0, libraryTotal: 0 };
    const [movies, series] = await Promise.all([
      this.api.movies({ pageSize: INITIAL_PAGE_SIZE }).catch(() => empty),
      this.api.seriesList({ pageSize: INITIAL_PAGE_SIZE }).catch(() => empty),
    ]);
    this.moviesIncomplete = movies.total > movies.items.length;
    this.seriesIncomplete = series.total > series.items.length;
    this.items.set([
      ...movies.items.map(m => ({ kind: 'movie' as const, id: m.id, title: m.title, year: m.year })),
      ...series.items.map(s => ({ kind: 'series' as const, id: s.id, title: s.title, year: s.year })),
    ]);
    this.loading.set(false);
  }

  private async searchRemote(q: string, gen: number) {
    const empty = { items: [], page: 1, pageSize: SEARCH_PAGE_SIZE, total: 0, libraryTotal: 0 };
    const [movies, series] = await Promise.all([
      this.moviesIncomplete ? this.api.movies({ q, pageSize: SEARCH_PAGE_SIZE }).catch(() => empty) : Promise.resolve(empty),
      this.seriesIncomplete ? this.api.seriesList({ q, pageSize: SEARCH_PAGE_SIZE }).catch(() => empty) : Promise.resolve(empty),
    ]);
    if (gen !== this.searchGen) return;
    this.remoteItems.set([
      ...movies.items.map(m => ({ kind: 'movie' as const, id: m.id, title: m.title, year: m.year })),
      ...series.items.map(s => ({ kind: 'series' as const, id: s.id, title: s.title, year: s.year })),
    ]);
  }

  protected pick(o: MediaLink) {
    this.link.set(o);
    this.text.set('');
  }

  protected badgeClass(l: MediaLink) {
    return l.kind === 'movie' ? 'bg-accent-500/15 text-accent-300' : 'bg-violet-500/15 text-violet-300';
  }
}

/** Request fields for a link (what the download endpoints accept). */
export function linkFields(link: MediaLink | null): DownloadLink {
  if (!link) return {};
  return link.kind === 'movie' ? { movieId: link.id } : { seriesId: link.id };
}
