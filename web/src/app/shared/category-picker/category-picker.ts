import { Component, computed, input, model, signal } from '@angular/core';
import { IndexerCategory } from '../../core/models';

interface Group { parent: IndexerCategory; subs: IndexerCategory[]; relevant: boolean; }

/**
 * Checkbox picker for indexer categories, grouped parent → subcategories.
 * Categories in the suggested range (e.g. 2000-2999 for movies) are shown first; the rest behind "Show all".
 */
@Component({
  selector: 'kr-category-picker',
  templateUrl: './category-picker.html',
  styleUrl: './category-picker.scss',
})
export class CategoryPicker {
  readonly categories = input.required<IndexerCategory[]>();
  /** Inclusive id range shown by default, e.g. [2000, 2999]. */
  readonly range = input<[number, number]>([0, Number.MAX_SAFE_INTEGER]);
  readonly selected = model<number[]>([]);
  protected readonly showAll = signal(false);

  private readonly groups = computed<Group[]>(() => {
    const [min, max] = this.range();
    const inRange = (id: number) => id >= min && id <= max;
    const parents = this.categories().filter(c => c.parentId == null);
    const groups = parents.map(p => {
      const subs = this.categories().filter(c => c.parentId === p.id);
      return { parent: p, subs, relevant: inRange(p.id) || subs.some(s => inRange(s.id)) };
    });
    // Subcategories whose parent is missing are shown as their own group.
    const orphans = this.categories().filter(c => c.parentId != null && !parents.some(p => p.id === c.parentId));
    return [...groups, ...orphans.map(o => ({ parent: o, subs: [], relevant: inRange(o.id) }))];
  });

  protected readonly shown = computed(() => this.groups().filter(g => this.showAll() || g.relevant || g.parent.id === 0
    || [g.parent, ...g.subs].some(c => this.selected().includes(c.id))));
  protected readonly otherCount = computed(() => this.groups().length - this.groups().filter(g => g.relevant).length);
  protected readonly unknown = computed(() => this.selected().filter(id => !this.categories().some(c => c.id === id)));

  protected isSelected(id: number) { return this.selected().includes(id); }

  protected toggle(id: number) {
    const current = this.selected();
    this.selected.set(current.includes(id) ? current.filter(x => x !== id) : [...current, id].sort((a, b) => a - b));
  }

  protected shortName(sub: IndexerCategory, parent: IndexerCategory) {
    return sub.name.startsWith(parent.name + '/') ? sub.name.slice(parent.name.length + 1) : sub.name;
  }
}
