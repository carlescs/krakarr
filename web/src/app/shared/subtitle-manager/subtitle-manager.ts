import { Component, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api, errorMessage } from '../../core/api';
import { Language, ScoredSubtitle, SubtitleFile, SubtitleFileRef } from '../../core/models';
import { Toasts } from '../../core/toast';
import { Modal } from '../modal/modal';

/** Dialog listing a file's subtitles with automatic and interactive search. */
@Component({
  selector: 'kr-subtitle-manager',
  imports: [FormsModule, Modal],
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
  protected readonly languages = signal<Language[]>([]);
  protected readonly results = signal<ScoredSubtitle[]>([]);
  protected readonly errors = signal<string[]>([]);
  protected readonly searching = signal(false);
  protected readonly searched = signal(false);
  protected readonly downloading = signal<string | null>(null);
  protected language = '';
  private readonly names = computed(() => new Map(this.languages().map(l => [l.code, l.name])));

  async ngOnInit() { this.languages.set(await this.api.subtitleLanguages()); }

  protected languageName(code: string) { return this.names().get(code) ?? code; }

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

  async remove(s: SubtitleFile) {
    await this.api.deleteSubtitle(s.id);
    this.changed.emit();
  }
}
