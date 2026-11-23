import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api, errorMessage } from '../../core/api';
import { AgoPipe, BytesPipe } from '../../core/format';
import { ReleaseSearch } from '../../core/indexer-search';
import { SearchIndexer, SearchResult, SearchType } from '../../core/models';
import { Toasts } from '../../core/toast';
import { MediaLink, MediaLinkPicker, linkFields } from '../../shared/media-link-picker/media-link-picker';
import { MultiPicker } from '../../shared/multi-picker/multi-picker';
import { IndexerProgress } from '../../shared/indexer-progress/indexer-progress';
import { Modal } from '../../shared/modal/modal';
import { TableSort } from '../../core/table-sort';
import { SortHeader } from '../../shared/sort-header/sort-header';

@Component({
  selector: 'kr-search',
  imports: [FormsModule, BytesPipe, AgoPipe, Modal, MediaLinkPicker, MultiPicker, IndexerProgress, SortHeader],
  templateUrl: './search.html',
  styleUrl: './search.scss',
})
export class SearchPage {
  private readonly api = inject(Api);
  private readonly toasts = inject(Toasts);
  protected query = '';
  protected type: SearchType = 'Generic';
  protected readonly availableIndexers = signal<SearchIndexer[]>([]);
  protected readonly selectedIndexerIds = signal<number[]>([]);
  protected readonly placeholder = computed(() => {
    const ids = new Set(this.selectedIndexerIds());
    const picked = this.availableIndexers().filter(i => ids.has(i.id));
    if (picked.length === 0) return 'Search all enabled indexers…';
    if (picked.length === 1) return `Search ${picked[0].name}…`;
    return `Search ${picked.length} selected indexers…`;
  });
  protected readonly searched = signal(false);
  /** Most seeders first, also while indexers are still answering. */
  protected readonly releaseSearch = new ReleaseSearch<SearchResult>((a, b) => (b.release.seeders ?? 0) - (a.release.seeders ?? 0));
  protected readonly results = this.releaseSearch.results;
  protected readonly busy = this.releaseSearch.running;
  protected readonly sort = new TableSort(this.releaseSearch.filtered, {
    title: r => r.release.title,
    indexer: r => r.release.indexerName,
    age: { value: r => r.release.publishDate ? Date.parse(r.release.publishDate) : null, desc: true },
    size: { value: r => r.release.size, desc: true },
    peers: { value: r => r.release.seeders, desc: true },
    quality: r => r.quality,
  });

  constructor() {
    this.api.searchIndexers().then(list => this.availableIndexers.set(list)).catch(() => { /* search still queries all enabled indexers */ });
  }

  async search() {
    this.searched.set(true);
    const ids = this.selectedIndexerIds();
    try {
      await this.releaseSearch.run((onEvent, signal) =>
        ids.length > 0
          ? this.api.searchStream(this.query, this.type, onEvent, signal, ids)
          : this.api.searchStream(this.query, this.type, onEvent, signal));
    } catch (e) {
      this.toasts.error(errorMessage(e));
    }
  }

  protected readonly grabbing = signal<SearchResult | null>(null);
  protected readonly link = signal<MediaLink | null>(null);
  protected readonly sending = signal(false);

  protected open(r: SearchResult) {
    this.link.set(null);
    this.grabbing.set(r);
  }

  async grab(r: SearchResult) {
    const link = this.link();
    this.sending.set(true);
    try {
      // Linked releases use the movie/series category; plain ones keep "manual".
      await this.api.grabRelease(r.release, link ? undefined : 'manual', linkFields(link));
      this.toasts.success(link ? `Sent to downloads for ${link.title}` : 'Sent to downloads');
      this.grabbing.set(null);
    } catch (e) {
      this.toasts.error(errorMessage(e));
    } finally {
      this.sending.set(false);
    }
  }
}
