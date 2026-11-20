import { Injectable, inject, signal } from '@angular/core';
import { Api } from './api';
import { Language } from './models';

/** Release audio languages and the global wanted list, loaded once and shared by every page. */
@Injectable({ providedIn: 'root' })
export class Languages {
  private readonly api = inject(Api);
  private pending?: Promise<void>;

  readonly all = signal<Language[]>([]);
  /** Global wanted languages (Settings › Quality profiles), most preferred first; empty = any. */
  readonly preferred = signal<string[]>([]);

  /** Loads the catalogue; `force` re-reads the global list after it changed. */
  load(force = false): Promise<void> {
    if (!this.pending || force) {
      this.pending = this.api.languages().then(
        c => { this.all.set(c.all); this.preferred.set(c.preferred); },
        e => { this.pending = undefined; throw e; },
      );
    }
    return this.pending;
  }

  name(code: string) { return this.all().find(l => l.code === code)?.name ?? code; }

  /** "Spanish, English", or `empty` when the list is empty. */
  describe(codes: readonly string[] | null | undefined, empty = 'Any language') {
    return codes?.length ? codes.map(c => this.name(c)).join(', ') : empty;
  }
}
