import { Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { Api, errorMessage } from '../../core/api';
import { Auth } from '../../core/auth';
import { Events } from '../../core/events';
import { Language, SubtitleProvider, SubtitleSettings, WantedSubtitle } from '../../core/models';
import { Toasts } from '../../core/toast';
import { MultiPicker, PickerOption } from '../../shared/multi-picker/multi-picker';
import { SubtitleManager } from '../../shared/subtitle-manager/subtitle-manager';
import { TableSort } from '../../core/table-sort';
import { SortHeader } from '../../shared/sort-header/sort-header';

/** Which wanted files to list: all of them, those still searched automatically, or those marked "not found". */
export type WantedFilter = 'all' | 'searching' | 'notFound';

@Component({
  selector: 'kr-subtitles',
  imports: [RouterLink, DatePipe, MultiPicker, SubtitleManager, SortHeader],
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
  protected readonly providers = signal<SubtitleProvider[]>([]);
  protected readonly availableProviders = computed<PickerOption<string>[]>(() =>
    this.providers().filter(p => p.configured).map(p => ({ id: p.name, name: p.displayName || p.name })));
  protected readonly selectedProviders = signal<string[]>([]);
  protected readonly wanted = signal<WantedSubtitle[]>([]);
  protected readonly filter = signal<WantedFilter>('all');
  protected readonly filters: { id: WantedFilter; label: string; title: string }[] = [
    { id: 'all', label: 'All', title: 'Every file missing a wanted language' },
    { id: 'searching', label: 'Searching', title: 'Files the scheduled search still retries' },
    { id: 'notFound', label: 'Not found', title: 'Movies no provider had subtitles for; not searched automatically until retried' },
  ];
  protected readonly languages = signal<Language[]>([]);
  protected readonly loading = signal(true);
  protected readonly manage = signal<WantedSubtitle | null>(null);
  protected readonly sort = new TableSort(this.wanted, {
    type: w => w.mediaType,
    title: w => w.label,
    missing: { value: w => w.missingLanguages.length, desc: true },
  });
  /** Managers can't read settings; for them the server reports a missing provider when they search. */
  protected readonly ready = computed(() => {
    if (!this.auth.isAdmin()) return true;
    const s = this.settings();
    return !!s?.enabled && ((s.openSubtitles?.enabled !== false && !!s.openSubtitles?.apiKey) || (s.subdl?.enabled !== false && !!s.subdl?.apiKey));
  });

  async ngOnInit() {
    this.api.subtitleProviders().then(list => this.providers.set(list)).catch(() => { /* search still queries all configured providers */ });
    const [s, langs] = await Promise.all([
      this.auth.isAdmin() ? this.api.settings('subtitles') : Promise.resolve(null), this.api.subtitleLanguages(),
    ]);
    this.settings.set(s);
    this.languages.set(langs);
    await this.load();
    this.events.subtitlesUpdated.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(() => this.load());
  }

  async load() {
    const filter = this.filter();
    try {
      const list = await this.api.wantedSubtitles(filter === 'all' ? undefined : filter === 'notFound');
      if (filter === this.filter()) this.wanted.set(list); // ignore a stale response after switching filters
    }
    catch (e) { this.toasts.error(errorMessage(e)); }
    finally { this.loading.set(false); }
  }

  protected setFilter(filter: WantedFilter) {
    if (filter === this.filter()) return;
    this.filter.set(filter);
    this.loading.set(true);
    this.load();
  }

  async retry(w?: WantedSubtitle) {
    try {
      const { count } = await this.api.retrySubtitles(w?.movieFileId);
      this.toasts.show(w ? `${w.label} will be searched again shortly` : `${count} movie(s) will be searched again shortly`);
      await this.load();
    } catch (e) { this.toasts.error(errorMessage(e)); }
  }

  protected languageNames(codes: string[]) {
    return codes.map(c => this.languages().find(l => l.code === c)?.name ?? c).join(', ');
  }

  async search(w: WantedSubtitle) {
    await this.api.searchSubtitles({ movieFileId: w.movieFileId, episodeFileId: w.episodeFileId });
    this.toasts.show(`Searching subtitles for ${w.label}…`);
  }

  async searchAll() {
    const allowed = new Set(this.availableProviders().map(p => p.id));
    const providers = this.selectedProviders().filter(id => allowed.has(id));
    await (providers.length > 0 ? this.api.searchSubtitles({ providers }) : this.api.searchSubtitles());
    const names = this.availableProviders().filter(p => providers.includes(p.id)).map(p => p.name);
    this.toasts.show(names.length > 0
      ? `Searching subtitles for all wanted files (${names.join(', ')})…`
      : 'Searching subtitles for all wanted files…');
  }
}
