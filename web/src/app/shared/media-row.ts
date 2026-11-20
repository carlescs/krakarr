import { Component, ElementRef, computed, input, signal, viewChild } from '@angular/core';
import { RouterLink } from '@angular/router';
import { DiscoverItem } from '../core/models';
import { DiscoverAdd } from './discover-add';
import { MediaCard, discoverKey } from './media-card';

/** Horizontally scrolling row of discovered titles (Seerr-style slider) with an optional "see all" link. */
@Component({
  selector: 'kr-media-row',
  imports: [RouterLink, MediaCard, DiscoverAdd],
  template: `
    <section class="mb-8">
      <div class="mb-3 flex items-center gap-3">
        <h2 class="text-lg font-semibold text-white">{{ title() }}</h2>
        @if (subtitle()) { <span class="hidden text-sm text-slate-500 sm:inline">{{ subtitle() }}</span> }
        <div class="flex-1"></div>
        @if (link(); as l) { <a [routerLink]="l" class="text-sm text-accent-300 hover:text-accent-200">See all →</a> }
        @if (shown().length > 0) {
          <div class="hidden gap-1 sm:flex">
            <button class="btn-ghost btn-sm" (click)="scroll(-1)" aria-label="Scroll left">‹</button>
            <button class="btn-ghost btn-sm" (click)="scroll(1)" aria-label="Scroll right">›</button>
          </div>
        }
      </div>
      @if (error()) {
        <p class="text-sm text-rose-300">{{ error() }}</p>
      } @else if (loading()) {
        <div class="flex gap-4 overflow-hidden">
          @for (i of placeholders; track i) { <div class="aspect-[2/3] w-32 shrink-0 animate-pulse rounded-lg bg-surface-800 sm:w-40"></div> }
        </div>
      } @else if (shown().length === 0) {
        <p class="text-sm text-slate-500">{{ empty() }}</p>
      } @else {
        <div #scroller class="-mx-1 -mt-1 flex snap-x gap-3 overflow-x-auto sm:gap-4 px-1 pt-1 pb-2 [scrollbar-width:thin]">
          @for (d of shown(); track key(d)) {
            <kr-media-card class="w-32 shrink-0 snap-start sm:w-40" [item]="d" (add)="selected.set($event)" />
          }
        </div>
      }
    </section>

    @if (selected(); as d) {
      <kr-discover-add [item]="d" (closed)="selected.set(null)" (added)="markAdded($event)" />
    }
  `,
})
export class MediaRow {
  readonly title = input.required<string>();
  readonly subtitle = input<string>('');
  readonly items = input<DiscoverItem[]>([]);
  readonly loading = input(false);
  readonly error = input<string | null>(null);
  readonly empty = input('Nothing to show.');
  readonly link = input<string[] | null>(null);

  protected readonly placeholders = [1, 2, 3, 4, 5, 6, 7, 8];
  protected readonly key = discoverKey;
  protected readonly selected = signal<DiscoverItem | null>(null);
  /** Items added from this row, keyed by discoverKey, until the parent reloads. */
  private readonly addedItems = signal(new Map<string, DiscoverItem>());
  private readonly scroller = viewChild<ElementRef<HTMLElement>>('scroller');

  protected readonly shown = computed(() => {
    const added = this.addedItems();
    return added.size === 0 ? this.items() : this.items().map(d => added.get(discoverKey(d)) ?? d);
  });

  protected markAdded(d: DiscoverItem) {
    this.selected.set(null);
    this.addedItems.update(m => new Map(m).set(discoverKey(d), d));
  }

  protected scroll(direction: number) {
    const el = this.scroller()?.nativeElement;
    el?.scrollBy({ left: direction * el.clientWidth * 0.8, behavior: 'smooth' });
  }
}
