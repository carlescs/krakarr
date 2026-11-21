import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api, errorMessage } from '../../core/api';
import { Indexer, IndexerCategory, IndexerTestResult } from '../../core/models';
import { Languages } from '../../core/languages';
import { Toasts } from '../../core/toast';
import { CategoryPicker } from '../../shared/category-picker/category-picker';
import { Modal } from '../../shared/modal/modal';

const MOVIE_RANGE: [number, number] = [2000, 2999];
const TV_RANGE: [number, number] = [5000, 5999];

const blankIndexer = (): Indexer => ({
  id: 0, name: '', protocol: 'Torznab', baseUrl: '', apiPath: '/api', apiKey: '', movieCategories: [2000], tvCategories: [5000],
  enabled: true, enableRss: true, priority: 25, minimumSeeders: 1, defaultLanguage: null,
});

@Component({
  selector: 'kr-indexers',
  imports: [FormsModule, Modal, CategoryPicker],
  templateUrl: './indexers.html',
  styleUrl: './indexers.scss',
})
export class IndexersPage implements OnInit {
  private readonly api = inject(Api);
  private readonly toasts = inject(Toasts);
  protected readonly languages = inject(Languages);
  protected readonly indexers = signal<Indexer[]>([]);
  protected readonly form = signal<Indexer | null>(null);
  protected readonly test = signal<IndexerTestResult | null>(null);
  protected readonly testing = signal(false);
  protected readonly saving = signal(false);
  protected readonly categories = signal<IndexerCategory[]>([]);
  protected readonly capsError = signal<string | null>(null);
  protected readonly loadingCaps = signal(false);
  protected readonly movieRange = MOVIE_RANGE;
  protected readonly tvRange = TV_RANGE;

  ngOnInit() {
    this.load();
    this.languages.load().catch(() => {});
  }

  private async load() { this.indexers.set(await this.api.indexers()); }

  protected parseCats(v: string) { return v.split(',').map(s => parseInt(s.trim(), 10)).filter(n => !isNaN(n)); }

  edit(i: Indexer | null) {
    this.test.set(null);
    this.categories.set([]);
    this.capsError.set(null);
    const form = i ? { ...structuredClone(i), defaultLanguage: i.defaultLanguage ?? null } : blankIndexer();
    this.form.set(form);
    if (i) this.loadCategories(form);
  }

  async loadCategories(f: Indexer) {
    this.loadingCaps.set(true);
    this.capsError.set(null);
    try {
      const result = await this.api.indexerCapabilities(f);
      if (result.success && result.capabilities) this.applyCategories(f, result.capabilities.categories);
      else this.capsError.set(result.message ?? 'Unknown error');
    } catch (e) {
      this.capsError.set(errorMessage(e));
    } finally {
      this.loadingCaps.set(false);
    }
  }

  /** Shows the indexer's categories; if none of the current selection exists there, picks the standard ones it offers. */
  private applyCategories(f: Indexer, categories: IndexerCategory[]) {
    this.categories.set(categories);
    const known = new Set(categories.map(c => c.id));
    const suggest = ([min, max]: [number, number]) => {
      const inRange = categories.filter(c => c.id >= min && c.id <= max);
      const parents = inRange.filter(c => c.parentId == null);
      return (parents.length > 0 ? parents : inRange).map(c => c.id);
    };
    if (categories.length > 0 && !f.movieCategories.some(id => known.has(id))) f.movieCategories = suggest(MOVIE_RANGE);
    if (categories.length > 0 && !f.tvCategories.some(id => known.has(id))) f.tvCategories = suggest(TV_RANGE);
    this.form.set({ ...f });
  }

  async runTest(f: Indexer) {
    this.testing.set(true);
    try {
      const result = await this.api.testIndexer(f);
      this.test.set(result);
      if (result.success && result.capabilities) this.applyCategories(f, result.capabilities.categories);
    }
    catch (e) { this.test.set({ success: false, message: errorMessage(e) }); }
    finally { this.testing.set(false); }
  }

  async save(f: Indexer) {
    this.saving.set(true);
    try {
      await this.api.saveIndexer(f);
      this.toasts.success(`Saved ${f.name}`);
      this.form.set(null);
      await this.load();
    } catch (e) {
      this.toasts.error(errorMessage(e));
    } finally {
      this.saving.set(false);
    }
  }

  async remove(f: Indexer) {
    if (!confirm(`Delete indexer ${f.name}?`)) return;
    await this.api.deleteIndexer(f.id);
    this.form.set(null);
    await this.load();
  }
}
