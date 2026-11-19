import { Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { Api, errorMessage } from '../core/api';
import { Events } from '../core/events';
import { Language, SubtitleSettings, WantedSubtitle } from '../core/models';
import { Toasts } from '../core/toast';
import { SubtitleManager } from '../shared/subtitle-manager';

@Component({
  selector: 'kr-subtitles',
  imports: [RouterLink, SubtitleManager],
  template: `
    <div class="mb-6 flex flex-wrap items-center gap-3">
      <h1 class="page-title mr-auto">Subtitles</h1>
      <button class="btn-primary" (click)="searchAll()" [disabled]="!ready() || wanted().length === 0">Search all missing</button>
    </div>

    @if (settings(); as s) {
      @if (!s.enabled || !s.openSubtitles.apiKey) {
        <div class="card mb-6 border-amber-500/30 px-5 py-4 text-sm text-amber-200">
          Automatic subtitles are {{ s.enabled ? 'enabled but no provider is configured' : 'disabled' }}.
          Configure languages and an OpenSubtitles.com API key in <a routerLink="/settings" class="underline">Settings → Subtitles</a>.
        </div>
      } @else {
        <p class="mb-4 text-sm text-slate-400">
          Wanted languages: <span class="text-slate-200">{{ languageNames(s.languages) }}</span>.
          New files are searched right after import; files still missing subtitles are retried every {{ s.searchIntervalHours }} h.
        </p>
      }
    }

    <section class="card overflow-hidden">
      <h2 class="border-b border-surface-700 px-5 py-3 text-sm font-semibold uppercase tracking-wide text-slate-400">
        Wanted <span class="ml-1 font-normal normal-case text-slate-500">{{ wanted().length }}</span>
      </h2>
      @if (loading()) {
        <p class="px-5 py-8 text-sm text-slate-500">Loading…</p>
      } @else if (wanted().length === 0) {
        <p class="px-5 py-8 text-sm text-slate-500">Every library file has subtitles in all wanted languages.</p>
      } @else {
        <div class="overflow-x-auto">
          <table class="table">
            <thead><tr><th>Type</th><th>Title</th><th>Missing</th><th></th></tr></thead>
            <tbody>
              @for (w of wanted(); track (w.movieFileId ?? 0) + ':' + (w.episodeFileId ?? 0)) {
                <tr>
                  <td><span class="badge" [class]="w.mediaType === 'Movie' ? 'bg-accent-500/15 text-accent-300' : 'bg-violet-500/15 text-violet-300'">{{ w.mediaType === 'Movie' ? 'Movie' : 'Episode' }}</span></td>
                  <td class="w-full max-w-0">
                    <a class="block truncate hover:text-accent-300" [routerLink]="w.movieId ? ['/movies', w.movieId] : ['/series', w.seriesId]">{{ w.label }}</a>
                  </td>
                  <td class="whitespace-nowrap">
                    @for (l of w.missingLanguages; track l) { <span class="badge mr-1 bg-rose-500/15 text-rose-300">{{ l }}</span> }
                  </td>
                  <td class="whitespace-nowrap text-right">
                    <button class="btn-ghost btn-sm" (click)="search(w)" [disabled]="!ready()">Search</button>
                    <button class="btn-ghost btn-sm" (click)="manage.set(w)">Manual</button>
                  </td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      }
    </section>

    @if (manage(); as w) {
      <kr-subtitle-manager [fileRef]="{ movieFileId: w.movieFileId, episodeFileId: w.episodeFileId }" [label]="w.label"
                           (closed)="manage.set(null); load()" (changed)="load()" />
    }
  `,
})
export class SubtitlesPage implements OnInit {
  private readonly api = inject(Api);
  private readonly toasts = inject(Toasts);
  private readonly events = inject(Events);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly settings = signal<SubtitleSettings | null>(null);
  protected readonly wanted = signal<WantedSubtitle[]>([]);
  protected readonly languages = signal<Language[]>([]);
  protected readonly loading = signal(true);
  protected readonly manage = signal<WantedSubtitle | null>(null);
  protected readonly ready = computed(() => !!this.settings()?.enabled && !!this.settings()?.openSubtitles.apiKey);

  async ngOnInit() {
    const [s, langs] = await Promise.all([this.api.settings('subtitles'), this.api.subtitleLanguages()]);
    this.settings.set(s);
    this.languages.set(langs);
    await this.load();
    this.events.subtitlesUpdated.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(() => this.load());
  }

  async load() {
    try { this.wanted.set(await this.api.wantedSubtitles()); }
    catch (e) { this.toasts.error(errorMessage(e)); }
    finally { this.loading.set(false); }
  }

  protected languageNames(codes: string[]) {
    return codes.map(c => this.languages().find(l => l.code === c)?.name ?? c).join(', ');
  }

  async search(w: WantedSubtitle) {
    await this.api.searchSubtitles({ movieFileId: w.movieFileId, episodeFileId: w.episodeFileId });
    this.toasts.show(`Searching subtitles for ${w.label}…`);
  }

  async searchAll() {
    await this.api.searchSubtitles();
    this.toasts.show('Searching subtitles for all wanted files…');
  }
}
