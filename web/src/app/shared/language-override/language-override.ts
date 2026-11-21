import { Component, inject, model } from '@angular/core';
import { Languages } from '../../core/languages';
import { LanguagePicker } from '../language-picker/language-picker';

/** Edits an item's own wanted languages: null follows the global default, a list overrides it. */
@Component({
  selector: 'kr-language-override',
  imports: [LanguagePicker],
  templateUrl: './language-override.html',
  styleUrl: './language-override.scss',
})
export class LanguageOverride {
  protected readonly catalog = inject(Languages);
  readonly value = model<string[] | null>(null);

  constructor() { this.catalog.load().catch(() => {}); }

  protected toggleDefault(useDefault: boolean) {
    this.value.set(useDefault ? null : [...this.catalog.preferred()]);
  }
}
