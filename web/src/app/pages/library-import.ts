import { Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { Api, errorMessage } from '../core/api';
import { AVAILABILITY_OPTIONS } from '../core/calendar';
import { BytesPipe } from '../core/format';
import { MatchConfidence, MinimumAvailability, MonitorMode, MovieLookup, QualityProfile, RootFolder, SeriesLookup } from '../core/models';
import { Toasts } from '../core/toast';
import { Modal } from '../shared/modal';
import { Poster } from '../shared/poster';

type Kind = 'movies' | 'series';
type Filter = 'all' | 'matched' | 'unmatched' | 'existing';
type Match = { id: number; tvdbId?: number; title: string; year: number; posterUrl?: string; overview?: string };

interface Row {
  folderName: string;
  path: string;
  parsedTitle: string;
  parsedYear?: number;
  match?: Match;
  confidence: MatchConfidence;
  alreadyInLibrary: boolean;
  files: string;
  error?: string;
  selected: boolean;
}

const CONFIDENCE_STYLE: Record<MatchConfidence, string> = {
  Exact: 'bg-emerald-500/15 text-emerald-300',
  High: 'bg-emerald-500/15 text-emerald-300',
  Medium: 'bg-amber-500/15 text-amber-300',
  Low: 'bg-rose-500/15 text-rose-300',
  None: 'bg-slate-500/15 text-slate-400',
};

/** Import existing movie or series folders from a root folder, matched by folder name. */
@Component({
  selector: 'kr-library-import',
  imports: [FormsModule, RouterLink, Poster, Modal],
  template: `
    <div class="mb-6 flex flex-wrap items-center gap-3">
      <h1 class="page-title mr-auto">Import {{ isMovies() ? 'movie' : 'series' }} library</h1>
      <a [routerLink]="isMovies() ? '/movies' : '/series'" class="btn-ghost">Back</a>
    </div>

    @if (rootFolders().length === 0) {
      <div class="card px-6 py-10 text-center text-sm text-slate-400">
        Add a root folder of type “{{ isMovies() ? 'Movies' : 'Series' }}” in <a routerLink="/settings" class="underline">Settings → Media management</a> first.
      </div>
    } @else {
      <section class="card mb-6 grid gap-4 p-4 sm:p-5 md:grid-cols-[1fr_auto]">
        <div class="grid gap-4 sm:grid-cols-2 lg:grid-cols-4">
          <div>
            <label class="label">Root folder</label>
            <select class="input" [(ngModel)]="rootFolderId">
              @for (f of rootFolders(); track f.id) { <option [ngValue]="f.id">{{ f.path }}</option> }
            </select>
          </div>
          <div>
            <label class="label">Quality profile</label>
            <select class="input" [(ngModel)]="profileId">
              @for (p of profiles(); track p.id) { <option [ngValue]="p.id">{{ p.name }}</option> }
            </select>
          </div>
          @if (isMovies()) {
            <div>
              <label class="label">Minimum availability</label>
              <select class="input" [(ngModel)]="minimumAvailability">
                @for (o of availabilityOptions; track o.value) { <option [value]="o.value">{{ o.label }}</option> }
              </select>
            </div>
            <label class="flex items-center gap-2 self-end pb-2 text-sm"><input type="checkbox" class="accent-accent-500" [(ngModel)]="monitored" /> Monitored</label>
          } @else {
            <div>
              <label class="label">Monitor</label>
              <select class="input" [(ngModel)]="monitor">
                <option value="All">All episodes</option><option value="Future">Future episodes</option><option value="Missing">Missing episodes</option>
                <option value="FirstSeason">First season</option><option value="LatestSeason">Latest season</option><option value="None">None</option>
              </select>
            </div>
          }
          <label class="flex items-center gap-2 self-end pb-2 text-sm">
            <input type="checkbox" class="accent-accent-500" [(ngModel)]="search" />
            {{ isMovies() ? 'Search if no file found' : 'Search for missing episodes' }}
          </label>
        </div>
        <button class="btn-primary self-end" (click)="scan()" [disabled]="scanning()">{{ scanning() ? 'Scanning…' : 'Scan folder' }}</button>
      </section>

      @if (scanned()) {
        <div class="mb-3 flex flex-wrap items-center gap-2">
          @for (f of filters; track f.id) {
            <button class="btn-sm" [class]="filter() === f.id ? 'btn-secondary' : 'btn-ghost'" (click)="filter.set(f.id)">
              {{ f.label }} <span class="text-slate-500">{{ count(f.id) }}</span>
            </button>
          }
          <span class="flex-1"></span>
          <button class="btn-ghost btn-sm" (click)="selectAll(true)">Select all matched</button>
          <button class="btn-ghost btn-sm" (click)="selectAll(false)">Select none</button>
          <button class="btn-primary" (click)="import()" [disabled]="selectedCount() === 0 || importing()">
            {{ importing() ? 'Importing…' : 'Import ' + selectedCount() + ' selected' }}
          </button>
        </div>

        @if (visible().length === 0) {
          <div class="card px-6 py-10 text-center text-sm text-slate-400">No folders here.</div>
        } @else {
          <div class="card overflow-x-auto">
            <table class="table">
              <thead><tr><th class="w-8"></th><th class="max-sm:hidden">Folder</th><th class="max-md:hidden">Files</th><th>Match</th><th class="w-12 sm:w-28"></th></tr></thead>
              <tbody>
                @for (r of visible(); track r.path) {
                  <tr [class.opacity-60]="!r.match || r.alreadyInLibrary">
                    <td>
                      <input type="checkbox" class="accent-accent-500" [(ngModel)]="r.selected" (ngModelChange)="touch()"
                             [disabled]="!r.match || r.alreadyInLibrary" [attr.aria-label]="'Select ' + r.folderName" />
                    </td>
                    <td class="max-w-xs max-sm:hidden">
                      <div class="truncate font-medium text-slate-100" [title]="r.path">{{ r.folderName }}</div>
                      <div class="text-xs text-slate-500">{{ r.parsedTitle }}{{ r.parsedYear ? ' (' + r.parsedYear + ')' : '' }}</div>
                    </td>
                    <td class="whitespace-nowrap text-xs text-slate-400 max-md:hidden">{{ r.files }}</td>
                    <td class="w-full max-w-0">
                      <div class="mb-1.5 truncate font-mono text-xs text-slate-400 sm:hidden" [title]="r.path">{{ r.folderName }}</div>
                      @if (r.match; as m) {
                        <div class="flex items-center gap-3">
                          @if (m.posterUrl) {
                            <img [src]="m.posterUrl" alt="" loading="lazy" class="aspect-[2/3] w-8 shrink-0 rounded object-cover" />
                          } @else {
                            <div class="aspect-[2/3] w-8 shrink-0 rounded bg-surface-700"></div>
                          }
                          <div class="min-w-0">
                            <div class="truncate text-slate-100">{{ m.title }} <span class="text-slate-500">{{ m.year || '' }}</span></div>
                            <div class="flex flex-wrap gap-1">
                              <span class="badge" [class]="style(r.confidence)">{{ r.confidence }}</span>
                              @if (r.alreadyInLibrary) { <span class="badge bg-slate-500/15 text-slate-400">Already in library</span> }
                            </div>
                          </div>
                        </div>
                      } @else {
                        <span class="text-sm text-slate-500">{{ r.error ?? 'No match' }}</span>
                      }
                    </td>
                    <td class="whitespace-nowrap text-right"><button class="btn-ghost btn-sm" (click)="openChange(r)" title="Change match"><span class="max-sm:hidden">Change match</span><span class="sm:hidden" aria-hidden="true">✎</span></button></td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        }
      }
    }

    @if (changing(); as row) {
      <kr-modal [title]="'Match ' + row.folderName" width="max-w-2xl" (closed)="changing.set(null)">
        <form class="mb-4 flex gap-2" (ngSubmit)="lookup()">
          <input class="input min-w-0 flex-1" name="term" [(ngModel)]="term" />
          <button class="btn-primary" [disabled]="looking()">{{ looking() ? 'Searching…' : 'Search' }}</button>
        </form>
        <div class="space-y-2 sm:max-h-[55vh] sm:overflow-y-auto">
          @for (c of lookupResults(); track c.id + '-' + (c.tvdbId ?? 0)) {
            <button class="flex w-full items-center gap-3 rounded-lg p-2 text-left hover:bg-surface-800" (click)="choose(row, c)">
              <kr-poster class="w-10 shrink-0" [url]="c.posterUrl" [title]="c.title" />
              <div class="min-w-0">
                <div class="text-slate-100">{{ c.title }} <span class="text-slate-500">{{ c.year || '' }}</span></div>
                <div class="line-clamp-2 text-xs text-slate-500">{{ c.overview }}</div>
              </div>
            </button>
          } @empty {
            @if (looked()) { <p class="py-6 text-center text-sm text-slate-500">No results.</p> }
          }
        </div>
      </kr-modal>
    }
  `,
})
export class LibraryImportPage implements OnInit {
  /** Route data: which library to import. */
  readonly kind = input<Kind>('movies');

  private readonly api = inject(Api);
  private readonly toasts = inject(Toasts);
  private readonly router = inject(Router);

  protected readonly filters: { id: Filter; label: string }[] = [
    { id: 'all', label: 'All' }, { id: 'matched', label: 'Matched' }, { id: 'unmatched', label: 'Unmatched' }, { id: 'existing', label: 'Already in library' },
  ];
  protected readonly isMovies = computed(() => this.kind() === 'movies');
  protected readonly rootFolders = signal<RootFolder[]>([]);
  protected readonly profiles = signal<QualityProfile[]>([]);
  protected readonly rows = signal<Row[]>([]);
  protected readonly scanning = signal(false);
  protected readonly scanned = signal(false);
  protected readonly importing = signal(false);
  protected readonly filter = signal<Filter>('all');
  protected readonly changing = signal<Row | null>(null);
  protected readonly lookupResults = signal<Match[]>([]);
  protected readonly looking = signal(false);
  protected readonly looked = signal(false);
  private readonly version = signal(0);
  protected rootFolderId = 0;
  protected profileId = 0;
  protected monitored = true;
  protected readonly availabilityOptions = AVAILABILITY_OPTIONS;
  protected minimumAvailability: MinimumAvailability = 'Released';
  protected monitor: MonitorMode = 'All';
  protected search = false;
  protected term = '';

  protected readonly visible = computed(() => {
    this.version();
    return this.rows().filter(r => this.matches(r, this.filter()));
  });
  protected readonly selectedCount = computed(() => {
    this.version();
    return this.rows().filter(r => r.selected).length;
  });

  async ngOnInit() {
    const [folders, profiles] = await Promise.all([this.api.rootFolders(), this.api.qualityProfiles()]);
    this.rootFolders.set(folders.filter(f => f.mediaType === (this.isMovies() ? 'Movie' : 'Series')));
    this.profiles.set(profiles);
    this.rootFolderId = this.rootFolders()[0]?.id ?? 0;
    this.profileId = profiles[0]?.id ?? 0;
  }

  protected style(c: MatchConfidence) { return CONFIDENCE_STYLE[c]; }
  protected touch() { this.version.update(v => v + 1); }

  private matches(r: Row, f: Filter) {
    switch (f) {
      case 'matched': return !!r.match && !r.alreadyInLibrary;
      case 'unmatched': return !r.match;
      case 'existing': return r.alreadyInLibrary;
      default: return true;
    }
  }

  protected count(f: Filter) { return this.rows().filter(r => this.matches(r, f)).length; }

  async scan() {
    this.scanning.set(true);
    try {
      const auto = (c: MatchConfidence) => c === 'Exact' || c === 'High' || c === 'Medium';
      if (this.isMovies()) {
        const result = await this.api.scanMovieImport(this.rootFolderId);
        this.rows.set(result.map(r => ({
          folderName: r.folderName, path: r.path, parsedTitle: r.parsedTitle, parsedYear: r.parsedYear, confidence: r.confidence,
          alreadyInLibrary: r.alreadyInLibrary, error: r.error, match: r.match ? this.fromMovie(r.match) : undefined,
          files: r.videoFile ? `${r.videoFile.quality} · ${new BytesPipe().transform(r.videoFile.size)}` : 'no video file',
          selected: !!r.match && !r.alreadyInLibrary && auto(r.confidence),
        })));
      } else {
        const result = await this.api.scanSeriesImport(this.rootFolderId);
        this.rows.set(result.map(r => ({
          folderName: r.folderName, path: r.path, parsedTitle: r.parsedTitle, parsedYear: r.parsedYear, confidence: r.confidence,
          alreadyInLibrary: r.alreadyInLibrary, error: r.error, match: r.match ? this.fromSeries(r.match) : undefined,
          files: `${r.episodeFileCount} episode file(s)` + (r.videoFileCount > r.episodeFileCount ? ` · ${r.videoFileCount - r.episodeFileCount} other` : ''),
          selected: !!r.match && !r.alreadyInLibrary && auto(r.confidence),
        })));
      }
      this.scanned.set(true);
      this.filter.set('all');
    } catch (e) {
      this.toasts.error(errorMessage(e));
    } finally {
      this.scanning.set(false);
    }
  }

  private fromMovie(m: MovieLookup): Match { return { id: m.tmdbId, title: m.title, year: m.year, posterUrl: m.posterUrl, overview: m.overview }; }
  private fromSeries(s: SeriesLookup): Match { return { id: s.tmdbId ?? 0, tvdbId: s.tvdbId, title: s.title, year: s.year, posterUrl: s.posterUrl, overview: s.overview }; }

  selectAll(on: boolean) {
    for (const r of this.rows()) r.selected = on && !!r.match && !r.alreadyInLibrary;
    this.touch();
  }

  openChange(r: Row) {
    this.term = r.parsedTitle + (r.parsedYear && this.isMovies() ? ` ${r.parsedYear}` : '');
    this.lookupResults.set([]);
    this.looked.set(false);
    this.changing.set(r);
    this.lookup();
  }

  async lookup() {
    this.looking.set(true);
    try {
      this.lookupResults.set(this.isMovies()
        ? (await this.api.lookupMovies(this.term)).map(m => this.fromMovie(m))
        : (await this.api.lookupSeries(this.term)).map(s => this.fromSeries(s)));
      this.looked.set(true);
    } catch (e) {
      this.toasts.error(errorMessage(e));
    } finally {
      this.looking.set(false);
    }
  }

  choose(row: Row, match: Match) {
    row.match = match;
    row.confidence = 'Exact';
    row.alreadyInLibrary = false;
    row.error = undefined;
    row.selected = true;
    this.changing.set(null);
    this.rows.update(list => [...list]);
    this.touch();
  }

  async import() {
    const selected = this.rows().filter(r => r.selected && r.match);
    this.importing.set(true);
    try {
      if (this.isMovies()) {
        await this.api.importMovies(selected.map(r => ({
          path: r.path, tmdbId: r.match!.id, qualityProfileId: this.profileId, monitored: this.monitored, searchForMovie: this.search, minimumAvailability: this.minimumAvailability,
        })));
      } else {
        await this.api.importSeries(selected.map(r => ({
          path: r.path, tmdbId: r.match!.id || undefined, tvdbId: r.match!.tvdbId, qualityProfileId: this.profileId, monitor: this.monitor, searchForMissing: this.search,
        })));
      }
      this.toasts.success(`Importing ${selected.length} folder(s) — progress is shown in Activity`);
      this.router.navigateByUrl(this.isMovies() ? '/movies' : '/series');
    } catch (e) {
      this.toasts.error(errorMessage(e));
    } finally {
      this.importing.set(false);
    }
  }
}
