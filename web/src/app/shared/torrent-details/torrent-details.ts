import { Component, OnDestroy, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { DatePipe, DecimalPipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { Api, errorMessage } from '../../core/api';
import { BytesPipe, DurationPipe, SpeedPipe } from '../../core/format';
import { TorrentDetails } from '../../core/models';
import { Modal } from '../modal/modal';
import { Progress } from '../progress/progress';
import { StateBadge } from '../state-badge/state-badge';

type Tab = 'overview' | 'files' | 'peers' | 'trackers';

/** Live details of one torrent (works for the built-in engine and qBittorrent). */
@Component({
  selector: 'kr-torrent-details',
  imports: [RouterLink, Modal, Progress, StateBadge, BytesPipe, SpeedPipe, DurationPipe, DatePipe, DecimalPipe],
  templateUrl: './torrent-details.html',
  styleUrl: './torrent-details.scss',
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
