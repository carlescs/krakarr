import { Component, DestroyRef, ElementRef, computed, effect, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { AgoPipe } from '../../../core/format';
import { DashboardRecent, MediaType } from '../../../core/models';
import { Poster } from '../../../shared/poster/poster';

/** Posters on one page of the panel: two rows of three. */
export const PAGE_SIZE = 6;
/** Horizontal drag, in pixels, that turns the page. Shorter drags snap back (and still count as clicks). */
export const SWIPE_THRESHOLD = 40;

type Tab = 'imported' | 'added' | 'played';

/**
 * Dashboard panel with the latest imports, additions and (when a media server reports play status) plays, one tab
 * each, paged by dots or by swiping.
 */
@Component({
  selector: 'kr-recent',
  imports: [RouterLink, AgoPipe, Poster],
  templateUrl: './recent.html',
  styleUrl: './recent.scss',
  host: { class: 'block' },
})
export class Recent {
  readonly imported = input.required<DashboardRecent[]>();
  readonly added = input.required<DashboardRecent[]>();
  /** Titles played last on the media servers; null hides the tab (no server set up to report play status). */
  readonly played = input<DashboardRecent[] | null>(null);

  protected readonly tabs = computed(() => [
    { id: 'imported' as const, label: 'Imported' },
    { id: 'added' as const, label: 'Added' },
    ...(this.played() ? [{ id: 'played' as const, label: 'Played' }] : []),
  ]);
  protected readonly selected = signal<Tab>('imported');
  /** The selected tab, back on Imported when the Played tab goes away. */
  protected readonly tab = computed<Tab>(() => (this.selected() === 'played' && !this.played() ? 'imported' : this.selected()));
  protected readonly page = signal(0);
  /** How far the track follows the pointer during a swipe, in pixels. */
  protected readonly dragOffset = signal(0);

  protected readonly items = computed(() => {
    const tab = this.tab();
    return tab === 'imported' ? this.imported() : tab === 'added' ? this.added() : (this.played() ?? []);
  });
  protected readonly pages = computed(() => {
    const items = this.items();
    const pages: DashboardRecent[][] = [];
    for (let i = 0; i < items.length; i += PAGE_SIZE) pages.push(items.slice(i, i + PAGE_SIZE));
    return pages;
  });

  private start: { id: number; x: number; y: number } | null = null;
  private horizontal = false;
  /** Set after a swipe so the click that ends it doesn't open a poster. */
  private swiped = false;

  constructor() {
    // Capture phase, so the click that ends a swipe is stopped before a poster link handles it.
    const host = inject<ElementRef<HTMLElement>>(ElementRef).nativeElement;
    const onClick = (event: MouseEvent) => this.onClick(event);
    host.addEventListener('click', onClick, true);
    inject(DestroyRef).onDestroy(() => host.removeEventListener('click', onClick, true));

    // A reload can leave fewer pages than before.
    effect(() => {
      const last = Math.max(0, this.pages().length - 1);
      if (this.page() > last) this.page.set(last);
    });
  }

  protected selectTab(tab: Tab) {
    this.selected.set(tab);
    this.page.set(0);
  }

  protected goTo(page: number) {
    this.page.set(Math.min(Math.max(page, 0), this.pages().length - 1));
  }

  /** S01E02 for the last episode played of a series. */
  protected episode(r: DashboardRecent) {
    if (r.seasonNumber == null || r.episodeNumber == null) return null;
    return `S${String(r.seasonNumber).padStart(2, '0')}E${String(r.episodeNumber).padStart(2, '0')}`;
  }

  protected playedTitle(r: DashboardRecent) {
    const episode = this.episode(r);
    return `${r.title} (${r.year})${episode ? ' · ' + episode : ''} · ${r.finished ? 'watched' : 'part-way watched'} on ${r.server}`;
  }

  protected link(mediaType: MediaType, id: number): unknown[] {
    return [mediaType === 'Movie' ? '/movies' : '/series', id];
  }

  protected onPointerDown(event: PointerEvent) {
    if (this.pages().length < 2 || (event.pointerType === 'mouse' && event.button !== 0)) return;
    this.start = { id: event.pointerId, x: event.clientX, y: event.clientY };
    this.horizontal = false;
    this.swiped = false;
  }

  protected onPointerMove(event: PointerEvent) {
    if (!this.start || event.pointerId !== this.start.id) return;
    const dx = event.clientX - this.start.x;
    if (!this.horizontal) {
      // Decide once whether this is a swipe; vertical drags are left to the page's scrolling.
      if (Math.abs(dx) < 8) return;
      if (Math.abs(event.clientY - this.start.y) > Math.abs(dx)) { this.start = null; return; }
      this.horizontal = true;
      (event.currentTarget as Element).setPointerCapture?.(event.pointerId);
    }
    // Resist past the first and last page.
    const atEdge = (dx > 0 && this.page() === 0) || (dx < 0 && this.page() === this.pages().length - 1);
    this.dragOffset.set(atEdge ? dx / 3 : dx);
  }

  protected onPointerUp(event: PointerEvent) {
    if (!this.start || event.pointerId !== this.start.id) return;
    const dx = event.clientX - this.start.x;
    if (this.horizontal) {
      // The click that ends a swipe, if the browser sends one, follows straight after this event.
      this.swiped = true;
      setTimeout(() => (this.swiped = false));
      if (dx <= -SWIPE_THRESHOLD) this.goTo(this.page() + 1);
      else if (dx >= SWIPE_THRESHOLD) this.goTo(this.page() - 1);
    }
    this.start = null;
    this.horizontal = false;
    this.dragOffset.set(0);
  }

  private onClick(event: MouseEvent) {
    if (!this.swiped || !(event.target as Element).closest('[aria-roledescription="carousel"]')) return;
    this.swiped = false;
    event.preventDefault();
    event.stopPropagation();
  }
}
