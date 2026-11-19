import { Component, computed, input, model, signal } from '@angular/core';
import { IndexerCategory } from '../core/models';

interface Group { parent: IndexerCategory; subs: IndexerCategory[]; relevant: boolean; }

/**
 * Checkbox picker for indexer categories, grouped parent → subcategories.
 * Categories in the suggested range (e.g. 2000-2999 for movies) are shown first; the rest behind "Show all".
 */
@Component({
  selector: 'kr-category-picker',
  template: `
    <div class="rounded-lg border border-surface-700">
      <div class="max-h-56 overflow-y-auto p-2">
        @for (g of shown(); track g.parent.id) {
          <label class="flex items-center gap-2 rounded px-2 py-1 text-sm hover:bg-surface-800">
            <input type="checkbox" class="accent-accent-500" [checked]="isSelected(g.parent.id)" (change)="toggle(g.parent.id)" />
            <span class="flex-1 font-medium text-slate-100">{{ g.parent.name }}</span>
            <span class="font-mono text-xs text-slate-500">{{ g.parent.id }}</span>
          </label>
          @for (s of g.subs; track s.id) {
            <label class="ml-5 flex items-center gap-2 rounded px-2 py-0.5 text-sm hover:bg-surface-800"
                   [class.opacity-50]="isSelected(g.parent.id)" [title]="isSelected(g.parent.id) ? 'Included by ' + g.parent.name : ''">
              <input type="checkbox" class="accent-accent-500" [checked]="isSelected(s.id)" (change)="toggle(s.id)" />
              <span class="flex-1 text-slate-300">{{ shortName(s, g.parent) }}</span>
              <span class="font-mono text-xs text-slate-500">{{ s.id }}</span>
            </label>
          }
        } @empty {
          <p class="px-2 py-1 text-sm text-slate-500">No categories in this range.</p>
        }
      </div>
      @if (otherCount() > 0) {
        <button type="button" class="w-full border-t border-surface-700 px-3 py-1.5 text-left text-xs text-slate-400 hover:text-white" (click)="showAll.set(!showAll())">
          {{ showAll() ? 'Show suggested only' : 'Show all categories (+' + otherCount() + ')' }}
        </button>
      }
    </div>
    @if (unknown().length > 0) {
      <div class="mt-1.5 flex flex-wrap gap-1">
        @for (id of unknown(); track id) {
          <span class="badge bg-amber-500/15 text-amber-300" title="Not advertised by this indexer">
            {{ id }} <button type="button" class="ml-1 hover:text-white" (click)="toggle(id)" aria-label="Remove">✕</button>
          </span>
        }
      </div>
    }
  `,
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
