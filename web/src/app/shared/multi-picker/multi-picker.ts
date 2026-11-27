import { Component, ElementRef, computed, effect, inject, input, model, signal } from '@angular/core';

export interface PickerOption<T extends string | number = string | number> {
  id: T;
  name: string;
}

/**
 * Multi-select popover for picking a subset of options (e.g. indexers or subtitle providers).
 * An empty `selected` array means all available options.
 */
@Component({
  selector: 'kr-multi-picker',
  host: {
    class: 'relative block',
    '(document:click)': 'onDocumentClick($event)',
    '(document:keydown.escape)': 'close()',
  },
  templateUrl: './multi-picker.html',
  styleUrl: './multi-picker.scss',
})
export class MultiPicker<T extends string | number = string | number> {
  readonly options = input.required<readonly PickerOption<T>[]>();
  /** Selected option ids; empty means all available options. */
  readonly selected = model<T[]>([]);
  readonly disabled = input(false);
  /** Plural noun used in labels, e.g. "indexers" or "providers". */
  readonly plural = input('items');
  /** Qualifier used when nothing is selected, e.g. "enabled" or "configured". */
  readonly qualifier = input('enabled');

  protected readonly open = signal(false);
  protected readonly filter = signal('');
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  private readonly selectedSet = computed(() => new Set<T>(this.selected()));
  protected readonly selectedOptions = computed(() => this.options().filter(o => this.selectedSet().has(o.id)));
  protected readonly showFilter = computed(() => this.options().length > 6);
  protected readonly filtered = computed(() => {
    const q = this.filter().trim().toLowerCase();
    return q ? this.options().filter(o => o.name.toLowerCase().includes(q)) : this.options();
  });
  protected readonly allLabel = computed(() => `All ${this.plural()}`);
  protected readonly ariaName = computed(() => {
    const p = this.plural();
    return p ? p[0].toUpperCase() + p.slice(1) : 'Items';
  });
  protected readonly label = computed(() => {
    const picked = this.selectedOptions();
    if (picked.length === 0) return this.allLabel();
    if (picked.length === 1) return picked[0].name;
    return `${picked.length} ${this.plural()}`;
  });
  protected readonly title = computed(() => {
    const picked = this.selectedOptions();
    return picked.length > 0 ? picked.map(o => o.name).join(', ') : `All ${this.qualifier()} ${this.plural()}`;
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

  protected isSelected(id: T): boolean { return this.selectedSet().has(id); }

  protected toggle(id: T) {
    const current = this.selected();
    this.selected.set(current.includes(id) ? current.filter(x => x !== id) : [...current, id]);
  }

  protected clear() { this.selected.set([]); }
}
