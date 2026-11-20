import { Component, inject, input, model } from '@angular/core';
import { Language } from '../core/models';
import { Languages } from '../core/languages';

/** Ordered list of languages: the first one is preferred most. Add from a dropdown, reorder with the arrows. */
@Component({
  selector: 'kr-language-picker',
  template: `
    <div class="flex flex-wrap items-center gap-2">
      @for (code of value(); track code; let i = $index, first = $first, last = $last) {
        <span class="badge gap-1 bg-accent-500/15 text-accent-300">
          @if (!first) {
            <button type="button" class="hover:text-white" (click)="move(i, -1)" [attr.aria-label]="'Prefer ' + catalog.name(code) + ' more'">‹</button>
          }
          <span>{{ i + 1 }}. {{ catalog.name(code) }}</span>
          @if (!last) {
            <button type="button" class="hover:text-white" (click)="move(i, 1)" [attr.aria-label]="'Prefer ' + catalog.name(code) + ' less'">›</button>
          }
          <button type="button" class="ml-1 hover:text-white" (click)="remove(code)" [attr.aria-label]="'Remove ' + catalog.name(code)">✕</button>
        </span>
      }
      <select class="input w-full sm:w-56" [attr.aria-label]="'Add language'" (change)="add($any($event.target).value); $any($event.target).value = ''">
        <option value="">Add language…</option>
        @for (l of languages(); track l.code) {
          @if (!value().includes(l.code)) { <option [value]="l.code">{{ l.name }}</option> }
        }
      </select>
    </div>
  `,
})
export class LanguagePicker {
  protected readonly catalog = inject(Languages);
  readonly languages = input.required<Language[]>();
  readonly value = model<string[]>([]);

  protected add(code: string) {
    if (code && !this.value().includes(code)) this.value.set([...this.value(), code]);
  }

  protected remove(code: string) { this.value.set(this.value().filter(c => c !== code)); }

  protected move(index: number, delta: number) {
    const list = [...this.value()];
    const target = index + delta;
    if (target < 0 || target >= list.length) return;
    [list[index], list[target]] = [list[target], list[index]];
    this.value.set(list);
  }
}

/** Edits an item's own wanted languages: null follows the global default, a list overrides it. */
@Component({
  selector: 'kr-language-override',
  imports: [LanguagePicker],
  template: `
    <label class="label">Languages</label>
    <label class="flex items-center gap-2 text-sm">
      <input type="checkbox" class="accent-accent-500" [checked]="value() === null" (change)="toggleDefault($any($event.target).checked)" />
      Use the default ({{ catalog.describe(catalog.preferred()) }})
    </label>
    @if (value(); as list) {
      <div class="mt-2"><kr-language-picker [languages]="catalog.all()" [value]="list" (valueChange)="value.set($event)" /></div>
      @if (list.length === 0) { <p class="hint">No languages selected: releases in any language are accepted.</p> }
    }
    <p class="hint">Only releases with one of these audio languages are downloaded; earlier languages are preferred over better quality.</p>
  `,
})
export class LanguageOverride {
  protected readonly catalog = inject(Languages);
  readonly value = model<string[] | null>(null);

  constructor() { this.catalog.load().catch(() => {}); }

  protected toggleDefault(useDefault: boolean) {
    this.value.set(useDefault ? null : [...this.catalog.preferred()]);
  }
}
