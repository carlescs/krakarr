import { ScrollingModule } from '@angular/cdk/scrolling';
import { Component, DestroyRef, ElementRef, Injector, OnInit, computed, effect, inject, signal, untracked } from '@angular/core';
import { DatePipe, NgTemplateOutlet } from '@angular/common';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { Api, errorMessage } from '../../core/api';
import { Auth } from '../../core/auth';
import { Events } from '../../core/events';
import { Language, SubtitleProvider, SubtitleSettings, WantedSubtitle, WantedSubtitlePage, WantedSubtitleQuery, WantedSubtitleSortKey } from '../../core/models';
import { PagedListCache, VirtualRowDataSource } from '../../core/paged-list';
import { Toasts } from '../../core/toast';
import { observeContainerWidth } from '../../core/virtual-scroll';
import { MultiPicker, PickerOption } from '../../shared/multi-picker/multi-picker';
import { SubtitleManager } from '../../shared/subtitle-manager/subtitle-manager';
import { TableSort } from '../../core/table-sort';
import { SortHeader } from '../../shared/sort-header/sort-header';

/** Which wanted files to list: all of them, those still searched automatically, or those marked "not found". */
export type WantedFilter = 'all' | 'searching' | 'notFound';

/** A wanted file with a list id: movie file ids as they are, episode file ids negated so the two never collide. */
type WantedRow = WantedSubtitle & { id: number };
type WantedListQuery = Omit<WantedSubtitleQuery, 'notFound'> & { filter: WantedFilter };
type WantedPageRows = Omit<WantedSubtitlePage, 'items'> & { items: WantedRow[] };

/** Table rows are a fixed height so the list can be virtualized: one line on wider screens, title plus badges on phones. */
const ROW_HEIGHT = 44;
const MOBILE_ROW_HEIGHT = 64;
const SM_BREAKPOINT = 640;

function notFoundParam(filter: WantedFilter) { return filter === 'all' ? undefined : filter === 'notFound'; }

@Component({
  selector: 'kr-subtitles',
  imports: [ScrollingModule, RouterLink, DatePipe, NgTemplateOutlet, MultiPicker, SubtitleManager, SortHeader],
  templateUrl: './subtitles.html',
  styleUrl: './subtitles.scss',
})
export class SubtitlesPage implements OnInit {
  private readonly auth = inject(Auth);
  private readonly api = inject(Api);
  private readonly toasts = inject(Toasts);
  private readonly events = inject(Events);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly settings = signal<SubtitleSettings | null>(null);
  protected readonly providers = signal<SubtitleProvider[]>([]);
  protected readonly availableProviders = computed<PickerOption<string>[]>(() =>
    this.providers().filter(p => p.configured).map(p => ({ id: p.name, name: p.displayName || p.name })));
  protected readonly selectedProviders = signal<string[]>([]);
  /** Files marked "not found" are hidden by default: nothing will happen to them until they are retried. */
  protected readonly filter = signal<WantedFilter>('searching');
  protected readonly filters: { id: WantedFilter; label: string; title: string }[] = [
    { id: 'searching', label: 'Searching', title: 'Files the scheduled search still retries' },
    { id: 'notFound', label: 'Not found', title: 'Files no provider had subtitles for, or marked by hand; not searched automatically until retried' },
    { id: 'all', label: 'All', title: 'Every file missing a wanted language' },
  ];
  /** Sort state shared with `SortHeader`; the server sorts, so the source here stays empty. */
  protected readonly sort = new TableSort<WantedRow, WantedSubtitleSortKey>(signal<WantedRow[]>([]), {
    type: 'source',
    title: 'source',
    missing: { value: () => null, desc: true },
  });
  /** Keeps the filter by name: the cache's query key ignores `false`, so `notFound: false` would read as "all". */
  private readonly query = computed<WantedListQuery>(() => {
    const { key, desc } = this.sort.state();
    return { filter: this.filter(), sort: key ?? undefined, desc: key && desc ? true : undefined };
  });

  /** Pages of the wanted list, loaded as the table scrolls and dropped again when far away (bounded). */
  private readonly cache = new PagedListCache<WantedRow, WantedListQuery, WantedPageRows>(
    untracked(() => this.query()),
    async ({ filter, ...q }, page, pageSize) => {
      const notFound = notFoundParam(filter);
      const res = await this.api.wantedSubtitles({ ...(notFound != null ? { notFound } : {}), ...q, page, pageSize });
      return { ...res, items: res.items.map(w => ({ ...w, id: w.movieFileId ?? -(w.episodeFileId ?? 0) })) };
    },
    e => this.toasts.error(errorMessage(e)),
    undefined,
    500, // a bulk search publishes one event per file: refresh once things settle
  );
  protected readonly loading = this.cache.loading;
  /** Total for the selected filter. */
  protected readonly total = this.cache.total;
  /** Total for a filter, reported with every page. */
  protected count(filter: WantedFilter): number | null {
    const page = this.cache.meta();
    if (!page) return null;
    return filter === 'searching' ? page.searching : filter === 'notFound' ? page.notFound : page.libraryTotal;
  }

  private readonly viewportWidth = signal(typeof window !== 'undefined' ? window.innerWidth : 1024);
  protected readonly rowHeight = computed(() => (this.viewportWidth() < SM_BREAKPOINT ? MOBILE_ROW_HEIGHT : ROW_HEIGHT));
  /** One virtual row per wanted file: its index in the list. */
  private readonly rows = computed(() => Array.from({ length: this.total() }, (_, i) => i));
  protected readonly dataSource: VirtualRowDataSource<number>;

  protected readonly languages = signal<Language[]>([]);
  protected readonly manage = signal<WantedSubtitle | null>(null);
  /** Managers can't read settings; for them the server reports a missing provider when they search. */
  protected readonly ready = computed(() => {
    if (!this.auth.isAdmin()) return true;
    const s = this.settings();
    return !!s?.enabled && ((s.openSubtitles?.enabled !== false && !!s.openSubtitles?.apiKey) || (s.subdl?.enabled !== false && !!s.subdl?.apiKey));
  });

  constructor() {
    const host = inject(ElementRef<HTMLElement>);
    this.dataSource = new VirtualRowDataSource(this.rows, (rows, start, end) => [rows[start], rows[end]],
      (start, end) => this.cache.ensureRange(start, end), inject(Injector));
    this.destroyRef.onDestroy(observeContainerWidth(host.nativeElement, (_, vw) => this.viewportWidth.set(vw)));
    this.destroyRef.onDestroy(() => this.dataSource.destroy());

    effect(() => {
      if (this.cache.setQuery(this.query()) && typeof window !== 'undefined' && window.scrollY > 0) window.scrollTo({ top: 0 });
    });
  }

  async ngOnInit() {
    this.api.subtitleProviders().then(list => this.providers.set(list)).catch(() => { /* search still queries all configured providers */ });
    const [s, langs] = await Promise.all([
      this.auth.isAdmin() ? this.api.settings('subtitles') : Promise.resolve(null), this.api.subtitleLanguages(),
    ]);
    this.settings.set(s);
    this.languages.set(langs);
    this.destroyRef.onDestroy(this.cache.attach());
    this.events.subtitlesUpdated.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(() => this.cache.invalidate());
  }

  /** Reloads the loaded pages in place (after a change made here). */
  load() { return this.cache.reload(); }

  protected wantedAt(index: number): WantedRow | undefined { return this.cache.at(index); }

  protected trackRow(_: number, index: number) { return index; }

  protected setFilter(filter: WantedFilter) { this.filter.set(filter); }

  async retry(w?: WantedSubtitle) {
    try {
      const { count } = await this.api.retrySubtitles(w ? { movieFileId: w.movieFileId, episodeFileId: w.episodeFileId } : {});
      this.toasts.show(w ? `${w.label} will be searched again shortly` : `${count} file(s) will be searched again shortly`);
      await this.load();
    } catch (e) { this.toasts.error(errorMessage(e)); }
  }

  /** Stops the scheduled search for every movie and episode still missing subtitles, until they are retried. */
  async markAllNotFound() {
    const n = this.count('searching');
    if (!confirm(`Mark ${n ?? 'all'} file(s) missing subtitles as not found? They won't be searched automatically until you retry them.`)) return;
    try {
      const { count } = await this.api.markAllSubtitlesNotFound();
      this.toasts.show(`${count} file(s) marked as not found`);
      await this.load();
    } catch (e) { this.toasts.error(errorMessage(e)); }
  }

  protected languageNames(codes: string[]) {
    return codes.map(c => this.languages().find(l => l.code === c)?.name ?? c).join(', ');
  }

  async search(w: WantedSubtitle) {
    await this.api.searchSubtitles({ movieFileId: w.movieFileId, episodeFileId: w.episodeFileId });
    this.toasts.show(`Searching subtitles for ${w.label}…`);
  }

  async searchAll() {
    const allowed = new Set(this.availableProviders().map(p => p.id));
    const providers = this.selectedProviders().filter(id => allowed.has(id));
    const notFound = notFoundParam(this.filter());
    await this.api.searchSubtitles({ ...(providers.length > 0 ? { providers } : {}), ...(notFound != null ? { notFound } : {}) });
    const names = this.availableProviders().filter(p => providers.includes(p.id)).map(p => p.name);
    this.toasts.show(names.length > 0
      ? `Searching subtitles for all wanted files (${names.join(', ')})…`
      : 'Searching subtitles for all wanted files…');
  }
}
