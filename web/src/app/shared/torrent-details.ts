import { Component, OnDestroy, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { DatePipe, DecimalPipe } from '@angular/common';
import { Api, errorMessage } from '../core/api';
import { BytesPipe, DurationPipe, SpeedPipe } from '../core/format';
import { TorrentDetails } from '../core/models';
import { Modal } from './modal';
import { Progress } from './progress';
import { StateBadge } from './state-badge';

type Tab = 'overview' | 'files' | 'peers' | 'trackers';

/** Live details of one torrent (works for the built-in engine and qBittorrent). */
@Component({
  selector: 'kr-torrent-details',
  imports: [Modal, Progress, StateBadge, BytesPipe, SpeedPipe, DurationPipe, DatePipe, DecimalPipe],
  template: `
    <kr-modal [title]="details()?.status?.name ?? name()" width="max-w-6xl" (closed)="closed.emit()">
      @if (error()) {
        <p class="rounded bg-rose-500/10 px-3 py-2 text-sm text-rose-300">{{ error() }}</p>
      }
      @if (details(); as d) {
        <!-- Header -->
        <div class="mb-4 flex flex-wrap items-center gap-x-4 gap-y-2 text-sm">
          <kr-state-badge [state]="d.status.state" />
          @if (d.clientState) { <span class="badge bg-surface-700 font-mono text-slate-300" title="Client state">{{ d.clientState }}</span> }
          <span class="text-slate-400">{{ d.status.progress | number: '1.0-2' }}% of {{ d.status.size | bytes }}</span>
          <span class="text-slate-400">↓ {{ d.status.downloadRate | speed }}</span>
          <span class="text-slate-400">↑ {{ d.status.uploadRate | speed }}</span>
          @if (d.status.state === 'Downloading') { <span class="text-slate-400">ETA {{ d.status.etaSeconds | duration }}</span> }
          <span class="ml-auto text-xs text-slate-500">{{ d.client }}</span>
        </div>

        <!-- Piece map -->
        @if (d.pieces) {
          <div class="mb-1 flex h-3 overflow-hidden rounded bg-surface-800" [title]="(d.piecesHave ?? '?') + ' / ' + (d.pieceCount ?? '?') + ' pieces'">
            @for (c of pieceChars(); track $index) {
              <div class="h-full flex-1" [class]="c === '2' ? 'bg-accent-500' : c === '1' ? 'bg-amber-400' : 'bg-transparent'"></div>
            }
          </div>
          <p class="mb-4 text-xs text-slate-500">Pieces: {{ d.piecesHave ?? '?' }} / {{ d.pieceCount ?? '?' }}{{ d.pieceSize ? ' × ' + (d.pieceSize | bytes) : '' }}
            · <span class="text-accent-300">■</span> have <span class="text-amber-300">■</span> partial</p>
        } @else {
          <kr-progress class="mb-4" [value]="d.status.progress" />
        }

        <!-- Tabs -->
        <div class="mb-4 flex gap-1 border-b border-surface-700">
          @for (t of tabs; track t.id) {
            <button class="-mb-px border-b-2 px-3 py-1.5 text-sm font-medium"
                    [class]="tab() === t.id ? 'border-accent-400 text-white' : 'border-transparent text-slate-400 hover:text-slate-200'"
                    (click)="tab.set(t.id)">
              {{ t.label }} @if (count(t.id) != null) { <span class="text-xs text-slate-500">{{ count(t.id) }}</span> }
            </button>
          }
        </div>

        <div class="max-h-[55vh] overflow-auto">
          @switch (tab()) {
            @case ('overview') {
              <div class="grid gap-x-8 gap-y-6 md:grid-cols-2">
                <dl class="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1.5 text-sm">
                  <dt class="col-span-2 mb-1 text-xs font-semibold uppercase tracking-wide text-slate-400">Transfer</dt>
                  <dt class="text-slate-500">Downloaded</dt><dd>{{ d.status.downloaded | bytes }} @if (d.sessionDownloaded != null) { <span class="text-slate-500">({{ d.sessionDownloaded | bytes }} this session)</span> }</dd>
                  <dt class="text-slate-500">Uploaded</dt><dd>{{ d.status.uploaded | bytes }} @if (d.sessionUploaded != null) { <span class="text-slate-500">({{ d.sessionUploaded | bytes }} this session)</span> }</dd>
                  <dt class="text-slate-500">Ratio</dt><dd>{{ d.status.ratio | number: '1.2-3' }}</dd>
                  @if (d.wasted != null) { <dt class="text-slate-500">Wasted</dt><dd>{{ d.wasted | bytes }}</dd> }
                  @if (d.averageDownloadRate != null) { <dt class="text-slate-500">Avg. speed</dt><dd>↓ {{ d.averageDownloadRate | speed }} · ↑ {{ d.averageUploadRate | speed }}</dd> }
                  <dt class="text-slate-500">Limits</dt><dd>↓ {{ limit(d.downloadLimit) }} · ↑ {{ limit(d.uploadLimit) }}</dd>
                  @if (d.timeActiveSeconds != null) { <dt class="text-slate-500">Time active</dt><dd>{{ d.timeActiveSeconds | duration }}</dd> }
                  @if (d.seedingTimeSeconds != null) { <dt class="text-slate-500">Seeding time</dt><dd>{{ d.seedingTimeSeconds | duration }}</dd> }
                  @if (d.lastActivity) { <dt class="text-slate-500">Last activity</dt><dd>{{ d.lastActivity | date: 'medium' }}</dd> }
                  <dt class="col-span-2 mt-3 mb-1 text-xs font-semibold uppercase tracking-wide text-slate-400">Swarm</dt>
                  <dt class="text-slate-500">Seeds</dt><dd>{{ d.seedsConnected ?? d.status.seeds }}{{ d.seedsTotal != null ? ' (' + d.seedsTotal + ' in swarm)' : '' }}</dd>
                  <dt class="text-slate-500">Peers</dt><dd>{{ d.peersConnected ?? d.status.leechers }}{{ d.peersTotal != null ? ' (' + d.peersTotal + ' ' + (d.seedsTotal != null ? 'in swarm' : 'known') + ')' : '' }}</dd>
                  @if (d.connections != null) { <dt class="text-slate-500">Connections</dt><dd>{{ d.connections }}{{ d.connectionsLimit ? ' / ' + d.connectionsLimit : '' }}</dd> }
                  @if (d.availability != null) { <dt class="text-slate-500">Availability</dt><dd>{{ d.availability | number: '1.0-3' }}</dd> }
                  @if (d.lastSeenComplete) { <dt class="text-slate-500">Last seen complete</dt><dd>{{ d.lastSeenComplete | date: 'medium' }}</dd> }
                  @if (d.nextAnnounceSeconds != null) { <dt class="text-slate-500">Next announce</dt><dd>{{ d.nextAnnounceSeconds | duration }}</dd> }
                </dl>
                <dl class="grid grid-cols-[auto_1fr] gap-x-4 gap-y-1.5 text-sm">
                  <dt class="col-span-2 mb-1 text-xs font-semibold uppercase tracking-wide text-slate-400">Torrent</dt>
                  @if (d.totalSize != null) { <dt class="text-slate-500">Total size</dt><dd>{{ d.totalSize | bytes }}{{ d.selectedSize != null && d.selectedSize !== d.totalSize ? ' (' + (d.selectedSize | bytes) + ' selected)' : '' }}</dd> }
                  @if (d.infoHashV1) { <dt class="text-slate-500">Info hash v1</dt><dd class="break-all font-mono text-xs">{{ d.infoHashV1 }}</dd> }
                  @if (d.infoHashV2) { <dt class="text-slate-500">Info hash v2</dt><dd class="break-all font-mono text-xs">{{ d.infoHashV2 }}</dd> }
                  @if (d.isPrivate != null) { <dt class="text-slate-500">Private</dt><dd>{{ d.isPrivate ? 'Yes' : 'No' }}</dd> }
                  @if (d.createdBy) { <dt class="text-slate-500">Created by</dt><dd>{{ d.createdBy }}</dd> }
                  @if (d.creationDate) { <dt class="text-slate-500">Created on</dt><dd>{{ d.creationDate | date: 'medium' }}</dd> }
                  @if (d.comment) { <dt class="text-slate-500">Comment</dt><dd class="break-words">{{ d.comment }}</dd> }
                  <dt class="text-slate-500">Added</dt><dd>{{ d.status.added | date: 'medium' }}</dd>
                  @if (d.status.completedAt) { <dt class="text-slate-500">Completed</dt><dd>{{ d.status.completedAt | date: 'medium' }}</dd> }
                  <dt class="text-slate-500">Save path</dt><dd class="break-all font-mono text-xs">{{ d.status.savePath }}</dd>
                  @if (d.contentPath) { <dt class="text-slate-500">Content path</dt><dd class="break-all font-mono text-xs">{{ d.contentPath }}</dd> }
                  @if (d.tags.length) {
                    <dt class="text-slate-500">Category / tags</dt>
                    <dd class="flex flex-wrap gap-1">@for (t of d.tags; track t) { <span class="badge bg-surface-700 text-slate-300">{{ t }}</span> }</dd>
                  }
                  @if (d.webSeeds.length) { <dt class="text-slate-500">Web seeds</dt><dd class="break-all text-xs">{{ d.webSeeds.join(', ') }}</dd> }
                  @if (d.status.errorMessage || d.status.importMessage) {
                    <dt class="text-slate-500">Problem</dt><dd class="text-rose-300">{{ d.status.errorMessage || d.status.importMessage }}</dd>
                  }
                  @if (d.magnetUri) {
                    <dt class="text-slate-500">Magnet</dt>
                    <dd><button class="btn-ghost btn-sm -ml-2" (click)="copy(d.magnetUri)">Copy magnet link</button></dd>
                  }
                </dl>
              </div>
            }
            @case ('files') {
              @if (d.files.length === 0) { <p class="py-6 text-center text-sm text-slate-500">File list not available yet (waiting for metadata).</p> }
              @else {
                <table class="table">
                  <thead><tr><th>Name</th><th>Size</th><th class="w-48">Progress</th><th>Priority</th><th>Availability</th></tr></thead>
                  <tbody>
                    @for (f of d.files; track f.index) {
                      <tr [class.opacity-50]="f.priority === 'Skip'">
                        <td class="w-full max-w-0"><div class="truncate font-mono text-xs" [title]="f.path">{{ f.path }}</div></td>
                        <td class="whitespace-nowrap">{{ f.size | bytes }}</td>
                        <td><div class="flex items-center gap-2"><kr-progress class="flex-1" [value]="f.progress" [color]="f.progress >= 100 ? 'bg-emerald-500' : 'bg-accent-500'" /><span class="w-12 text-right text-xs text-slate-400">{{ f.progress | number: '1.0-1' }}%</span></div></td>
                        <td>{{ f.priority }}</td>
                        <td class="text-slate-400">{{ f.availability != null ? (f.availability | number: '1.0-2') : '—' }}</td>
                      </tr>
                    }
                  </tbody>
                </table>
              }
            }
            @case ('peers') {
              @if (d.peers.length === 0) { <p class="py-6 text-center text-sm text-slate-500">Not connected to any peers.</p> }
              @else {
                <table class="table">
                  <thead><tr><th>Address</th><th>Client</th><th>Flags</th><th>Connection</th><th class="w-36">Progress</th><th>↓ Speed</th><th>↑ Speed</th><th>Downloaded</th><th>Uploaded</th></tr></thead>
                  <tbody>
                    @for (p of d.peers; track p.address) {
                      <tr>
                        <td class="whitespace-nowrap font-mono text-xs">{{ p.address }}@if (p.country) { <span class="ml-1 font-sans text-slate-500">{{ p.country }}</span> }</td>
                        <td class="max-w-40 truncate" [title]="p.client ?? ''">{{ p.client ?? '—' }}</td>
                        <td class="whitespace-nowrap font-mono text-xs" [title]="p.flagsDescription ?? ''">{{ p.flags || '—' }}</td>
                        <td class="whitespace-nowrap text-xs text-slate-400">{{ p.connection ?? '—' }}</td>
                        <td><div class="flex items-center gap-2"><kr-progress class="flex-1" [value]="p.progress" [color]="p.isSeeder ? 'bg-emerald-500' : 'bg-accent-500'" /><span class="w-10 text-right text-xs text-slate-400">{{ p.progress | number: '1.0-0' }}%</span></div></td>
                        <td class="whitespace-nowrap">{{ p.downloadRate | speed }}</td>
                        <td class="whitespace-nowrap">{{ p.uploadRate | speed }}</td>
                        <td class="whitespace-nowrap text-slate-400">{{ p.downloaded | bytes }}</td>
                        <td class="whitespace-nowrap text-slate-400">{{ p.uploaded | bytes }}</td>
                      </tr>
                    }
                  </tbody>
                </table>
              }
            }
            @case ('trackers') {
              <table class="table">
                <thead><tr><th>Tracker</th><th>Tier</th><th>Status</th><th>Seeds</th><th>Leechers</th><th>Peers</th><th>Completed</th><th>Next announce</th></tr></thead>
                <tbody>
                  @for (t of d.trackers; track t.url) {
                    <tr>
                      <td class="w-full max-w-0">
                        <div class="truncate font-mono text-xs" [title]="t.url">{{ t.url }}</div>
                        @if (t.message) { <div class="truncate text-xs text-amber-300" [title]="t.message">{{ t.message }}</div> }
                      </td>
                      <td class="text-slate-400">{{ t.tier >= 0 ? t.tier : '' }}</td>
                      <td class="whitespace-nowrap"><span class="badge" [class]="trackerStyle(t.status)">{{ t.status }}</span></td>
                      <td>{{ t.seeds ?? '—' }}</td>
                      <td>{{ t.leechers ?? '—' }}</td>
                      <td>{{ t.peers ?? '—' }}</td>
                      <td>{{ t.downloaded ?? '—' }}</td>
                      <td class="whitespace-nowrap text-slate-400">{{ t.nextAnnounceSeconds != null ? (t.nextAnnounceSeconds | duration) : '—' }}</td>
                    </tr>
                  }
                </tbody>
              </table>
            }
          }
        </div>
      } @else if (!error()) {
        <p class="py-10 text-center text-slate-400">Loading…</p>
      }
    </kr-modal>
  `,
})
export class TorrentDetailsDialog implements OnInit, OnDestroy {
  readonly hash = input.required<string>();
  readonly name = input('');
  readonly closed = output<void>();

  private readonly api = inject(Api);
  protected readonly tabs: { id: Tab; label: string }[] = [
    { id: 'overview', label: 'Overview' }, { id: 'files', label: 'Files' }, { id: 'peers', label: 'Peers' }, { id: 'trackers', label: 'Trackers' },
  ];
  protected readonly tab = signal<Tab>('overview');
  protected readonly details = signal<TorrentDetails | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly pieceChars = computed(() => (this.details()?.pieces ?? '').split(''));
  private timer?: ReturnType<typeof setInterval>;

  ngOnInit() {
    this.refresh();
    this.timer = setInterval(() => this.refresh(), 2000);
  }

  ngOnDestroy() { clearInterval(this.timer); }

  private async refresh() {
    try {
      this.details.set(await this.api.downloadDetails(this.hash()));
      this.error.set(null);
    } catch (e) {
      this.error.set(errorMessage(e));
    }
  }

  protected count(t: Tab) {
    const d = this.details();
    if (!d) return null;
    return t === 'files' ? d.files.length : t === 'peers' ? d.peers.length : t === 'trackers' ? d.trackers.length : null;
  }

  protected limit(bytesPerSecond?: number) {
    return !bytesPerSecond ? '∞' : new SpeedPipe().transform(bytesPerSecond);
  }

  protected trackerStyle(status: string) {
    return status === 'Working' ? 'bg-emerald-500/15 text-emerald-300'
      : status === 'Not working' ? 'bg-rose-500/15 text-rose-300'
      : status === 'Updating' ? 'bg-amber-500/15 text-amber-300'
      : 'bg-slate-500/15 text-slate-400';
  }

  protected copy(text: string) { navigator.clipboard?.writeText(text); }
}
