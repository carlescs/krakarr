import { Component, DestroyRef, OnInit, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { Api, errorMessage } from '../../../core/api';
import { Events } from '../../../core/events';
import { EmbeddedSubtitle, RedundantSubtitle } from '../../../core/models';
import { Toasts } from '../../../core/toast';

/** Rows loaded per "Show more"; a reload asks for everything shown so far, up to the endpoint's limit. */
const PAGE_SIZE = 100;
const MAX_LIMIT = 500;

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

  ngOnInit() {
    void this.load();
    this.events.subtitlesUpdated.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(() => void this.load());
  }

  /** Reloads from the top, keeping as many rows as were shown. */
  async load() {
    const limit = Math.min(MAX_LIMIT, Math.max(PAGE_SIZE, this.items().length));
    try {
      const res = await this.api.redundantSubtitles({ offset: 0, limit });
      this.items.set(res.items);
      this.total.set(res.total);
    } catch (e) {
      this.toasts.error(errorMessage(e));
    } finally {
      this.loading.set(false);
    }
  }

  async more() {
    this.loadingMore.set(true);
    try {
      // Deleted rows are gone on the server too, so the rows shown are exactly the first ones; skip duplicates anyway.
      const res = await this.api.redundantSubtitles({ offset: this.items().length, limit: PAGE_SIZE });
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

  protected describe(s: { languageName: string; forced: boolean; hearingImpaired: boolean }) {
    return s.languageName + (s.forced ? ' (forced)' : '') + (s.hearingImpaired ? ' (SDH)' : '');
  }

  protected embeddedLabel(e: EmbeddedSubtitle) {
    return e.language + (e.forced ? ' forced' : '') + (e.hearingImpaired ? ' SDH' : '');
  }
}
