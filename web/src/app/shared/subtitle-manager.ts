import { Component, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api, errorMessage } from '../core/api';
import { Language, ScoredSubtitle, SubtitleFile, SubtitleFileRef } from '../core/models';
import { Toasts } from '../core/toast';
import { Modal } from './modal';

/** Dialog listing a file's subtitles with automatic and interactive search. */
@Component({
  selector: 'kr-subtitle-manager',
  imports: [FormsModule, Modal],
  template: `
    <kr-modal [title]="'Subtitles · ' + label()" width="max-w-5xl" (closed)="closed.emit()">
      <div class="mb-4 flex flex-wrap items-center gap-2">
        @for (s of files(); track s.id) {
          <span class="badge bg-surface-700 text-slate-200" [title]="(s.release ?? s.fileName) + ' · ' + s.provider + (s.score != null ? ' · score ' + s.score : '')">
            {{ s.languageName }}{{ s.hearingImpaired ? ' (HI)' : '' }}{{ s.forced ? ' (forced)' : '' }}
            <button class="ml-1 text-slate-400 hover:text-rose-300" (click)="remove(s)" aria-label="Delete subtitle">✕</button>
          </span>
        } @empty {
          <span class="text-sm text-slate-500">No subtitles yet.</span>
        }
        <span class="flex-1"></span>
        <button class="btn-secondary btn-sm" (click)="auto()">Automatic search</button>
      </div>

      <div class="mb-3 flex flex-wrap items-center gap-2">
        <select class="input min-w-0 flex-1 sm:w-60 sm:flex-none" [(ngModel)]="language">
          <option value="">All wanted languages</option>
          @for (l of languages(); track l.code) { <option [value]="l.code">{{ l.name }}</option> }
        </select>
        <button class="btn-primary btn-sm" (click)="search()" [disabled]="searching()">{{ searching() ? 'Searching…' : 'Search providers' }}</button>
      </div>

      @for (e of errors(); track e) { <p class="mb-2 rounded bg-amber-500/10 px-3 py-2 text-xs text-amber-300">{{ e }}</p> }

      @if (searched() && results().length === 0 && !searching()) {
        <p class="py-6 text-center text-sm text-slate-400">No subtitles found.</p>
      }
      @if (results().length > 0) {
        <div class="overflow-x-auto sm:max-h-[55vh] sm:overflow-auto">
          <table class="table">
            <thead><tr><th>Release</th><th class="max-sm:hidden">Language</th><th>Score</th><th class="max-sm:hidden">Downloads</th><th></th></tr></thead>
            <tbody>
              @for (r of results(); track r.candidate.provider + r.candidate.payload) {
                <tr [class.opacity-60]="!r.acceptable">
                  <td class="w-full max-w-0">
                    <div class="break-all sm:truncate sm:break-normal" [title]="r.candidate.releaseNames.join(' | ')">{{ r.candidate.releaseNames[0] ?? r.candidate.fileName }}</div>
                    <div class="flex flex-wrap gap-1 text-xs">
                      <span class="badge bg-surface-700 text-slate-200 sm:hidden">{{ languageName(r.candidate.language) }}</span>
                      @if (r.candidate.hashMatch) { <span class="badge bg-emerald-500/15 text-emerald-300">hash match</span> }
                      @if (r.candidate.hearingImpaired) { <span class="badge bg-surface-700 text-slate-300">HI</span> }
                      @if (r.candidate.trustedUploader) { <span class="badge bg-surface-700 text-slate-300">trusted</span> }
                      <span class="text-slate-500">{{ r.matches.join(', ') }}</span>
                      @if (!r.acceptable) { <span class="text-rose-300">· {{ r.rejections.join(' · ') }}</span> }
                    </div>
                  </td>
                  <td class="whitespace-nowrap max-sm:hidden">{{ languageName(r.candidate.language) }}</td>
                  <td><span class="badge" [class]="r.score >= 80 ? 'bg-emerald-500/15 text-emerald-300' : r.score >= 60 ? 'bg-amber-500/15 text-amber-300' : 'bg-rose-500/15 text-rose-300'">{{ r.score }}</span></td>
                  <td class="text-slate-400 max-sm:hidden">{{ r.candidate.downloads }}</td>
                  <td class="text-right">
                    <button class="btn-sm" [class]="r.acceptable ? 'btn-primary' : 'btn-secondary'" (click)="download(r)" [disabled]="downloading() === r.candidate.payload">
                      {{ downloading() === r.candidate.payload ? '…' : 'Download' }}
                    </button>
                  </td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      }
    </kr-modal>
  `,
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
