import { Component, inject, input, model } from '@angular/core';
import { Language } from '../../core/models';
import { Languages } from '../../core/languages';

/** Ordered list of languages: the first one is preferred most. Add from a dropdown, reorder with the arrows. */
@Component({
  selector: 'kr-language-picker',
  templateUrl: './language-picker.html',
  styleUrl: './language-picker.scss',
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
