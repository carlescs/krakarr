import { Component, DestroyRef, OnInit, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { DatePipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { Api } from '../../core/api';
import { Events } from '../../core/events';
import { CommandInfo, HistoryItem } from '../../core/models';
import { TableSort } from '../../core/table-sort';
import { SortHeader } from '../../shared/sort-header/sort-header';

@Component({
  selector: 'kr-activity',
  imports: [DatePipe, RouterLink, SortHeader],
  templateUrl: './activity.html',
  styleUrl: './activity.scss',
})
export class ActivityPage implements OnInit {
  private readonly api = inject(Api);
  private readonly events = inject(Events);
  private readonly destroyRef = inject(DestroyRef);
  protected readonly history = signal<HistoryItem[]>([]);
  protected readonly commands = signal<CommandInfo[]>([]);
  /** The API returns history newest first. */
  protected readonly historySort = new TableSort(this.history, {
    event: h => h.eventType,
    release: h => h.sourceTitle,
    quality: h => h.quality,
    indexer: h => h.indexer,
    date: { value: h => Date.parse(h.date), desc: true },
  });

  ngOnInit() {
    this.load();
    this.events.historyAdded.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(() => this.load());
    this.events.commandUpdated.pipe(takeUntilDestroyed(this.destroyRef)).subscribe(c =>
      this.commands.update(list => {
        const idx = list.findIndex(x => x.id === c.id);
        return idx >= 0 ? list.map(x => (x.id === c.id ? c : x)) : [c, ...list];
      }));
  }

  private async load() {
    const [history, commands] = await Promise.all([this.api.history(undefined, 1, 100), this.api.commands()]);
    this.history.set(history.items);
    this.commands.set(commands);
  }
}
