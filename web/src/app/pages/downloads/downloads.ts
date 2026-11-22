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
const SORT_KEY = 'krakarr.downloads.sort';

type SortKey = 'added' | 'name' | 'eta' | 'progress' | 'size' | 'downloadRate' | 'uploadRate' | 'ratio';
interface SortOrder { key: SortKey; desc: boolean; }

/** Sort options with the direction each starts in: soonest ETA and A-Z first, otherwise the biggest/newest. */
const SORTS: readonly { key: SortKey; label: string; desc: boolean }[] = [
  { key: 'added', label: 'Added', desc: true },
  { key: 'name', label: 'Name', desc: false },
  { key: 'eta', label: 'ETA', desc: false },
  { key: 'progress', label: 'Progress', desc: true },
  { key: 'size', label: 'Size', desc: true },
  { key: 'downloadRate', label: 'Download speed', desc: true },
  { key: 'uploadRate', label: 'Upload speed', desc: true },
  { key: 'ratio', label: 'Ratio', desc: true },
];
const DEFAULT_SORT: SortOrder = { key: 'added', desc: true };

function readSort(): SortOrder {
  try {
    const stored = JSON.parse(localStorage.getItem(SORT_KEY) ?? 'null') as Partial<SortOrder> | null;
    if (stored && SORTS.some(s => s.key === stored.key)) return { key: stored.key!, desc: !!stored.desc };
  } catch { /* storage unavailable or malformed */ }
  return DEFAULT_SORT;
}

/** The value a torrent sorts by; null (no ETA: not downloading, stalled) always sorts last. */
function sortValue(d: TorrentStatus, key: SortKey): number | string | null {
  switch (key) {
    case 'added': return Date.parse(d.added) || 0;
    case 'name': return d.name;
    case 'eta': return d.state === 'Downloading' && d.etaSeconds != null ? d.etaSeconds : null;
    default: return d[key];
  }
}

const collator = new Intl.Collator(undefined, { numeric: true, sensitivity: 'base' });

export function sortTorrents(list: readonly TorrentStatus[], { key, desc }: SortOrder): TorrentStatus[] {
  const sign = desc ? -1 : 1;
  // Array sort is stable: ties keep the client's order (newest first).
  return [...list].sort((a, b) => {
    const x = sortValue(a, key), y = sortValue(b, key);
    if (x === null || y === null) return x === y ? 0 : x === null ? 1 : -1;
    return sign * (typeof x === 'string' ? collator.compare(x, y as string) : x - (y as number));
  });
}

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
  protected readonly sorts = SORTS;
  protected readonly sort = signal(readSort());
  protected readonly visible = computed(() => {
    const seeding = this.showSeeding();
    return sortTorrents(this.list().filter(d => (d.state === 'Seeding') === seeding), this.sort());
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

  /** Picking another field starts in its natural direction; the arrow button flips it. */
  protected setSort(key: SortKey) {
    this.storeSort({ key, desc: SORTS.find(s => s.key === key)?.desc ?? false });
  }

  protected flipSort() {
    this.storeSort({ ...this.sort(), desc: !this.sort().desc });
  }

  private storeSort(order: SortOrder) {
    this.sort.set(order);
    try { localStorage.setItem(SORT_KEY, JSON.stringify(order)); } catch { /* storage unavailable */ }
  }

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
