import { Component, DestroyRef, computed, inject, linkedSignal, signal } from '@angular/core';
import { DatePipe, NgTemplateOutlet } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Auth } from '../../../core/auth';
import { Library } from '../../../core/library';
import { Subscriptions } from '../../../core/subscriptions';
import { SeriesListItem } from '../../../core/models';
import { NearViewport } from '../../../shared/near-viewport/near-viewport';
import { Poster } from '../../../shared/poster/poster';
import { GRID_CHUNK, POSTER_GRID, PosterGridSkeleton } from '../../../shared/poster-grid/poster-grid';
import { MoreMenu } from '../../../shared/more-menu/more-menu';
import { Progress } from '../../../shared/progress/progress';
import { BulkActions } from '../../../shared/bulk-actions/bulk-actions';
import { ViewToggle } from '../../../shared/view-toggle/view-toggle';
import { Selection, ViewMode, storeView, storedView } from '../../../core/selection';

type Filter = 'all' | 'following' | 'missing' | 'continuing' | 'ended' | 'unmonitored';
type SortKey = 'title' | 'year' | 'episodes' | 'nextAiring' | 'status';

const VIEW_KEY = 'krakarr.series.view';

@Component({
  selector: 'kr-series-list',
  imports: [RouterLink, FormsModule, DatePipe, NgTemplateOutlet, Poster, Progress, NearViewport, PosterGridSkeleton, MoreMenu, BulkActions, ViewToggle],
  templateUrl: './series-list.html',
  styleUrl: './series-list.scss',
})
export class SeriesListPage {
  protected readonly auth = inject(Auth);
  private readonly cache = inject(Library).series;
  private readonly subscriptions = inject(Subscriptions);

  protected readonly grid = POSTER_GRID;
  protected readonly loading = computed(() => this.cache.items() === null);
  protected readonly series = computed(() => this.cache.items() ?? []);
  protected readonly query = signal('');
  protected readonly filter = signal<Filter>('all');
  protected readonly view = signal<ViewMode>(storedView(VIEW_KEY));
  /** Grid selection mode: cards toggle selection instead of opening the series. The table always has checkboxes. */
  protected readonly selecting = signal(false);
  protected readonly sort = signal<{ key: SortKey; desc: boolean }>({ key: 'title', desc: false });
  protected readonly following = this.subscriptions.seriesIds;

  protected readonly filtered = computed(() => {
    const q = this.query().toLowerCase().trim();
    const filter = this.filter();
    const following = filter === 'following' ? this.following() : null;
    return this.series().filter(s => {
      if (q && !s.title.toLowerCase().includes(q)) return false;
      switch (filter) {
        case 'following': return following!.has(s.id);
        case 'missing': return s.monitored && s.episodeFileCount < s.episodeCount;
        case 'continuing': return s.status !== 'Ended';
        case 'ended': return s.status === 'Ended';
        case 'unmonitored': return !s.monitored;
        default: return true;
      }
    });
  });

  /** The filtered list in the chosen sort order (the API sorts by title). */
  protected readonly sorted = computed(() => {
    const { key, desc } = this.sort();
    const list = this.filtered();
    if (key === 'title') return desc ? [...list].reverse() : list;
    const value: (s: SeriesListItem) => number =
      key === 'year' ? s => s.year || 9999
      : key === 'episodes' ? s => this.percent(s)
      : key === 'status' ? s => s.status === 'Ended' ? 1 : 0
      // Titles with no upcoming episode go last.
      : s => s.nextAiring ? Date.parse(s.nextAiring) : Number.MAX_SAFE_INTEGER;
    const sign = desc ? -1 : 1;
    // Array sort is stable: ties keep the title order.
    return [...list].sort((a, b) => sign * (value(a) - value(b)));
  });

  /** Items rendered so far: more are added as the end of the list scrolls into view, and a new filter starts over. */
  private readonly limit = linkedSignal({ source: () => [this.query(), this.filter(), this.sort()], computation: () => GRID_CHUNK });
  protected readonly visible = computed(() => this.sorted().slice(0, this.limit()));

  protected readonly selection = new Selection(this.sorted);
  /** The bulk toolbar shows while something is selected in the table, and throughout grid selection mode. */
  protected readonly showBulk = computed(() => this.view() === 'table' ? this.selection.count() > 0 : this.selecting());

  constructor() {
    void this.subscriptions.refresh();
    inject(DestroyRef).onDestroy(this.cache.attach());
  }

  protected percent(s: SeriesListItem) { return s.episodeCount === 0 ? 0 : (s.episodeFileCount / s.episodeCount) * 100; }

  protected showMore() { this.limit.update(n => n + GRID_CHUNK); }

  protected setView(mode: ViewMode) {
    this.view.set(mode);
    storeView(VIEW_KEY, mode);
    // Keep a selection made in the table visible (and actionable) in the grid.
    this.selecting.set(mode === 'grid' && this.selection.count() > 0);
  }

  protected stopSelecting() {
    this.selecting.set(false);
    this.selection.clear();
  }

  /** Grid card click (or Enter/Space): toggles the series in selection mode (Shift selects a range), otherwise follows the link. */
  protected cardClick(event: Event, s: SeriesListItem) {
    if (!this.selecting()) return;
    event.preventDefault();
    this.selection.toggle(s.id, (event as MouseEvent | KeyboardEvent).shiftKey);
  }

  protected sortBy(key: SortKey) {
    this.sort.update(s => ({ key, desc: s.key === key ? !s.desc : false }));
  }

  protected ariaSort(key: SortKey): 'ascending' | 'descending' | 'none' {
    const s = this.sort();
    return s.key !== key ? 'none' : s.desc ? 'descending' : 'ascending';
  }

  protected progressColor(s: SeriesListItem) {
    return !s.monitored ? 'bg-slate-500' : s.episodeFileCount >= s.episodeCount ? 'bg-emerald-500' : 'bg-rose-500';
  }
}
