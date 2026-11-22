import { Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Api, errorMessage } from '../../core/api';
import { Events } from '../../core/events';
import { BytesPipe, DurationPipe, SpeedPipe } from '../../core/format';
import { BulkDownloadAction, BulkResult, DownloadClientStatus, TorrentStatus } from '../../core/models';
import { Selection } from '../../core/selection';
import { Toasts } from '../../core/toast';
import { MediaLink, MediaLinkPicker, linkFields } from '../../shared/media-link-picker/media-link-picker';
import { Modal } from '../../shared/modal/modal';
import { Progress } from '../../shared/progress/progress';
import { StateBadge } from '../../shared/state-badge/state-badge';
import { TorrentDetailsDialog } from '../../shared/torrent-details/torrent-details';

const SHOW_SEEDING_KEY = 'krakarr.downloads.showSeeding';

/** The list shows either seeding torrents or everything else; the default is the non-seeding view. */
function readShowSeeding(): boolean {
  try { return localStorage.getItem(SHOW_SEEDING_KEY) === 'true'; } catch { return false; }
}

@Component({
  selector: 'kr-downloads',
  imports: [FormsModule, RouterLink, Modal, MediaLinkPicker, Progress, StateBadge, TorrentDetailsDialog, BytesPipe, SpeedPipe, DurationPipe],
  templateUrl: './downloads.html',
  styleUrl: './downloads.scss',
})
export class DownloadsPage implements OnInit, OnDestroy {
  private readonly api = inject(Api);
  private readonly toasts = inject(Toasts);
  private readonly events = inject(Events);

  protected readonly list = computed(() => this.events.downloads());
  protected readonly showSeeding = signal(readShowSeeding());
  protected readonly seedingCount = computed(() => this.list().filter(d => d.state === 'Seeding').length);
  protected readonly visible = computed(() => {
    const seeding = this.showSeeding();
    return this.list().filter(d => (d.state === 'Seeding') === seeding);
  });
  /** Multi-selection for bulk pause/resume/remove; only torrents in the current view (downloading or seeding) count. */
  protected readonly selection = new Selection(this.visible, d => d.infoHash);
  protected readonly bulkBusy = signal(false);
  protected readonly confirmBulkRemove = signal(false);
  protected bulkDeleteData = false;
  protected readonly totalDown = computed(() => this.list().reduce((s, d) => s + d.downloadRate, 0));
  protected readonly totalUp = computed(() => this.list().reduce((s, d) => s + d.uploadRate, 0));
  protected readonly addOpen = signal(false);
  protected readonly client = signal<DownloadClientStatus | null>(null);
  private clientTimer?: ReturnType<typeof setInterval>;
  protected readonly busy = signal(false);
  protected readonly removing = signal<TorrentStatus | null>(null);
  protected readonly details = signal<TorrentStatus | null>(null);
  protected readonly magnet = signal('');
  protected category = 'manual';
  protected readonly file = signal<File | null>(null);
  protected readonly link = signal<MediaLink | null>(null);
  /** Torrent name to suggest a library item from: the magnet's dn= or the file name. */
  protected readonly addHint = computed(() => {
    const file = this.file();
    if (file) return file.name.replace(/\.torrent$/i, '');
    try { return new URLSearchParams(this.magnet().trim().replace(/^magnet:\?/i, '')).get('dn') ?? ''; } catch { return ''; }
  });
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
      const file = this.file();
      const magnet = this.magnet().trim();
      const link = linkFields(this.link());
      if (file) await this.api.addTorrentFile(file, this.category, link);
      else if (magnet) await this.api.addMagnet(magnet, this.category, link);
      else return;
      this.toasts.success('Torrent added');
      this.addOpen.set(false);
      this.magnet.set('');
      this.file.set(null);
      this.setLink(null);
    } catch (e) {
      this.toasts.error(errorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }

  /** Linked downloads go to the movies/tv category unless the user typed their own. */
  protected setLink(link: MediaLink | null) {
    const defaults = ['manual', 'movies', 'tv', ''];
    if (defaults.includes(this.category.trim())) this.category = link ? (link.kind === 'movie' ? 'movies' : 'tv') : 'manual';
    this.link.set(link);
  }

  protected segment(active: boolean) {
    return 'rounded-md px-3 py-1 font-medium transition-colors pointer-coarse:min-h-9 '
      + (active ? 'bg-surface-600 text-white shadow-sm' : 'text-slate-400 hover:text-slate-200');
  }

  protected toggleSeeding(show: boolean) {
    this.showSeeding.set(show);
    this.selection.clear();
    try { localStorage.setItem(SHOW_SEEDING_KEY, String(show)); } catch { /* storage unavailable */ }
  }

  async pause(d: TorrentStatus) { await this.api.pauseDownload(d.infoHash).catch(e => this.toasts.error(errorMessage(e))); }
  async resume(d: TorrentStatus) { await this.api.resumeDownload(d.infoHash).catch(e => this.toasts.error(errorMessage(e))); }

  protected async bulk(action: BulkDownloadAction) {
    const hashes = this.selection.selected();
    if (hashes.length === 0 || this.bulkBusy()) return;
    this.bulkBusy.set(true);
    try {
      const result = await this.api.bulkDownloads(hashes, action, action === 'Remove' && this.bulkDeleteData);
      const verb = action === 'Pause' ? 'Paused' : action === 'Resume' ? 'Resumed' : 'Removed';
      this.report(`${verb} ${result.affected} torrent${result.affected === 1 ? '' : 's'}`, result);
      if (action === 'Remove') {
        this.confirmBulkRemove.set(false);
        this.bulkDeleteData = false;
        this.selection.clear();
      }
      // Reflect the new states now instead of waiting for the next live update.
      this.events.downloads.set(await this.api.downloads());
    } catch (e) {
      this.toasts.error(errorMessage(e));
    } finally {
      this.bulkBusy.set(false);
    }
  }

  private report(message: string, result: BulkResult) {
    if (result.errors.length === 0) this.toasts.success(message);
    else this.toasts.error(`${message}; ${result.errors.length} failed: ${result.errors.slice(0, 3).join(' · ')}`);
  }

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
