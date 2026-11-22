import { Injectable, computed, inject, signal } from '@angular/core';
import { Api } from './api';
import { Language, SubtitleFile, WantedSubtitleLanguages } from './models';

/** Provider name of subtitle tracks inside the video file (they have id 0 and cannot be deleted). */
export const EMBEDDED_PROVIDER = 'embedded';

export const isEmbedded = (s: SubtitleFile) => s.provider === EMBEDDED_PROVIDER;

/** "English (HI, forced)". */
export function subtitleLabel(s: SubtitleFile): string {
  const flags = [s.hearingImpaired ? 'HI' : '', s.forced ? 'forced' : ''].filter(Boolean);
  return flags.length ? `${s.languageName} (${flags.join(', ')})` : s.languageName;
}

/** Where a subtitle comes from, for tooltips and the detailed list. */
export function subtitleSource(s: SubtitleFile): string {
  return isEmbedded(s) ? 'Inside the video file' : s.provider === 'disk' || !s.provider ? 'On disk' : s.provider;
}

/**
 * Wanted languages a file has no full (non-forced) subtitle for, mirroring the server's check: external files always
 * count, tracks inside the video only when "use embedded subtitles" is on.
 */
export function missingSubtitleLanguages(files: readonly SubtitleFile[], wanted: WantedSubtitleLanguages | null): string[] {
  if (!wanted?.enabled || wanted.languages.length === 0) return [];
  const have = new Set(files.filter(s => !s.forced && (!isEmbedded(s) || wanted.useEmbeddedSubtitles)).map(s => s.language));
  return wanted.languages.filter(l => !have.has(l));
}

/** Subtitle languages Krakarr looks for, loaded once and shared by the detail pages. */
@Injectable({ providedIn: 'root' })
export class SubtitleLanguages {
  private readonly api = inject(Api);
  private pending?: Promise<void>;

  readonly wanted = signal<WantedSubtitleLanguages | null>(null);
  readonly all = signal<Language[]>([]);
  private readonly names = computed(() => new Map(this.all().map(l => [l.code, l.name])));

  /** Loads the wanted languages and the language names; `force` re-reads them after the settings changed. */
  load(force = false): Promise<void> {
    if (!this.pending || force) {
      this.pending = Promise.all([this.api.wantedSubtitleLanguages(), this.api.subtitleLanguages()]).then(
        ([wanted, all]) => { this.wanted.set(wanted); this.all.set(all); },
        e => { this.pending = undefined; throw e; },
      );
    }
    return this.pending;
  }

  name(code: string) { return this.names().get(code) ?? code; }

  missing(files: readonly SubtitleFile[]) { return missingSubtitleLanguages(files, this.wanted()); }
}
