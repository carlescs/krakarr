import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api, errorMessage } from '../core/api';
import { Indexer, IndexerCategory, IndexerTestResult } from '../core/models';
import { Toasts } from '../core/toast';
import { CategoryPicker } from '../shared/category-picker';
import { Modal } from '../shared/modal';

const MOVIE_RANGE: [number, number] = [2000, 2999];
const TV_RANGE: [number, number] = [5000, 5999];

const blankIndexer = (): Indexer => ({
  id: 0, name: '', protocol: 'Torznab', baseUrl: '', apiPath: '/api', apiKey: '', movieCategories: [2000], tvCategories: [5000],
  enabled: true, enableRss: true, priority: 25, minimumSeeders: 1,
});

@Component({
  selector: 'kr-indexers',
  imports: [FormsModule, Modal, CategoryPicker],
  template: `
    <div class="mb-6 flex items-center gap-3">
      <h1 class="page-title mr-auto">Indexers</h1>
      <button class="btn-primary" (click)="edit(null)">+ Add indexer</button>
    </div>

    @if (indexers().length === 0) {
      <div class="card px-6 py-12 text-center text-sm text-slate-400">
        <p class="mb-1 text-base font-medium text-white">No indexers yet</p>
        Add any Torznab or Newznab compatible feed (trackers with Torznab APIs, Jackett feeds, …).
      </div>
    } @else {
      <div class="card overflow-hidden">
        <table class="table">
          <thead><tr><th>Name</th><th>Protocol</th><th class="hidden md:table-cell">URL</th><th>Priority</th><th>Status</th><th></th></tr></thead>
          <tbody>
            @for (i of indexers(); track i.id) {
              <tr>
                <td class="font-medium text-white">{{ i.name }}</td>
                <td>{{ i.protocol }}</td>
                <td class="hidden max-w-xs truncate font-mono text-xs text-slate-400 md:table-cell">{{ i.baseUrl }}</td>
                <td>{{ i.priority }}</td>
                <td>
                  <span class="badge" [class]="i.enabled ? 'bg-emerald-500/15 text-emerald-300' : 'bg-slate-500/15 text-slate-400'">{{ i.enabled ? 'Enabled' : 'Disabled' }}</span>
                  @if (i.enableRss) { <span class="badge ml-1 bg-surface-700 text-slate-300">RSS</span> }
                </td>
                <td class="text-right"><button class="btn-ghost btn-sm" (click)="edit(i)">Edit</button></td>
              </tr>
            }
          </tbody>
        </table>
      </div>
    }

    @if (form(); as f) {
      <kr-modal [title]="f.id ? 'Edit indexer' : 'Add indexer'" width="max-w-2xl" (closed)="form.set(null)">
        <div class="grid gap-4 sm:grid-cols-2">
          <div class="sm:col-span-2"><label class="label">Name</label><input class="input" [(ngModel)]="f.name" /></div>
          <div>
            <label class="label">Protocol</label>
            <select class="input" [(ngModel)]="f.protocol"><option>Torznab</option><option>Newznab</option></select>
          </div>
          <div><label class="label">API path</label><input class="input font-mono" [(ngModel)]="f.apiPath" /></div>
          <div class="sm:col-span-2">
            <label class="label">URL</label>
            <input class="input font-mono" [(ngModel)]="f.baseUrl" placeholder="https://tracker.example/api/torznab" />
            <p class="hint">Base URL of the Torznab endpoint. The API path is appended (Jackett: …/torznab with path “/api”).</p>
          </div>
          <div class="sm:col-span-2"><label class="label">API key</label><input class="input font-mono" [(ngModel)]="f.apiKey" autocomplete="off" /></div>
          <div class="flex items-center gap-2 sm:col-span-2">
            <span class="text-sm text-slate-400">
              @if (categories().length > 0) { {{ categories().length }} categories from the indexer. }
              @else if (capsError()) { <span class="text-rose-300">Could not load categories: {{ capsError() }}</span> }
              @else { Categories can be loaded from the indexer once the URL and API key are filled in. }
            </span>
            <span class="flex-1"></span>
            <button type="button" class="btn-secondary btn-sm" (click)="loadCategories(f)" [disabled]="loadingCaps() || !f.baseUrl">
              {{ loadingCaps() ? 'Loading…' : categories().length > 0 ? 'Reload categories' : 'Load categories' }}
            </button>
          </div>
          <div>
            <label class="label">Movie categories</label>
            @if (categories().length > 0) {
              <kr-category-picker [categories]="categories()" [range]="movieRange" [(selected)]="f.movieCategories" />
            } @else {
              <input class="input" [ngModel]="f.movieCategories.join(',')" (ngModelChange)="f.movieCategories = parseCats($event)" />
            }
          </div>
          <div>
            <label class="label">TV categories</label>
            @if (categories().length > 0) {
              <kr-category-picker [categories]="categories()" [range]="tvRange" [(selected)]="f.tvCategories" />
            } @else {
              <input class="input" [ngModel]="f.tvCategories.join(',')" (ngModelChange)="f.tvCategories = parseCats($event)" />
            }
          </div>
          <div><label class="label">Priority</label><input class="input" type="number" min="1" max="50" [(ngModel)]="f.priority" /><p class="hint">Lower wins ties between equal releases.</p></div>
          <div><label class="label">Minimum seeders</label><input class="input" type="number" min="0" [(ngModel)]="f.minimumSeeders" /></div>
          <label class="flex items-center gap-2 text-sm"><input type="checkbox" class="accent-accent-500" [(ngModel)]="f.enabled" /> Enabled</label>
          <label class="flex items-center gap-2 text-sm"><input type="checkbox" class="accent-accent-500" [(ngModel)]="f.enableRss" /> Use for RSS sync</label>
        </div>
        @if (test(); as t) {
          <div class="mt-4 rounded-lg px-3 py-2 text-sm" [class]="t.success ? 'bg-emerald-500/10 text-emerald-300' : 'bg-rose-500/10 text-rose-300'">
            @if (t.success) {
              Connection OK.
              Movie search: {{ t.capabilities?.movieSearchAvailable ? 'yes (' + t.capabilities?.movieSearchParams?.join(', ') + ')' : 'no' }} ·
              TV search: {{ t.capabilities?.tvSearchAvailable ? 'yes' : 'no' }}
            } @else { {{ t.message }} }
          </div>
        }
        <ng-container footer>
          @if (f.id) { <button class="btn-danger mr-auto" (click)="remove(f)">Delete</button> }
          <button class="btn-secondary" (click)="runTest(f)" [disabled]="testing()">{{ testing() ? 'Testing…' : 'Test' }}</button>
          <button class="btn-primary" (click)="save(f)" [disabled]="saving()">Save</button>
        </ng-container>
      </kr-modal>
    }
  `,
})
export class IndexersPage implements OnInit {
  private readonly api = inject(Api);
  private readonly toasts = inject(Toasts);
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

  ngOnInit() { this.load(); }

  private async load() { this.indexers.set(await this.api.indexers()); }

  protected parseCats(v: string) { return v.split(',').map(s => parseInt(s.trim(), 10)).filter(n => !isNaN(n)); }

  edit(i: Indexer | null) {
    this.test.set(null);
    this.categories.set([]);
    this.capsError.set(null);
    const form = i ? structuredClone(i) : blankIndexer();
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
