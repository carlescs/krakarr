import { Component, ElementRef, computed, effect, inject, input, model, signal } from '@angular/core';
import { SearchIndexer } from '../../core/models';

/**
 * Multi-select popover for picking which enabled indexers a manual search queries.
 * An empty `selected` array means all enabled indexers.
 */
@Component({
  selector: 'kr-indexer-picker',
  host: {
    class: 'relative block',
    '(document:click)': 'onDocumentClick($event)',
    '(document:keydown.escape)': 'close()',
  },
  templateUrl: './indexer-picker.html',
  styleUrl: './indexer-picker.scss',
})
export class IndexerPicker {
  readonly indexers = input.required<SearchIndexer[]>();
  /** Selected indexer ids; empty means all enabled indexers. */
  readonly selected = model<number[]>([]);
  readonly disabled = input(false);

  protected readonly open = signal(false);
  protected readonly filter = signal('');
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  private readonly selectedSet = computed(() => new Set(this.selected()));
  protected readonly selectedIndexers = computed(() => this.indexers().filter(i => this.selectedSet().has(i.id)));
  protected readonly showFilter = computed(() => this.indexers().length > 6);
  protected readonly filtered = computed(() => {
    const q = this.filter().trim().toLowerCase();
    return q ? this.indexers().filter(i => i.name.toLowerCase().includes(q)) : this.indexers();
  });
  protected readonly label = computed(() => {
    const picked = this.selectedIndexers();
    if (picked.length === 0) return 'All indexers';
    if (picked.length === 1) return picked[0].name;
    return `${picked.length} indexers`;
  });
  protected readonly title = computed(() => {
    const picked = this.selectedIndexers();
    return picked.length > 0 ? picked.map(i => i.name).join(', ') : 'All enabled indexers';
  });

  constructor() {
    effect(() => { if (this.disabled()) this.close(); });
  }

  protected toggleOpen() {
    if (this.open()) this.close();
    else this.open.set(true);
  }

  protected close() {
    if (!this.open()) return;
    this.open.set(false);
    this.filter.set('');
  }

  protected onDocumentClick(event: MouseEvent) {
    if (this.open() && !this.host.nativeElement.contains(event.target as Node)) this.close();
  }

  protected isSelected(id: number): boolean { return this.selectedSet().has(id); }

  protected toggle(id: number) {
    const current = this.selected();
    this.selected.set(current.includes(id) ? current.filter(x => x !== id) : [...current, id]);
  }

  protected clear() { this.selected.set([]); }
}
