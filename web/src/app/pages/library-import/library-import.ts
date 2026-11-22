import { Component, OnInit, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { Api, errorMessage } from '../../core/api';
import { AVAILABILITY_OPTIONS } from '../../core/calendar';
import { BytesPipe } from '../../core/format';
import { MatchConfidence, MinimumAvailability, MonitorMode, MovieLookup, QualityProfile, RootFolder, SeriesLookup } from '../../core/models';
import { Toasts } from '../../core/toast';
import { Modal } from '../../shared/modal/modal';
import { Poster } from '../../shared/poster/poster';
import { TableSort } from '../../core/table-sort';
import { SortHeader } from '../../shared/sort-header/sort-header';

type Kind = 'movies' | 'series';
type Filter = 'all' | 'matched' | 'unmatched' | 'existing';
type Match = { id: number; tvdbId?: number; title: string; year: number; posterUrl?: string; overview?: string };

const CONFIDENCE: MatchConfidence[] = ['None', 'Low', 'Medium', 'High', 'Exact'];

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
  imports: [FormsModule, RouterLink, Poster, Modal, SortHeader],
  templateUrl: './library-import.html',
  styleUrl: './library-import.scss',
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
  protected readonly sort = new TableSort(this.visible, {
    folder: r => r.folderName,
    files: r => r.files,
    // Unmatched folders go last.
    match: { value: r => r.match ? CONFIDENCE.indexOf(r.confidence) : null, desc: true },
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
