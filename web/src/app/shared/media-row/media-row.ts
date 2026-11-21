import { Component, ElementRef, computed, input, signal, viewChild } from '@angular/core';
import { RouterLink } from '@angular/router';
import { DiscoverItem } from '../../core/models';
import { DiscoverAdd } from '../discover-add/discover-add';
import { MediaCard, discoverKey } from '../media-card/media-card';

/** Horizontally scrolling row of discovered titles (Seerr-style slider) with an optional "see all" link. */
@Component({
  selector: 'kr-media-row',
  imports: [RouterLink, MediaCard, DiscoverAdd],
  templateUrl: './media-row.html',
  styleUrl: './media-row.scss',
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
