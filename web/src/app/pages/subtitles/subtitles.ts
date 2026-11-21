import { Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { Api, errorMessage } from '../../core/api';
import { Auth } from '../../core/auth';
import { Events } from '../../core/events';
import { Language, SubtitleSettings, WantedSubtitle } from '../../core/models';
import { Toasts } from '../../core/toast';
import { SubtitleManager } from '../../shared/subtitle-manager/subtitle-manager';

@Component({
  selector: 'kr-subtitles',
  imports: [RouterLink, SubtitleManager],
  templateUrl: './subtitles.html',
  styleUrl: './subtitles.scss',
})
export class SubtitlesPage implements OnInit {
  private readonly auth = inject(Auth);
  private readonly api = inject(Api);
  private readonly toasts = inject(Toasts);
  private readonly events = inject(Events);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly settings = signal<SubtitleSettings | null>(null);
  protected readonly wanted = signal<WantedSubtitle[]>([]);
  protected readonly languages = signal<Language[]>([]);
  protected readonly loading = signal(true);
  protected readonly manage = signal<WantedSubtitle | null>(null);
  /** Managers can't read settings; for them the server reports a missing provider when they search. */
  protected readonly ready = computed(() => !this.auth.isAdmin() || (!!this.settings()?.enabled && !!this.settings()?.openSubtitles.apiKey));

  async ngOnInit() {
    const [s, langs] = await Promise.all([
      this.auth.isAdmin() ? this.api.settings('subtitles') : Promise.resolve(null), this.api.subtitleLanguages(),
    ]);
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
