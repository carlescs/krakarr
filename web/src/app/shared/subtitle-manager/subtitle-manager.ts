import { Component, OnInit, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api, errorMessage } from '../../core/api';
import { ScoredSubtitle, SubtitleFile, SubtitleFileRef } from '../../core/models';
import { SubtitleLanguages } from '../../core/subtitles';
import { Toasts } from '../../core/toast';
import { Modal } from '../modal/modal';
import { TableSort } from '../../core/table-sort';
import { SortHeader } from '../sort-header/sort-header';
import { SubtitleList } from '../subtitle-list/subtitle-list';

/** Dialog listing a file's subtitles with automatic and interactive search. */
@Component({
  selector: 'kr-subtitle-manager',
  imports: [FormsModule, Modal, SortHeader, SubtitleList],
  templateUrl: './subtitle-manager.html',
  styleUrl: './subtitle-manager.scss',
})
export class SubtitleManager implements OnInit {
  readonly fileRef = input.required<SubtitleFileRef>();
  readonly label = input('');
  readonly files = input<SubtitleFile[]>([]);
  readonly closed = output<void>();
  readonly changed = output<void>();

  private readonly api = inject(Api);
  private readonly toasts = inject(Toasts);
  private readonly subtitleLanguages = inject(SubtitleLanguages);
  protected readonly languages = this.subtitleLanguages.all;
  protected readonly results = signal<ScoredSubtitle[]>([]);
  /** The API returns subtitles best first. */
  protected readonly sort = new TableSort(this.results, {
    release: r => r.candidate.releaseNames[0] ?? r.candidate.fileName,
    language: r => this.languageName(r.candidate.language),
    score: { value: r => r.score, desc: true },
    downloads: { value: r => r.candidate.downloads, desc: true },
  });
  protected readonly errors = signal<string[]>([]);
  protected readonly searching = signal(false);
  protected readonly searched = signal(false);
  protected readonly downloading = signal<string | null>(null);
  protected language = '';

  ngOnInit() { this.subtitleLanguages.load().catch(() => {}); }

  protected languageName(code: string) { return this.subtitleLanguages.name(code); }

  async auto() {
    await this.api.searchSubtitles(this.fileRef());
    this.toasts.show('Searching subtitles — they will appear here when found');
  }

  async search() {
    this.searching.set(true);
    try {
      const res = await this.api.subtitleCandidates(this.fileRef(), this.language || undefined);
      this.results.set(res.results);
      this.errors.set(res.errors);
      this.searched.set(true);
    } catch (e) {
      this.toasts.error(errorMessage(e));
    } finally {
      this.searching.set(false);
    }
  }

  async download(r: ScoredSubtitle) {
    this.downloading.set(r.candidate.payload);
    try {
      await this.api.downloadSubtitle(this.fileRef(), r.candidate, r.score);
      this.toasts.success(`Downloaded ${this.languageName(r.candidate.language)} subtitle`);
      this.changed.emit();
    } catch (e) {
      this.toasts.error(errorMessage(e));
    } finally {
      this.downloading.set(null);
    }
  }
}
