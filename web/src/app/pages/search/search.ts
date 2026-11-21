import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api, errorMessage } from '../../core/api';
import { AgoPipe, BytesPipe } from '../../core/format';
import { IndexerError, SearchResult, SearchType } from '../../core/models';
import { Toasts } from '../../core/toast';
import { MediaLink, MediaLinkPicker, linkFields } from '../../shared/media-link-picker/media-link-picker';
import { Modal } from '../../shared/modal/modal';

@Component({
  selector: 'kr-search',
  imports: [FormsModule, BytesPipe, AgoPipe, Modal, MediaLinkPicker],
  templateUrl: './search.html',
  styleUrl: './search.scss',
})
export class SearchPage {
  private readonly api = inject(Api);
  private readonly toasts = inject(Toasts);
  protected query = '';
  protected type: SearchType = 'Generic';
  protected readonly busy = signal(false);
  protected readonly searched = signal(false);
  protected readonly results = signal<SearchResult[]>([]);
  protected readonly errors = signal<IndexerError[]>([]);

  async search() {
    this.busy.set(true);
    try {
      const res = await this.api.search(this.query, this.type);
      this.results.set(res.results);
      this.errors.set(res.errors);
      this.searched.set(true);
    } catch (e) {
      this.toasts.error(errorMessage(e));
    } finally {
      this.busy.set(false);
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
