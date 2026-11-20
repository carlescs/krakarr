import { Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Api, errorMessage } from '../core/api';
import { Events } from '../core/events';
import { BytesPipe, DurationPipe, SpeedPipe } from '../core/format';
import { DownloadClientStatus, TorrentStatus } from '../core/models';
import { Toasts } from '../core/toast';
import { Modal } from '../shared/modal';
import { Progress } from '../shared/progress';
import { StateBadge } from '../shared/state-badge';
import { TorrentDetailsDialog } from '../shared/torrent-details';

@Component({
  selector: 'kr-downloads',
  imports: [FormsModule, RouterLink, Modal, Progress, StateBadge, TorrentDetailsDialog, BytesPipe, SpeedPipe, DurationPipe],
  template: `
    <div class="mb-6 flex flex-wrap items-center gap-3">
      <h1 class="page-title">Downloads</h1>
      @if (client(); as c) {
        <span class="badge bg-surface-700 text-slate-300">{{ c.kind === 'QBittorrent' ? 'qBittorrent · tag ' + c.tag : 'Built-in client' }}</span>
      }
      <span class="mr-auto"></span>
      <span class="text-sm text-slate-400 max-sm:hidden">↓ {{ totalDown() | speed }} · ↑ {{ totalUp() | speed }}</span>
      <button class="btn-primary" (click)="addOpen.set(true)">+ Add torrent</button>
    </div>

    @if (client()?.error; as err) {
      <div class="card mb-4 border-rose-500/30 px-5 py-3 text-sm text-rose-300">
        Cannot reach the download client: {{ err }} — check <a routerLink="/settings" class="underline">Settings → Download client</a>.
      </div>
    }

    @if (list().length === 0) {
      <div class="card px-6 py-12 text-center text-sm text-slate-400">No torrents. Grab a release or add a magnet link.</div>
    } @else {
      <div class="space-y-2">
        @for (d of list(); track d.infoHash) {
          <div class="card p-3 sm:p-4">
            <div class="flex flex-wrap items-center gap-x-2 gap-y-1.5 sm:gap-x-3">
              <kr-state-badge [state]="d.state" />
              <button class="min-w-0 text-left font-medium text-slate-100 break-all hover:text-accent-300 max-sm:order-first max-sm:line-clamp-2 max-sm:basis-full sm:flex-1 sm:truncate" [title]="d.name + ' — show details'" (click)="details.set(d)">{{ d.name }}</button>
              @if (d.movieId) {
                <a [routerLink]="['/movies', d.movieId]" class="badge bg-accent-500/15 text-accent-300 hover:bg-accent-500/25">Movie</a>
              }
              @if (d.seriesId) {
                <a [routerLink]="['/series', d.seriesId]" class="badge bg-violet-500/15 text-violet-300 hover:bg-violet-500/25">Series</a>
              }
              @if (d.imported) { <span class="badge bg-emerald-500/15 text-emerald-300">Imported</span> }
              <span class="badge bg-surface-700 text-slate-300 max-sm:hidden">{{ d.category }}</span>
              <div class="flex gap-1 max-sm:ml-auto">
                @if (d.state === 'Paused' || d.state === 'Completed') {
                  <button class="btn-ghost btn-sm" (click)="resume(d)" title="Resume">▶</button>
                } @else {
                  <button class="btn-ghost btn-sm" (click)="pause(d)" title="Pause">❚❚</button>
                }
                <button class="btn-ghost btn-sm" (click)="details.set(d)" title="Details">ⓘ</button>
                <button class="btn-ghost btn-sm text-rose-300" (click)="removing.set(d)" title="Remove">✕</button>
              </div>
            </div>
            <kr-progress class="mt-3" [value]="d.progress" [color]="d.state === 'Seeding' || d.state === 'Completed' ? 'bg-emerald-500' : d.state === 'Paused' ? 'bg-amber-500' : 'bg-accent-500'" />
            <div class="mt-2 flex flex-wrap gap-x-4 gap-y-1 text-xs text-slate-400 sm:gap-x-5">
              <span>{{ d.progress.toFixed(1) }}% of {{ d.size | bytes }}</span>
              <span>↓ {{ d.downloadRate | speed }}</span>
              <span>↑ {{ d.uploadRate | speed }}</span>
              <span>Seeds {{ d.seeds }} · Peers {{ d.leechers }}</span>
              <span>Ratio {{ d.ratio.toFixed(2) }}</span>
              @if (d.state === 'Downloading') { <span>ETA {{ d.etaSeconds | duration }}</span> }
              <span class="hidden font-mono md:inline">{{ d.savePath }}</span>
            </div>
            @if (d.errorMessage || d.importMessage) {
              <p class="mt-2 text-xs text-rose-300">{{ d.errorMessage || d.importMessage }}</p>
            }
          </div>
        }
      </div>
    }

    @if (addOpen()) {
      <kr-modal title="Add torrent" (closed)="addOpen.set(false)">
        <div class="space-y-4">
          <div>
            <label class="label">Magnet link</label>
            <textarea class="input font-mono" rows="3" [(ngModel)]="magnet" placeholder="magnet:?xt=urn:btih:…"></textarea>
          </div>
          <div class="text-center text-xs uppercase tracking-wide text-slate-500">or</div>
          <div>
            <label class="label">Torrent file</label>
            <input type="file" accept=".torrent,application/x-bittorrent" class="block w-full text-sm text-slate-400 file:mr-3 file:rounded-lg file:border-0 file:bg-surface-700 file:px-3 file:py-2 file:text-slate-100"
                   (change)="file = $any($event.target).files?.[0] ?? null" />
          </div>
          <div><label class="label">Category</label><input class="input" [(ngModel)]="category" /></div>
        </div>
        <ng-container footer>
          <button class="btn-ghost" (click)="addOpen.set(false)">Cancel</button>
          <button class="btn-primary" (click)="add()" [disabled]="busy()">Add</button>
        </ng-container>
      </kr-modal>
    }

    @if (details(); as d) {
      <kr-torrent-details [hash]="d.infoHash" [name]="d.name" (closed)="details.set(null)" />
    }

    @if (removing(); as d) {
      <kr-modal title="Remove torrent" (closed)="removing.set(null)">
        <p class="break-all text-sm text-slate-300">{{ d.name }}</p>
        <label class="mt-4 flex items-center gap-2 text-sm"><input type="checkbox" class="accent-rose-500" [(ngModel)]="deleteData" /> Also delete downloaded files</label>
        @if (d.imported) { <p class="hint">Imported library files are hardlinks/copies and are not affected.</p> }
        <ng-container footer>
          <button class="btn-ghost" (click)="removing.set(null)">Cancel</button>
          <button class="btn-danger" (click)="remove(d)">Remove</button>
        </ng-container>
      </kr-modal>
    }
  `,
})
export class DownloadsPage implements OnInit, OnDestroy {
  private readonly api = inject(Api);
  private readonly toasts = inject(Toasts);
  private readonly events = inject(Events);

  protected readonly list = computed(() => this.events.downloads());
  protected readonly totalDown = computed(() => this.list().reduce((s, d) => s + d.downloadRate, 0));
  protected readonly totalUp = computed(() => this.list().reduce((s, d) => s + d.uploadRate, 0));
  protected readonly addOpen = signal(false);
  protected readonly client = signal<DownloadClientStatus | null>(null);
  private clientTimer?: ReturnType<typeof setInterval>;
  protected readonly busy = signal(false);
  protected readonly removing = signal<TorrentStatus | null>(null);
  protected readonly details = signal<TorrentStatus | null>(null);
  protected magnet = '';
  protected category = 'manual';
  protected file: File | null = null;
  protected deleteData = false;

  async ngOnInit() {
    const refreshClient = () => this.api.downloadClientStatus().then(c => this.client.set(c)).catch(() => {});
    refreshClient();
    this.clientTimer = setInterval(refreshClient, 10000);
    // Prime the list before the first SignalR tick arrives.
    if (this.events.downloads().length === 0) this.events.downloads.set(await this.api.downloads());
  }

  ngOnDestroy() { clearInterval(this.clientTimer); }

  async add() {
    this.busy.set(true);
    try {
      if (this.file) await this.api.addTorrentFile(this.file, this.category);
      else if (this.magnet.trim()) await this.api.addMagnet(this.magnet.trim(), this.category);
      else return;
      this.toasts.success('Torrent added');
      this.addOpen.set(false);
      this.magnet = '';
      this.file = null;
    } catch (e) {
      this.toasts.error(errorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }

  async pause(d: TorrentStatus) { await this.api.pauseDownload(d.infoHash).catch(e => this.toasts.error(errorMessage(e))); }
  async resume(d: TorrentStatus) { await this.api.resumeDownload(d.infoHash).catch(e => this.toasts.error(errorMessage(e))); }

  async remove(d: TorrentStatus) {
    try {
      await this.api.removeDownload(d.infoHash, this.deleteData);
      this.events.downloads.update(list => list.filter(x => x.infoHash !== d.infoHash));
      this.removing.set(null);
      this.deleteData = false;
    } catch (e) {
      this.toasts.error(errorMessage(e));
    }
  }
}
