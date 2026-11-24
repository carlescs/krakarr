import { Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { NgTemplateOutlet } from '@angular/common';
import { RouterLink } from '@angular/router';
import { debounceTime, merge } from 'rxjs';
import { Api, errorMessage } from '../../core/api';
import { Events } from '../../core/events';
import { AgoPipe, BytesPipe, DurationPipe, SpeedPipe } from '../../core/format';
import { DashboardStats, DownloadState, MediaType, RootFolder } from '../../core/models';
import { Poster } from '../../shared/poster/poster';
import { Progress } from '../../shared/progress/progress';
import { StateBadge } from '../../shared/state-badge/state-badge';
import { Upcoming } from './upcoming/upcoming';

/** One part of a segmented bar: its share of the whole and how it is drawn in the bar and the legend. */
export interface Segment { label: string; value: number; color: string; }

/** Download states summarised on the dashboard, in display order. */
const STATES: { state: DownloadState; label: string; color: string }[] = [
  { state: 'Downloading', label: 'Downloading', color: 'bg-accent-500' },
  { state: 'FetchingMetadata', label: 'Metadata', color: 'bg-violet-500' },
  { state: 'Queued', label: 'Queued', color: 'bg-slate-500' },
  { state: 'Paused', label: 'Paused', color: 'bg-amber-500' },
  { state: 'Seeding', label: 'Seeding', color: 'bg-emerald-500' },
  { state: 'Completed', label: 'Completed', color: 'bg-emerald-700' },
  { state: 'Error', label: 'Error', color: 'bg-rose-500' },
];

const ACTIVE: DownloadState[] = ['Downloading', 'FetchingMetadata', 'Queued'];

/** How many active downloads are listed; the rest are on the Downloads page. */
export const TOP_DOWNLOADS = 5;

@Component({
  selector: 'kr-dashboard',
  imports: [NgTemplateOutlet, RouterLink, BytesPipe, SpeedPipe, DurationPipe, AgoPipe, Poster, Progress, StateBadge, Upcoming],
  templateUrl: './dashboard.html',
  styleUrl: './dashboard.scss',
})
export class DashboardPage implements OnInit {
  private readonly api = inject(Api);
  private readonly events = inject(Events);
  private readonly destroyRef = inject(DestroyRef);

  protected readonly stats = signal<DashboardStats | null>(null);
  protected readonly rootFolders = signal<RootFolder[]>([]);
  protected readonly error = signal<string | null>(null);

  // Library breakdowns as segmented bars.
  protected readonly movieSegments = computed<Segment[]>(() => {
    const m = this.stats()?.library.movies;
    if (!m) return [];
    return [
      { label: 'Downloaded', value: m.downloaded, color: 'bg-emerald-500' },
      { label: 'Missing', value: m.missing, color: 'bg-rose-500' },
      { label: 'Unmonitored', value: Math.max(0, m.total - m.downloaded - m.missing), color: 'bg-slate-600' },
    ];
  });
  protected readonly episodeSegments = computed<Segment[]>(() => {
    const e = this.stats()?.library.episodes;
    if (!e) return [];
    return [
      { label: 'Downloaded', value: e.downloaded, color: 'bg-emerald-500' },
      { label: 'Missing', value: e.missing, color: 'bg-rose-500' },
      { label: 'Not aired or unmonitored', value: Math.max(0, e.total - e.downloaded - e.missing), color: 'bg-slate-600' },
    ];
  });
  protected readonly requestSegments = computed<Segment[]>(() => {
    const r = this.stats()?.requests;
    if (!r) return [];
    return [
      { label: 'Pending', value: r.pending, color: 'bg-amber-500' },
      { label: 'Approved', value: r.approved, color: 'bg-emerald-500' },
      { label: 'Declined', value: r.declined, color: 'bg-rose-500' },
    ];
  });
  protected readonly roleSegments = computed<Segment[]>(() => {
    const u = this.stats()?.users;
    if (!u) return [];
    return [
      { label: 'Admins', value: u.admins, color: 'bg-accent-500' },
      { label: 'Managers', value: u.managers, color: 'bg-violet-500' },
      { label: 'Users', value: u.users, color: 'bg-slate-500' },
    ];
  });
  protected readonly maxResolutionSize = computed(() => Math.max(1, ...(this.stats()?.library.resolutions.map(r => r.size) ?? [])));
  protected readonly maxRequester = computed(() => Math.max(1, ...(this.stats()?.users.topRequesters.map(r => r.requests) ?? [])));

  // Downloads, live from SignalR.
  protected readonly downloads = this.events.downloads;
  protected readonly downloadRate = computed(() => this.downloads().reduce((s, d) => s + d.downloadRate, 0));
  protected readonly uploadRate = computed(() => this.downloads().reduce((s, d) => s + d.uploadRate, 0));
  protected readonly active = computed(() => this.downloads().filter(d => ACTIVE.includes(d.state)));
  /** Bytes still to fetch and the overall progress of the active downloads. */
  protected readonly activeTotals = computed(() => {
    const size = this.active().reduce((s, d) => s + d.size, 0);
    const done = this.active().reduce((s, d) => s + Math.min(d.downloaded, d.size), 0);
    return { size, remaining: size - done, progress: size > 0 ? (done / size) * 100 : 0 };
  });
  /** Time until everything active is done at the current rate. */
  protected readonly eta = computed(() => {
    const rate = this.downloadRate();
    return rate > 0 ? this.activeTotals().remaining / rate : null;
  });
  protected readonly stateSegments = computed<Segment[]>(() => {
    const counts = new Map<DownloadState, number>();
    for (const d of this.downloads()) counts.set(d.state, (counts.get(d.state) ?? 0) + 1);
    return STATES.map(s => ({ label: s.label, value: counts.get(s.state) ?? 0, color: s.color })).filter(s => s.value > 0);
  });
  /** The fastest active downloads first, then by progress. */
  protected readonly topDownloads = computed(() =>
    [...this.active()].sort((a, b) => b.downloadRate - a.downloadRate || b.progress - a.progress).slice(0, TOP_DOWNLOADS));
  protected readonly errors = computed(() => this.downloads().filter(d => d.state === 'Error').length);

  ngOnInit() {
    this.load();
    // Imports, grabs and request changes alter the figures; they come in bursts, so reload at most every few seconds.
    merge(this.events.historyAdded, this.events.requestsUpdated)
      .pipe(debounceTime(3000), takeUntilDestroyed(this.destroyRef))
      .subscribe(() => this.load());
  }

  private async load() {
    try {
      const [stats, folders] = await Promise.all([this.api.dashboard(), this.api.rootFolders().catch(() => [])]);
      this.stats.set(stats);
      this.rootFolders.set(folders);
      this.error.set(null);
    } catch (err) {
      this.error.set(errorMessage(err));
    }
  }

  protected total(segments: Segment[]): number {
    return segments.reduce((s, x) => s + x.value, 0);
  }

  protected segmentsLabel(segments: Segment[]): string {
    return segments.map(s => `${s.label}: ${s.value}`).join(', ');
  }

  protected percent(value: number, total: number): number {
    return total > 0 ? (value / total) * 100 : 0;
  }

  protected resolutionLabel(resolution: number): string {
    return resolution > 0 ? `${resolution}p` : 'Unknown';
  }

  protected link(mediaType: MediaType, id: number): unknown[] {
    return [mediaType === 'Movie' ? '/movies' : '/series', id];
  }
}
