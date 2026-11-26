import { Component, ElementRef, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { BackgroundTasks, isActive } from '../../core/background-tasks';
import { BackgroundTask } from '../../core/models';

// Heroicons-style outline paths (24x24): the menu's icon and one per task kind; unknown kinds fall back to the cog.
const ICONS: Record<string, string> = {
  tasks: 'M16.023 9.348h4.992v-.001M2.985 19.644v-4.992m0 0h4.992m-4.993 0 3.181 3.183a8.25 8.25 0 0 0 13.803-3.7M4.031 9.865a8.25 8.25 0 0 1 13.803-3.7l3.181 3.182m0-4.991v4.99',
  task: 'M9.594 3.94c.09-.542.56-.94 1.11-.94h2.593c.55 0 1.02.398 1.11.94l.213 1.281c.063.374.313.686.645.87.074.04.147.083.22.127.325.196.72.257 1.075.124l1.217-.456a1.125 1.125 0 0 1 1.37.49l1.296 2.247a1.125 1.125 0 0 1-.26 1.431l-1.003.827c-.293.241-.438.613-.43.992a7.723 7.723 0 0 1 0 .255c-.008.378.137.75.43.991l1.004.827c.424.35.534.955.26 1.43l-1.298 2.247a1.125 1.125 0 0 1-1.369.491l-1.217-.456c-.355-.133-.75-.072-1.076.124a6.47 6.47 0 0 1-.22.128c-.331.183-.581.495-.644.869l-.213 1.281c-.09.543-.56.94-1.11.94h-2.594c-.55 0-1.019-.398-1.11-.94l-.213-1.281c-.062-.374-.312-.686-.644-.87a6.52 6.52 0 0 1-.22-.127c-.325-.196-.72-.257-1.076-.124l-1.217.456a1.125 1.125 0 0 1-1.369-.49l-1.297-2.247a1.125 1.125 0 0 1 .26-1.431l1.004-.827c.292-.24.437-.613.43-.991a6.932 6.932 0 0 1 0-.255c.007-.38-.138-.751-.43-.992l-1.004-.827a1.125 1.125 0 0 1-.26-1.43l1.297-2.247a1.125 1.125 0 0 1 1.37-.491l1.216.456c.356.133.751.072 1.076-.124.072-.044.146-.086.22-.128.332-.183.582-.495.644-.869l.214-1.28Z M15 12a3 3 0 1 1-6 0 3 3 0 0 1 6 0Z',
  subtitle: 'M7.5 8.25h9m-9 3H12m-9.75 1.51c0 1.6 1.123 2.994 2.707 3.227 1.129.166 2.27.293 3.423.379.35.026.67.21.865.501L12 21l2.755-4.133a1.14 1.14 0 0 1 .865-.501 48.172 48.172 0 0 0 3.423-.379c1.584-.233 2.707-1.626 2.707-3.228V6.741c0-1.602-1.123-2.995-2.707-3.228A48.394 48.394 0 0 0 12 3c-2.392 0-4.744.175-7.043.513C3.373 3.746 2.25 5.14 2.25 6.741v6.018Z',
};

const STATE_LABELS: Record<BackgroundTask['state'], string> = {
  Waiting: 'Waiting', Running: 'Running', Cancelling: 'Cancelling', Succeeded: 'Done', Failed: 'Failed', Cancelled: 'Cancelled',
};

/**
 * Tasks menu in the top bar: long-running work such as subtitle syncs that the signed-in user started (plus automatic
 * work, for staff), with their progress, a Cancel button while they run and their outcome once finished. Hidden while
 * there is nothing to show; the icon spins while something is running.
 */
@Component({
  selector: 'kr-background-tasks',
  imports: [RouterLink],
  host: {
    class: 'relative inline-block',
    '[class.hidden]': 'hidden()',
    '(document:click)': 'onDocumentClick($event)',
    '(document:keydown.escape)': 'open.set(false)',
  },
  templateUrl: './background-tasks.html',
  styleUrl: './background-tasks.scss',
})
export class BackgroundTasksMenu {
  protected readonly tasks = inject(BackgroundTasks);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  protected readonly icons = ICONS;
  protected readonly stateLabels = STATE_LABELS;
  protected readonly isActive = isActive;
  protected readonly open = signal(false);

  /** Stays while its panel is open, so clearing the last task doesn't yank it away. */
  protected readonly hidden = computed(() => this.tasks.items().length === 0 && !this.open());
  /** The badge counts what is still going, or else the outcomes waiting to be seen. */
  protected readonly badge = computed(() => this.tasks.active() || this.tasks.finished());

  protected label() {
    const active = this.tasks.active();
    return active === 0 ? 'Tasks' : `Tasks (${active} running)`;
  }

  protected badgeTone() {
    return this.tasks.hasFailures() ? 'bg-rose-400' : this.tasks.active() > 0 ? 'bg-accent-400' : 'bg-emerald-400';
  }

  protected tone(task: BackgroundTask) {
    return task.state === 'Failed' ? 'text-rose-400' : task.state === 'Succeeded' ? 'text-emerald-400'
      : task.state === 'Cancelled' ? 'text-slate-500' : 'text-accent-300';
  }

  /** Estimated time left as "45 s left", "3 min left" or "1 h 5 min left"; empty without an estimate. */
  protected remaining(seconds: number | null | undefined): string {
    if (seconds == null) return '';
    if (seconds < 60) return `${Math.max(1, Math.ceil(seconds))} s left`;
    const minutes = Math.ceil(seconds / 60);
    if (minutes < 60) return `${minutes} min left`;
    const rest = minutes % 60;
    return `${Math.floor(minutes / 60)} h${rest ? ` ${rest} min` : ''} left`;
  }

  protected onDocumentClick(event: MouseEvent) {
    if (this.open() && !event.composedPath().includes(this.host.nativeElement)) this.open.set(false);
  }
}
