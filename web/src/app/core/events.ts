import { Injectable, signal } from '@angular/core';
import { HubConnection, HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr';
import { Subject } from 'rxjs';
import { CommandInfo, HistoryItem, TorrentStatus } from './models';

/** Real-time server events over SignalR. */
@Injectable({ providedIn: 'root' })
export class Events {
  readonly downloads = signal<TorrentStatus[]>([]);
  readonly connected = signal(false);
  readonly movieUpdated = new Subject<{ id: number }>();
  readonly seriesUpdated = new Subject<{ id: number }>();
  readonly subtitlesUpdated = new Subject<{ movieId?: number; seriesId?: number }>();
  readonly commandUpdated = new Subject<CommandInfo>();
  readonly historyAdded = new Subject<HistoryItem>();

  private connection?: HubConnection;

  async start() {
    if (this.connection && this.connection.state !== HubConnectionState.Disconnected) return;
    const baseHref = document.querySelector('base')?.getAttribute('href') ?? '/';
    this.connection = new HubConnectionBuilder()
      .withUrl(`${baseHref.replace(/\/$/, '')}/hubs/events`)
      .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
      .configureLogging(LogLevel.Warning)
      .build();

    this.connection.on('downloadsUpdated', (d: TorrentStatus[]) => this.downloads.set(d));
    this.connection.on('movieUpdated', (m: { id: number }) => this.movieUpdated.next(m));
    this.connection.on('seriesUpdated', (s: { id: number }) => this.seriesUpdated.next(s));
    this.connection.on('subtitlesUpdated', (s: { movieId?: number; seriesId?: number }) => this.subtitlesUpdated.next(s));
    this.connection.on('commandUpdated', (c: CommandInfo) => this.commandUpdated.next(c));
    this.connection.on('historyAdded', (h: HistoryItem) => this.historyAdded.next(h));
    this.connection.onreconnecting(() => this.connected.set(false));
    this.connection.onreconnected(() => this.connected.set(true));
    this.connection.onclose(() => this.connected.set(false));

    try {
      await this.connection.start();
      this.connected.set(true);
    } catch {
      this.connected.set(false);
      setTimeout(() => this.start(), 5000);
    }
  }

  async stop() {
    await this.connection?.stop();
    this.connection = undefined;
    this.connected.set(false);
  }
}

