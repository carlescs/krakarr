import { Component, DestroyRef, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { auditTime } from 'rxjs';
import { Api, errorMessage } from '../../core/api';
import { Events } from '../../core/events';
import { ScoredSubtitle, SubtitleFile, SubtitleFileRef } from '../../core/models';
import { SubtitleLanguages } from '../../core/subtitles';
import { Toasts } from '../../core/toast';
import { Modal } from '../modal/modal';
import { SubtitleList } from '../subtitle-list/subtitle-list';
import { SubtitleResults, resultKey } from '../subtitle-results/subtitle-results';

/** Search scopes besides a single language code. */
export const SEARCH_MISSING = '@missing';
export const SEARCH_WANTED = '@wanted';

/** A subtitle event per downloaded file arrives during bulk searches: reload once they settle. */
const RELOAD_DELAY_MS = 1000;

/**
 * Everything about one video file's subtitles in a single dialog: what it has (with delete and timing tools), which
 * wanted languages are missing, and the provider results for them, searched as soon as it opens. The top pick per
 * language is marked (what "best match" offers, even below the minimum score) and can be downloaded in one go.
 */
@Component({
  selector: 'kr-subtitle-manager',
  imports: [FormsModule, Modal, SubtitleList, SubtitleResults],
  templateUrl: './subtitle-manager.html',
  styleUrl: './subtitle-manager.scss',
})
export class SubtitleManager implements OnInit {
  readonly fileRef = input.required<SubtitleFileRef>();
  readonly label = input('');
  /** Language to search first (e.g. a missing language clicked on a detail page); otherwise the missing ones. */
  readonly language = input<string | null>(null);
  readonly closed = output<void>();
  /** Subtitles were downloaded, deleted, retimed or the file was marked "not found". */
  readonly changed = output<void>();

  private readonly api = inject(Api);
  private readonly toasts = inject(Toasts);
  private readonly events = inject(Events);
  private readonly destroyRef = inject(DestroyRef);
  protected readonly languages = inject(SubtitleLanguages);

  protected readonly MISSING = SEARCH_MISSING;
  protected readonly WANTED = SEARCH_WANTED;

  protected readonly files = signal<SubtitleFile[]>([]);
  protected readonly filesLoaded = signal(false);
  protected readonly missing = computed(() => this.languages.missing(this.files()));
  protected readonly wanted = computed(() => {
    const w = this.languages.wanted();
    return w?.enabled ? w.languages : [];
  });

  /** What the last search looked for: SEARCH_MISSING, SEARCH_WANTED or a language code ('' before the first). */
  protected readonly scope = signal('');
  protected readonly results = signal<ScoredSubtitle[]>([]);
  protected readonly errors = signal<string[]>([]);
  protected readonly minimumScore = signal<number | null>(null);
  protected readonly searching = signal(false);
  protected readonly searched = signal(false);
  protected readonly downloading = signal<string | null>(null);
  protected readonly downloaded = signal<ReadonlySet<string>>(new Set());
  protected readonly busy = signal(false);
  /** Narrows the results to one language when several were searched. */
  protected readonly languageFilter = signal<string | null>(null);
  private generation = 0;

  /** Languages among the results, with how many results each has, in the order the API ranked them. */
  protected readonly resultLanguages = computed(() => {
    const counts = new Map<string, number>();
    for (const r of this.results()) counts.set(r.candidate.language, (counts.get(r.candidate.language) ?? 0) + 1);
    return [...counts].map(([code, count]) => ({ code, count, name: this.languages.name(code) }));
  });
  protected readonly shown = computed(() => {
    const language = this.languageFilter();
    return language ? this.results().filter(r => r.candidate.language === language) : this.results();
  });
  /** The top result for each language the file is still missing, not downloaded yet. */
  protected readonly topPicks = computed(() => {
    const missing = new Set(this.missing());
    const done = this.downloaded();
    return this.results().filter(r => r.best && missing.has(r.candidate.language) && !done.has(resultKey(r)));
  });

  protected readonly topPickNames = computed(() => this.names(this.topPicks().map(r => r.candidate.language)));

  async ngOnInit() {
    this.events.subtitlesUpdated.pipe(auditTime(RELOAD_DELAY_MS), takeUntilDestroyed(this.destroyRef)).subscribe(() => void this.loadFiles());
    await Promise.all([this.languages.load().catch(() => {}), this.loadFiles()]);
    const initial = this.language() ?? (this.missing().length > 0 ? SEARCH_MISSING : this.wanted().length > 0 ? SEARCH_WANTED : '');
    if (initial) await this.searchIn(initial);
  }

  protected names(codes: readonly string[]) { return codes.map(c => this.languages.name(c)).join(', '); }

  protected async loadFiles() {
    try {
      this.files.set(await this.api.subtitleFiles(this.fileRef()));
    } catch (e) {
      this.toasts.error(errorMessage(e));
    } finally {
      this.filesLoaded.set(true);
    }
  }

  /** Searches providers in another scope (missing, all wanted or one language). */
  async searchIn(scope: string) {
    this.scope.set(scope);
    this.languageFilter.set(null);
    await this.search();
  }

  async search() {
    const scope = this.scope();
    if (!scope) return;
    const languages = scope === SEARCH_MISSING ? this.missing() : scope === SEARCH_WANTED ? undefined : scope;
    const generation = ++this.generation;
    this.searching.set(true);
    try {
      const res = await this.api.subtitleCandidates(this.fileRef(), languages);
      if (generation !== this.generation) return;
      this.results.set(res.results);
      this.errors.set(res.errors);
      this.minimumScore.set(res.minimumScore ?? null);
      this.searched.set(true);
    } catch (e) {
      if (generation === this.generation) this.toasts.error(errorMessage(e));
    } finally {
      if (generation === this.generation) this.searching.set(false);
    }
  }

  /**
   * Downloads one result. Picked from the list it counts as chosen by hand (automatic upgrades leave it alone); a top
   * pick doesn't, so a better subtitle can still replace it later, as with "best match".
   */
  async download(r: ScoredSubtitle, manual = true): Promise<boolean> {
    this.downloading.set(resultKey(r));
    try {
      await this.api.downloadSubtitle(this.fileRef(), r.candidate, r.score, manual);
      this.downloaded.update(s => new Set(s).add(resultKey(r)));
      this.changed.emit();
      return true;
    } catch (e) {
      this.toasts.error(`${this.languages.name(r.candidate.language)}: ${errorMessage(e)}`);
      return false;
    } finally {
      this.downloading.set(null);
    }
  }

  protected async pick(r: ScoredSubtitle) {
    if (await this.download(r)) {
      this.toasts.success(`Downloaded ${this.languages.name(r.candidate.language)} subtitle`);
      await this.loadFiles();
    }
  }

  /** Downloads the top pick of every missing language. */
  async downloadTopPicks() {
    const picks = this.topPicks();
    let ok = 0;
    for (const r of picks) if (await this.download(r, false)) ok++;
    if (ok > 0) this.toasts.success(`Downloaded ${ok} subtitle${ok === 1 ? '' : 's'}`);
    await this.loadFiles();
  }

  /** Leaves it to Krakarr: searches in the background and downloads what meets the minimum score. */
  async auto() {
    try {
      await this.api.searchSubtitles(this.fileRef());
      this.toasts.show('Searching in the background — subtitles appear here when found');
    } catch (e) { this.toasts.error(errorMessage(e)); }
  }

  /** Nothing good enough: stops the scheduled search for this file until it is retried. */
  async markNotFound() {
    this.busy.set(true);
    try {
      await this.api.markSubtitlesNotFound(this.fileRef());
      this.toasts.show(`${this.label() || 'File'} won't be searched automatically until you retry it`);
      this.changed.emit();
      this.closed.emit();
    } catch (e) {
      this.toasts.error(errorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }

  protected filesChanged() {
    void this.loadFiles();
    this.changed.emit();
  }
}
