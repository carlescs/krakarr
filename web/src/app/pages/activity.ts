import { Component, DestroyRef, OnInit, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { DatePipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { Api } from '../core/api';
import { Events } from '../core/events';
import { CommandInfo, HistoryItem } from '../core/models';

@Component({
  selector: 'kr-activity',
  imports: [DatePipe, RouterLink],
  template: `
    <h1 class="page-title mb-4 sm:mb-6">Activity</h1>
    <div class="grid gap-6 xl:grid-cols-[1fr_380px]">
      <section class="card overflow-hidden">
        <h2 class="border-b border-surface-700 px-4 py-3 text-sm font-semibold uppercase tracking-wide text-slate-400 sm:px-5">History</h2>
        @if (history().length === 0) {
          <p class="px-5 py-8 text-sm text-slate-500">No history yet.</p>
        } @else {
          <div class="overflow-x-auto">
            <table class="table">
              <thead><tr><th>Event</th><th>Release</th><th class="max-sm:hidden">Quality</th><th class="max-md:hidden">Indexer</th><th class="max-sm:hidden">Date</th></tr></thead>
              <tbody>
                @for (h of history(); track h.id) {
                  <tr>
                    <td>
                      <span class="badge" [class]="h.eventType === 'Imported' ? 'bg-emerald-500/15 text-emerald-300' : h.eventType === 'Grabbed' ? 'bg-accent-500/15 text-accent-300' : h.eventType === 'SubtitleDownloaded' ? 'bg-violet-500/15 text-violet-300' : 'bg-rose-500/15 text-rose-300'">
                        {{ h.eventType === 'DownloadFailed' ? 'Failed' : h.eventType === 'SubtitleDownloaded' ? 'Subtitle' : h.eventType }}
                      </span>
                    </td>
                    <td class="w-full max-w-0">
                      @if (h.movieId) {
                        <a [routerLink]="['/movies', h.movieId]" class="block truncate hover:text-accent-300" [title]="h.sourceTitle">{{ h.sourceTitle }}</a>
                      } @else if (h.seriesId) {
                        <a [routerLink]="['/series', h.seriesId]" class="block truncate hover:text-accent-300" [title]="h.sourceTitle">{{ h.sourceTitle }}</a>
                      } @else { <span class="block truncate">{{ h.sourceTitle }}</span> }
                      @if (h.message) { <div class="truncate text-xs text-slate-500" [title]="h.message">{{ h.message }}</div> }
                      <div class="truncate text-xs text-slate-500 sm:hidden">{{ h.date | date: 'short' }}{{ h.quality ? ' · ' + h.quality : '' }}{{ h.indexer ? ' · ' + h.indexer : '' }}</div>
                    </td>
                    <td class="whitespace-nowrap max-sm:hidden">{{ h.quality }}</td>
                    <td class="text-slate-400 max-md:hidden">{{ h.indexer }}</td>
                    <td class="whitespace-nowrap text-slate-400 max-sm:hidden">{{ h.date | date: 'short' }}</td>
                  </tr>
                }
              </tbody>
            </table>
          </div>
        }
      </section>
      <section class="card h-fit overflow-hidden">
        <h2 class="border-b border-surface-700 px-5 py-3 text-sm font-semibold uppercase tracking-wide text-slate-400">Tasks</h2>
        @if (commands().length === 0) {
          <p class="px-5 py-8 text-sm text-slate-500">No recent tasks.</p>
        }
        <ul class="divide-y divide-surface-800">
          @for (c of commands(); track c.id) {
            <li class="px-5 py-3 text-sm">
              <div class="flex items-center gap-2">
                <span class="size-2 rounded-full" [class]="c.status === 'Completed' ? 'bg-emerald-400' : c.status === 'Failed' ? 'bg-rose-400' : c.status === 'Running' ? 'animate-pulse bg-accent-400' : 'bg-slate-500'"></span>
                <span class="flex-1 truncate font-medium">{{ c.name }}</span>
                <span class="text-xs text-slate-500">{{ c.queued | date: 'shortTime' }}</span>
              </div>
              @if (c.message) { <p class="mt-1 pl-4 text-xs text-slate-400">{{ c.message }}</p> }
            </li>
          }
        </ul>
      </section>
    </div>
  `,
})
export class ActivityPage implements OnInit {
  private readonly api = inject(Api);
  private readonly events = inject(Events);
  private readonly destroyRef = inject(DestroyRef);
  protected readonly history = signal<HistoryItem[]>([]);
  protected readonly commands = signal<CommandInfo[]>([]);

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
