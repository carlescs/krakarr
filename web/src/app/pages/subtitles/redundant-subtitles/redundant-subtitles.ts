import { Component, DestroyRef, OnInit, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { auditTime } from 'rxjs';
import { RouterLink } from '@angular/router';
import { Api, errorMessage } from '../../../core/api';
import { Events } from '../../../core/events';
import { EmbeddedSubtitle, RedundantSubtitle } from '../../../core/models';
import { Selection } from '../../../core/selection';
import { Toasts } from '../../../core/toast';

/** Rows loaded per "Show more"; a reload asks for everything shown so far, in slices of the endpoint's limit. */
const PAGE_SIZE = 100;
const MAX_LIMIT = 500;
/** A bulk search publishes one event per downloaded subtitle, and each reload scans the library: wait for things to settle. */
const RELOAD_DELAY_MS = 2000;

/** Downloaded (external) subtitles in a language the video already carries as an embedded track, which can be deleted. */
@Component({
  selector: 'kr-redundant-subtitles',
  imports: [RouterLink, DatePipe],
  templateUrl: './redundant-subtitles.html',
  styleUrl: './redundant-subtitles.scss',
})
export class RedundantSubtitles implements OnInit {
  private readonly api = inject(Api);
  private readonly toasts = inject(Toasts);
  private readonly events = inject(Events);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly items = signal<RedundantSubtitle[]>([]);
  protected readonly total = signal(0);
  protected readonly loading = signal(true);
  protected readonly loadingMore = signal(false);
  /** Ids being deleted, so their button can't be pressed twice. */
  protected readonly deleting = signal<ReadonlySet<number>>(new Set());
  /** Rows picked for a bulk delete; only rows still listed count, so reloads drop deleted ones. */
  protected readonly selection = new Selection<RedundantSubtitle>(this.items);
  protected readonly bulkDeleting = signal(false);
  /** Bumped by every load and "Show more", so a response that a newer request overtook is dropped. */
  private generation = 0;

  ngOnInit() {
    void this.load();
    this.events.subtitlesUpdated.pipe(auditTime(RELOAD_DELAY_MS), takeUntilDestroyed(this.destroyRef)).subscribe(() => void this.load());
  }

  /** Reloads from the top, keeping as many rows as were shown. */
  async load() {
    const generation = ++this.generation;
    const wanted = Math.max(PAGE_SIZE, this.items().length);
    try {
      const items: RedundantSubtitle[] = [];
      let total = 0;
      for (;;) {
        const limit = Math.min(MAX_LIMIT, wanted - items.length);
        const res = await this.api.redundantSubtitles({ offset: items.length, limit });
        if (generation !== this.generation) return;
        items.push(...res.items);
        total = res.total;
        // A short slice is the end of the list.
        if (res.items.length < limit || items.length >= wanted) break;
      }
      this.items.set(items);
      this.total.set(total);
    } catch (e) {
      if (generation === this.generation) this.toasts.error(errorMessage(e));
    } finally {
      if (generation === this.generation) this.loading.set(false);
    }
  }

  async more() {
    const generation = ++this.generation;
    this.loadingMore.set(true);
    try {
      // Deleted rows are gone on the server too, so the rows shown are exactly the first ones; skip duplicates anyway.
      const res = await this.api.redundantSubtitles({ offset: this.items().length, limit: PAGE_SIZE });
      if (generation !== this.generation) return;
      const seen = new Set(this.items().map(r => r.id));
      this.items.update(list => [...list, ...res.items.filter(r => !seen.has(r.id))]);
      this.total.set(res.total);
    } catch (e) {
      this.toasts.error(errorMessage(e));
    } finally {
      this.loadingMore.set(false);
    }
  }

  async remove(row: RedundantSubtitle) {
    if (!confirm(`Delete the downloaded ${this.describe(row)} subtitle "${row.fileName}" for ${row.label}? The video already has an embedded ${row.languageName} track. The file is removed from disk.`)) return;
    this.deleting.update(s => new Set(s).add(row.id));
    try {
      await this.api.deleteSubtitle(row.id);
      this.items.update(list => list.filter(r => r.id !== row.id));
      this.total.update(n => Math.max(0, n - 1));
      this.toasts.show(`Deleted ${row.fileName}`);
    } catch (e) {
      this.toasts.error(errorMessage(e));
    } finally {
      this.deleting.update(s => { const next = new Set(s); next.delete(row.id); return next; });
    }
  }

  /** Deletes every selected subtitle in one request, after confirming. */
  async removeSelected() {
    const ids = this.selection.selected();
    if (ids.length === 0 || this.bulkDeleting()) return;
    const noun = ids.length === 1 ? 'subtitle' : 'subtitles';
    if (!confirm(`Delete ${ids.length} downloaded ${noun}? Their videos already have an embedded track in the same language. The files are removed from disk.`)) return;
    this.bulkDeleting.set(true);
    try {
      const result = await this.api.deleteSubtitles(ids);
      // Rows whose file couldn't be removed are still on the server; a reload shows exactly what is left.
      if (result.errors.length === 0) {
        const gone = new Set(ids);
        this.items.update(list => list.filter(r => !gone.has(r.id)));
        this.total.update(n => Math.max(0, n - result.affected));
        this.toasts.success(`Deleted ${result.affected} ${result.affected === 1 ? 'subtitle' : 'subtitles'}`);
      } else {
        this.toasts.error(`Deleted ${result.affected}; ${result.errors.length} failed: ${result.errors.slice(0, 3).join(' · ')}`);
        await this.load();
      }
      this.selection.clear();
    } catch (e) {
      this.toasts.error(errorMessage(e));
    } finally {
      this.bulkDeleting.set(false);
    }
  }

  protected describe(s: { languageName: string; forced: boolean; hearingImpaired: boolean }) {
    return s.languageName + (s.forced ? ' (forced)' : '') + (s.hearingImpaired ? ' (SDH)' : '');
  }

  protected embeddedLabel(e: EmbeddedSubtitle) {
    return e.language + (e.forced ? ' forced' : '') + (e.hearingImpaired ? ' SDH' : '');
  }
}
