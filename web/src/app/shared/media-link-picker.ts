import { Component, OnInit, computed, inject, input, model, signal } from '@angular/core';
import { Api, DownloadLink } from '../core/api';

/** A library item a manual download can be linked to. */
export interface MediaLink { kind: 'movie' | 'series'; id: number; title: string; year: number; }

/** Lower-case words without accents or punctuation: "Amélie (2001)" → "amelie 2001". */
export function normalizeTitle(s: string): string {
  return s.normalize('NFD').replace(/\p{M}/gu, '').toLowerCase().replace(/&/g, ' and ').replace(/[^a-z0-9]+/g, ' ').trim();
}

/** Items whose title contains what the user typed; titles starting with it first. */
export function filterLinks(items: MediaLink[], text: string, limit = 8): MediaLink[] {
  const q = normalizeTitle(text);
  if (!q) return [];
  const scored = items
    .map(item => ({ item, title: normalizeTitle(item.title) }))
    .filter(x => x.title.includes(q) || `${x.title} ${x.item.year}`.includes(q))
    .sort((a, b) => Number(b.title.startsWith(q)) - Number(a.title.startsWith(q)) || a.title.localeCompare(b.title));
  return scored.slice(0, limit).map(x => x.item);
}

/**
 * Items a release name probably belongs to: release names start with the title ("The.Office.US.S02E01…").
 * Longer (more specific) titles and matching years rank first.
 */
export function suggestLinks(items: MediaLink[], releaseName: string, limit = 4): MediaLink[] {
  const name = ` ${normalizeTitle(releaseName)} `;
  if (!name.trim()) return [];
  return items
    .map(item => ({ item, title: normalizeTitle(item.title) }))
    .filter(x => x.title && name.startsWith(` ${x.title} `))
    .map(x => ({ ...x, score: x.title.length + (x.item.year > 0 && name.includes(` ${x.item.year} `) ? 100 : 0) }))
    .sort((a, b) => b.score - a.score)
    .slice(0, limit)
    .map(x => x.item);
}

/** Optional "link this download to a movie or series" field for manual downloads. */
@Component({
  selector: 'kr-media-link-picker',
  host: { class: 'block' },
  template: `
    <label class="label" [attr.for]="inputId">Link to movie or series</label>
    @if (link(); as l) {
      <div class="flex items-center gap-2 rounded-lg border border-surface-700 bg-surface-800 px-3 py-2 text-sm">
        <span class="badge" [class]="badgeClass(l)">{{ l.kind === 'movie' ? 'Movie' : 'Series' }}</span>
        <span class="min-w-0 flex-1 truncate text-slate-100">{{ l.title }}@if (l.year) { <span class="text-slate-400"> ({{ l.year }})</span> }</span>
        <button type="button" class="btn-ghost btn-sm" (click)="link.set(null)" aria-label="Remove link">✕</button>
      </div>
      <p class="hint">Imported into the library when the download finishes.</p>
    } @else {
      <input class="input" [id]="inputId" [value]="text()" (input)="text.set($any($event.target).value)" autocomplete="off"
             [placeholder]="loading() ? 'Loading library…' : 'Search your movies and series (optional)'" />
      @if (options().length > 0) {
        <div class="mt-1.5 overflow-hidden rounded-lg border border-surface-700">
          @if (!text().trim()) { <div class="px-3 pt-2 text-xs text-slate-500">Suggestions</div> }
          @for (o of options(); track o.kind + o.id) {
            <button type="button" class="flex w-full items-center gap-2 px-3 py-1.5 text-left text-sm hover:bg-surface-800 pointer-coarse:min-h-10" (click)="pick(o)">
              <span class="badge" [class]="badgeClass(o)">{{ o.kind === 'movie' ? 'Movie' : 'Series' }}</span>
              <span class="min-w-0 flex-1 truncate text-slate-200">{{ o.title }}@if (o.year) { <span class="text-slate-500"> ({{ o.year }})</span> }</span>
            </button>
          }
        </div>
      } @else if (text().trim() && !loading()) {
        <p class="hint">Nothing in your library matches.</p>
      } @else {
        <p class="hint">Leave empty to keep it as a plain download.</p>
      }
    }
  `,
})
export class MediaLinkPicker implements OnInit {
  private static nextId = 0;
  private readonly api = inject(Api);
  /** Release or torrent name used to suggest items before the user types. */
  readonly hint = input('');
  readonly link = model<MediaLink | null>(null);
  protected readonly inputId = `media-link-${MediaLinkPicker.nextId++}`;
  protected readonly text = signal('');
  protected readonly loading = signal(true);
  private readonly items = signal<MediaLink[]>([]);
  protected readonly options = computed(() => this.text().trim()
    ? filterLinks(this.items(), this.text())
    : suggestLinks(this.items(), this.hint()));

  async ngOnInit() {
    const [movies, series] = await Promise.all([
      this.api.movies().catch(() => []),
      this.api.seriesList().catch(() => []),
    ]);
    this.items.set([
      ...movies.map(m => ({ kind: 'movie' as const, id: m.id, title: m.title, year: m.year })),
      ...series.map(s => ({ kind: 'series' as const, id: s.id, title: s.title, year: s.year })),
    ]);
    this.loading.set(false);
  }

  protected pick(o: MediaLink) {
    this.link.set(o);
    this.text.set('');
  }

  protected badgeClass(l: MediaLink) {
    return l.kind === 'movie' ? 'bg-accent-500/15 text-accent-300' : 'bg-violet-500/15 text-violet-300';
  }
}

/** Request fields for a link (what the download endpoints accept). */
export function linkFields(link: MediaLink | null): DownloadLink {
  if (!link) return {};
  return link.kind === 'movie' ? { movieId: link.id } : { seriesId: link.id };
}
