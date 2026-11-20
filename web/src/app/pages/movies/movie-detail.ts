import { Component, DestroyRef, OnInit, computed, inject, input, numberAttribute, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { DatePipe } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { Api, errorMessage } from '../../core/api';
import { Events } from '../../core/events';
import { AgoPipe, BytesPipe, DurationPipe, SpeedPipe } from '../../core/format';
import { HistoryItem, IndexerError, Movie, QualityProfile, ReleaseDecision, SubtitleFile } from '../../core/models';
import { Toasts } from '../../core/toast';
import { Modal } from '../../shared/modal';
import { Poster } from '../../shared/poster';
import { Progress } from '../../shared/progress';
import { StateBadge } from '../../shared/state-badge';
import { SubtitleManager } from '../../shared/subtitle-manager';

@Component({
  selector: 'kr-movie-detail',
  imports: [FormsModule, DatePipe, Poster, Progress, Modal, StateBadge, SubtitleManager, BytesPipe, SpeedPipe, DurationPipe, AgoPipe],
  template: `
    @if (movie(); as m) {
      <!-- Hero -->
      <div class="relative -mx-4 -mt-6 mb-6 overflow-hidden lg:-mx-8">
        @if (m.fanartUrl) {
          <img [src]="m.fanartUrl" alt="" class="absolute inset-0 h-full w-full object-cover opacity-20" />
        }
        <div class="absolute inset-0 bg-gradient-to-t from-surface-950 via-surface-950/80 to-surface-950/40"></div>
        <div class="relative flex flex-col gap-5 px-4 py-6 sm:flex-row sm:gap-6 sm:py-8 lg:px-8">
          <kr-poster class="w-32 shrink-0 shadow-2xl sm:w-48" [url]="m.posterUrl" [title]="m.title" />
          <div class="min-w-0 flex-1">
            <h1 class="text-2xl font-bold tracking-tight text-white sm:text-3xl">{{ m.title }} <span class="font-normal text-slate-400">({{ m.year }})</span></h1>
            <div class="mt-2 flex flex-wrap items-center gap-2 text-sm text-slate-400">
              @if (m.runtime) { <span>{{ m.runtime }} min</span><span>·</span> }
              <span>{{ m.status === 'InCinemas' ? 'In cinemas' : m.status }}</span>
              @if (m.imdbId) { <span>·</span><a class="hover:text-white" [href]="'https://www.imdb.com/title/' + m.imdbId" target="_blank" rel="noopener">IMDb</a> }
              <span>·</span><a class="hover:text-white" [href]="'https://www.themoviedb.org/movie/' + m.tmdbId" target="_blank" rel="noopener">TMDB</a>
            </div>
            <div class="mt-3 flex flex-wrap gap-2">
              @if (m.hasFile) {
                <span class="badge bg-emerald-500/15 text-emerald-300">Downloaded · {{ m.movieFile?.quality }}</span>
              } @else if (m.monitored) {
                <span class="badge bg-rose-500/15 text-rose-300">Missing</span>
              }
              <span class="badge" [class]="m.monitored ? 'bg-accent-500/15 text-accent-300' : 'bg-slate-500/15 text-slate-400'">
                {{ m.monitored ? 'Monitored' : 'Unmonitored' }}
              </span>
              <span class="badge bg-surface-700 text-slate-300">{{ profileName() }}</span>
            </div>
            <p class="mt-4 max-w-3xl text-sm leading-relaxed text-slate-300">{{ m.overview }}</p>
            <p class="mt-3 break-all font-mono text-xs text-slate-500">{{ m.path }}</p>
            <div class="mt-5 flex flex-wrap gap-2">
              <button class="btn-primary" (click)="autoSearch()">Automatic search</button>
              <button class="btn-secondary" (click)="interactiveSearch()">Interactive search</button>
              <button class="btn-secondary" (click)="toggleMonitored()">{{ m.monitored ? 'Unmonitor' : 'Monitor' }}</button>
              <button class="btn-ghost" (click)="editing.set(true)">Edit</button>
              <button class="btn-ghost text-rose-300" (click)="confirmDelete.set(true)">Delete</button>
            </div>
          </div>
        </div>
      </div>

      <!-- Active downloads -->
      @if (downloads().length > 0) {
        <section class="card mb-6 p-4 sm:p-5">
          <h2 class="mb-3 text-sm font-semibold uppercase tracking-wide text-slate-400">Download</h2>
          @for (d of downloads(); track d.infoHash) {
            <div class="space-y-2">
              <div class="flex flex-wrap items-center gap-2 text-sm">
                <kr-state-badge [state]="d.state" />
                <span class="min-w-0 flex-1 truncate font-medium text-slate-200">{{ d.name }}</span>
                <span class="text-slate-400">{{ d.progress.toFixed(1) }}% · {{ d.downloadRate | speed }} · ETA {{ d.etaSeconds | duration }}</span>
              </div>
              <kr-progress [value]="d.progress" />
              @if (d.importMessage) { <p class="text-xs text-rose-300">{{ d.importMessage }}</p> }
            </div>
          }
        </section>
      }

      <!-- File -->
      @if (m.movieFile; as f) {
        <section class="card mb-6 p-4 sm:p-5">
          <h2 class="mb-3 text-sm font-semibold uppercase tracking-wide text-slate-400">File</h2>
          <div class="grid grid-cols-[auto_1fr] gap-x-4 gap-y-2 text-sm sm:gap-x-8">
            <span class="text-slate-500">Path</span><span class="break-all font-mono text-xs text-slate-300">{{ f.relativePath }}</span>
            <span class="text-slate-500">Size</span><span>{{ f.size | bytes }}</span>
            <span class="text-slate-500">Quality</span><span>{{ f.quality }}</span>
            <span class="text-slate-500">Group</span><span>{{ f.releaseGroup || '—' }}</span>
            <span class="text-slate-500">Added</span><span>{{ f.dateAdded | date: 'medium' }}</span>
            <span class="text-slate-500">Subtitles</span>
            <span class="flex flex-wrap items-center gap-1.5">
              @for (s of subtitles(); track s.id) {
                <span class="badge bg-surface-700 text-slate-200" [title]="s.provider + (s.score != null ? ' · score ' + s.score : '')">{{ s.languageName }}{{ s.hearingImpaired ? ' (HI)' : '' }}</span>
              } @empty { <span class="text-slate-500">none</span> }
              <button class="btn-ghost btn-sm" (click)="subtitlesOpen.set(true)">Manage</button>
            </span>
          </div>
        </section>
        @if (subtitlesOpen()) {
          <kr-subtitle-manager [fileRef]="{ movieFileId: f.id }" [label]="m.title" [files]="subtitles()"
                               (closed)="subtitlesOpen.set(false)" (changed)="loadSubtitles()" />
        }
      }

      <!-- History -->
      <section class="card p-4 sm:p-5">
        <h2 class="mb-3 text-sm font-semibold uppercase tracking-wide text-slate-400">History</h2>
        @if (history().length === 0) {
          <p class="text-sm text-slate-500">Nothing yet.</p>
        } @else {
          <ul class="divide-y divide-surface-800 text-sm">
            @for (h of history(); track h.id) {
              <li class="flex flex-wrap items-center gap-3 py-2">
                <span class="badge w-20 justify-center sm:w-24"
                      [class]="h.eventType === 'Imported' ? 'bg-emerald-500/15 text-emerald-300' : h.eventType === 'Grabbed' ? 'bg-accent-500/15 text-accent-300' : h.eventType === 'SubtitleDownloaded' ? 'bg-violet-500/15 text-violet-300' : 'bg-rose-500/15 text-rose-300'">
                  {{ h.eventType === 'DownloadFailed' ? 'Failed' : h.eventType === 'SubtitleDownloaded' ? 'Subtitle' : h.eventType }}
                </span>
                <span class="min-w-0 flex-1 truncate max-sm:order-1 max-sm:basis-full" [title]="h.sourceTitle">{{ h.sourceTitle }}</span>
                <span class="text-xs text-slate-500">{{ h.quality }}</span>
                <span class="text-right text-xs text-slate-500 max-sm:ml-auto sm:w-24" [title]="h.date">{{ h.date | ago }}</span>
                @if (h.message) { <span class="w-full text-xs text-slate-500 max-sm:order-2 sm:pl-27">{{ h.message }}</span> }
              </li>
            }
          </ul>
        }
      </section>

      <!-- Interactive search -->
      @if (releasesOpen()) {
        <kr-modal title="Interactive search" width="max-w-6xl" (closed)="releasesOpen.set(false)">
          @if (searching()) {
            <p class="py-10 text-center text-slate-400">Searching indexers…</p>
          } @else {
            @for (e of searchErrors(); track e.indexerId) {
              <p class="mb-2 rounded bg-amber-500/10 px-3 py-2 text-xs text-amber-300">{{ e.indexerName }}: {{ e.message }}</p>
            }
            @if (releases().length === 0) {
              <p class="py-10 text-center text-slate-400">No results.</p>
            } @else {
              <div class="overflow-x-auto sm:max-h-[65vh] sm:overflow-auto">
                <table class="table">
                  <thead><tr><th>Release</th><th class="max-sm:hidden">Indexer</th><th class="max-sm:hidden">Size</th><th class="max-sm:hidden">Peers</th><th class="max-sm:hidden">Quality</th><th></th></tr></thead>
                  <tbody>
                    @for (r of releases(); track r.release.guid) {
                      <tr [class.opacity-60]="!r.approved">
                        <td class="w-full max-w-0">
                          <div class="break-all sm:truncate sm:break-normal" [title]="r.release.title">{{ r.release.title }}</div>
                          <div class="mt-1 flex flex-wrap items-center gap-x-2 gap-y-1 text-xs text-slate-400 sm:hidden">
                            <span class="badge bg-surface-700 text-slate-200">{{ r.quality }}</span>
                            <span>{{ r.release.size | bytes }}</span>
                            <span><span class="text-emerald-400">{{ r.release.seeders ?? '?' }}</span> / {{ r.release.peers ?? '?' }}</span>
                            <span>{{ r.release.indexerName }}</span>
                          </div>
                          @if (!r.approved) { <div class="text-xs text-rose-300">{{ r.rejections.join(' · ') }}</div> }
                        </td>
                        <td class="text-slate-400 max-sm:hidden">{{ r.release.indexerName }}</td>
                        <td class="whitespace-nowrap max-sm:hidden">{{ r.release.size | bytes }}</td>
                        <td class="whitespace-nowrap max-sm:hidden"><span class="text-emerald-400">{{ r.release.seeders ?? '?' }}</span> / {{ r.release.peers ?? '?' }}</td>
                        <td class="max-sm:hidden"><span class="badge bg-surface-700 text-slate-200">{{ r.quality }}</span></td>
                        <td class="text-right">
                          <button class="btn-sm" [class]="r.approved ? 'btn-primary' : 'btn-secondary'" (click)="grab(r)" [disabled]="grabbing() === r.release.guid">
                            {{ grabbing() === r.release.guid ? '…' : 'Grab' }}
                          </button>
                        </td>
                      </tr>
                    }
                  </tbody>
                </table>
              </div>
            }
          }
        </kr-modal>
      }

      @if (editing()) {
        <kr-modal [title]="'Edit ' + m.title" (closed)="editing.set(false)">
          <div class="space-y-4">
            <label class="flex items-center gap-2 text-sm"><input type="checkbox" class="accent-accent-500" [(ngModel)]="editMonitored" /> Monitored</label>
            <div>
              <label class="label">Quality profile</label>
              <select class="input" [(ngModel)]="editProfileId">
                @for (p of profiles(); track p.id) { <option [ngValue]="p.id">{{ p.name }}</option> }
              </select>
            </div>
          </div>
          <ng-container footer>
            <button class="btn-ghost" (click)="editing.set(false)">Cancel</button>
            <button class="btn-primary" (click)="saveEdit()">Save</button>
          </ng-container>
        </kr-modal>
      }

      @if (confirmDelete()) {
        <kr-modal [title]="'Delete ' + m.title" (closed)="confirmDelete.set(false)">
          <p class="text-sm text-slate-300">Remove this movie from the library?</p>
          <label class="mt-4 flex items-center gap-2 text-sm"><input type="checkbox" class="accent-rose-500" [(ngModel)]="deleteFiles" /> Also delete the movie folder from disk</label>
          <ng-container footer>
            <button class="btn-ghost" (click)="confirmDelete.set(false)">Cancel</button>
            <button class="btn-danger" (click)="delete()">Delete</button>
          </ng-container>
        </kr-modal>
      }
    } @else {
      <p class="text-slate-500">Loading…</p>
    }
  `,
})
export class MovieDetailPage implements OnInit {
  readonly id = input.required({ transform: numberAttribute });

  private readonly api = inject(Api);
  private readonly toasts = inject(Toasts);
  private readonly events = inject(Events);
  private readonly router = inject(Router);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly movie = signal<Movie | null>(null);
  protected readonly history = signal<HistoryItem[]>([]);
  protected readonly profiles = signal<QualityProfile[]>([]);
  protected readonly releases = signal<ReleaseDecision[]>([]);
  protected readonly searchErrors = signal<IndexerError[]>([]);
  protected readonly releasesOpen = signal(false);
  protected readonly searching = signal(false);
  protected readonly grabbing = signal<string | null>(null);
  protected readonly editing = signal(false);
  protected readonly confirmDelete = signal(false);
  protected readonly subtitles = signal<SubtitleFile[]>([]);
  protected readonly subtitlesOpen = signal(false);
  protected editMonitored = true;
  protected editProfileId = 0;
  protected deleteFiles = false;

  protected readonly downloads = computed(() => this.events.downloads().filter(d => d.movieId === this.id() && !d.imported));
  protected readonly profileName = computed(() => this.profiles().find(p => p.id === this.movie()?.qualityProfileId)?.name ?? '');

  ngOnInit() {
    this.load();
    this.api.qualityProfiles().then(p => this.profiles.set(p));
    this.events.movieUpdated.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(e => { if (e.id === this.id()) this.load(); });
    this.events.historyAdded.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(h => { if (h.movieId === this.id()) this.loadHistory(); });
    this.events.subtitlesUpdated.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(e => { if (e.movieId === this.id()) this.loadSubtitles(); });
  }

  async loadSubtitles() { this.subtitles.set(await this.api.subtitleFiles({ movieId: this.id() })); }

  private async load() {
    try {
      const m = await this.api.movie(this.id());
      this.movie.set(m);
      this.editMonitored = m.monitored;
      this.editProfileId = m.qualityProfileId;
      await Promise.all([this.loadHistory(), this.loadSubtitles()]);
    } catch (e) {
      this.toasts.error(errorMessage(e));
    }
  }

  private async loadHistory() {
    this.history.set((await this.api.history(this.id(), 1, 50)).items);
  }

  async autoSearch() {
    await this.api.searchMovie(this.id());
    this.toasts.show('Search started — results will appear in History');
  }

  async interactiveSearch() {
    this.releasesOpen.set(true);
    this.searching.set(true);
    try {
      const res = await this.api.movieReleases(this.id());
      this.releases.set(res.releases);
      this.searchErrors.set(res.errors);
    } catch (e) {
      this.toasts.error(errorMessage(e));
    } finally {
      this.searching.set(false);
    }
  }

  async grab(r: ReleaseDecision) {
    this.grabbing.set(r.release.guid);
    try {
      await this.api.grabMovieRelease(this.id(), r.release);
      this.toasts.success('Release sent to the download client');
      this.releasesOpen.set(false);
    } catch (e) {
      this.toasts.error(errorMessage(e));
    } finally {
      this.grabbing.set(null);
    }
  }

  async toggleMonitored() {
    const m = this.movie()!;
    await this.api.updateMovie(m.id, !m.monitored, m.qualityProfileId);
    this.movie.set({ ...m, monitored: !m.monitored });
    this.editMonitored = !m.monitored;
  }

  async saveEdit() {
    try {
      await this.api.updateMovie(this.id(), this.editMonitored, this.editProfileId);
      this.editing.set(false);
      await this.load();
    } catch (e) {
      this.toasts.error(errorMessage(e));
    }
  }

  async delete() {
    try {
      await this.api.deleteMovie(this.id(), this.deleteFiles);
      this.toasts.success('Movie removed');
      this.router.navigateByUrl('/movies');
    } catch (e) {
      this.toasts.error(errorMessage(e));
    }
  }
}
