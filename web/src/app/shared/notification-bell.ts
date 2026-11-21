import { Component, ElementRef, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { AppNotification } from '../core/models';
import { Notifications } from '../core/notifications';

// Heroicons-style outline paths (24x24) per notification kind; unknown kinds fall back to the bell.
const KIND_ICONS: Record<string, string> = {
  bell: 'M14.857 17.082a23.848 23.848 0 0 0 5.454-1.31A8.967 8.967 0 0 1 18 9.75V9A6 6 0 0 0 6 9v.75a8.967 8.967 0 0 1-2.312 6.022c1.733.64 3.56 1.085 5.455 1.31m5.714 0a24.255 24.255 0 0 1-5.714 0m5.714 0a3 3 0 1 1-5.714 0',
  movie: 'M3.375 19.5h17.25m-17.25 0a1.125 1.125 0 0 1-1.125-1.125M3.375 19.5h1.5C5.496 19.5 6 18.996 6 18.375m-3.75 0V5.625m0 12.75v-1.5c0-.621.504-1.125 1.125-1.125m18.375 2.625V5.625m0 12.75c0 .621-.504 1.125-1.125 1.125m1.125-1.125v-1.5c0-.621-.504-1.125-1.125-1.125m0 3.75h-1.5A1.125 1.125 0 0 1 18 18.375M20.625 4.5H3.375m17.25 0c.621 0 1.125.504 1.125 1.125M20.625 4.5h-1.5C18.504 4.5 18 5.004 18 5.625m3.75 0v1.5c0 .621-.504 1.125-1.125 1.125M3.375 4.5c-.621 0-1.125.504-1.125 1.125M3.375 4.5h1.5C5.496 4.5 6 5.004 6 5.625m-3.75 0v1.5c0 .621.504 1.125 1.125 1.125m0 0h1.5m-1.5 0c-.621 0-1.125.504-1.125 1.125v1.5c0 .621.504 1.125 1.125 1.125m1.5-3.75C5.496 8.25 6 7.746 6 7.125v-1.5M4.875 8.25C5.496 8.25 6 8.754 6 9.375v1.5m0-5.25v5.25m0-5.25C6 5.004 6.504 4.5 7.125 4.5h9.75c.621 0 1.125.504 1.125 1.125m1.125 2.625h1.5m-1.5 0A1.125 1.125 0 0 1 18 7.125v-1.5m1.125 2.625c-.621 0-1.125.504-1.125 1.125v1.5m2.625-2.625c.621 0 1.125.504 1.125 1.125v1.5c0 .621-.504 1.125-1.125 1.125M18 5.625v5.25M7.125 12h9.75m-9.75 0A1.125 1.125 0 0 1 6 10.875M7.125 12C6.504 12 6 12.504 6 13.125m0-2.25C6 11.496 5.496 12 4.875 12M18 10.875c0 .621-.504 1.125-1.125 1.125M18 10.875c0 .621.504 1.125 1.125 1.125m-2.25 0c.621 0 1.125.504 1.125 1.125m-12 5.25v-5.25m0 5.25c0 .621.504 1.125 1.125 1.125h9.75c.621 0 1.125-.504 1.125-1.125m-12 0v-1.5c0-.621-.504-1.125-1.125-1.125M18 18.375v-5.25m0 5.25v-1.5c0-.621.504-1.125 1.125-1.125M18 13.125v1.5c0 .621.504 1.125 1.125 1.125M18 13.125c0-.621.504-1.125 1.125-1.125M6 13.125v1.5c0 .621-.504 1.125-1.125 1.125M6 13.125C6 12.504 5.496 12 4.875 12m-1.5 0h1.5m-1.5 0c-.621 0-1.125.504-1.125 1.125v1.5c0 .621.504 1.125 1.125 1.125M19.125 12h1.5m0 0c.621 0 1.125.504 1.125 1.125v1.5c0 .621-.504 1.125-1.125 1.125m-17.25 0h1.5m14.25 0h1.5',
  episode: 'M6 20.25h12m-7.5-3v3m3-3v3m-10.125-3h17.25c.621 0 1.125-.504 1.125-1.125V4.875c0-.621-.504-1.125-1.125-1.125H3.375c-.621 0-1.125.504-1.125 1.125v11.25c0 .621.504 1.125 1.125 1.125Z',
  request: 'M2.25 13.5h3.86a2.25 2.25 0 0 1 2.012 1.244l.256.512a2.25 2.25 0 0 0 2.013 1.244h3.218a2.25 2.25 0 0 0 2.013-1.244l.256-.512a2.25 2.25 0 0 1 2.013-1.244h3.859m-19.5.338V18a2.25 2.25 0 0 0 2.25 2.25h15A2.25 2.25 0 0 0 21.75 18v-4.162c0-.224-.034-.447-.1-.661L19.24 5.338a2.25 2.25 0 0 0-2.15-1.588H6.911a2.25 2.25 0 0 0-2.15 1.588L2.35 13.177a2.25 2.25 0 0 0-.1.661Z',
};

/** Bell in the top bar: a badge with the total and a panel listing every notification, each linking to its page. */
@Component({
  selector: 'kr-notification-bell',
  imports: [RouterLink],
  host: {
    class: 'relative inline-block',
    '(document:click)': 'onDocumentClick($event)',
    '(document:keydown.escape)': 'open.set(false)',
  },
  template: `
    <button type="button" class="btn-ghost relative px-2" [attr.aria-label]="label()" [title]="label()" [attr.aria-expanded]="open()"
            aria-haspopup="true" (click)="open.set(!open())">
      <svg class="size-6" fill="none" viewBox="0 0 24 24" stroke-width="1.6" stroke="currentColor"><path stroke-linecap="round" stroke-linejoin="round" [attr.d]="icons['bell']" /></svg>
      @if (notifications.total() > 0) {
        <span class="absolute top-0.5 right-0.5 min-w-4 rounded-full px-1 text-center text-[10px] leading-4 font-semibold text-slate-950"
              [class]="notifications.hasProblems() ? 'bg-rose-400' : 'bg-amber-400'">{{ notifications.total() > 99 ? '99+' : notifications.total() }}</span>
      }
    </button>
    @if (open()) {
      <div class="card fixed inset-x-2 top-[calc(3.5rem+env(safe-area-inset-top))] z-40 overflow-hidden shadow-xl sm:absolute sm:inset-x-auto sm:top-full sm:right-0 sm:mt-1 sm:w-80">
        <div class="border-b border-surface-700 px-4 py-2.5 text-xs font-semibold uppercase tracking-wide text-slate-400">Notifications</div>
        @if (notifications.items().length === 0) {
          <p class="px-4 py-6 text-center text-sm text-slate-500">You're all caught up.</p>
        } @else {
          <ul class="max-h-[60vh] divide-y divide-surface-800 overflow-y-auto">
            @for (n of notifications.items(); track n.id) {
              <li>
                <a [routerLink]="n.link" (click)="open.set(false)" class="flex gap-3 px-4 py-3 transition hover:bg-surface-800">
                  <svg class="mt-0.5 size-5 shrink-0" [class]="tone(n)" fill="none" viewBox="0 0 24 24" stroke-width="1.6" stroke="currentColor">
                    <path stroke-linecap="round" stroke-linejoin="round" [attr.d]="icons[n.kind] ?? icons['bell']" />
                  </svg>
                  <span class="min-w-0 flex-1">
                    <span class="block text-sm font-medium text-slate-100">{{ n.title }}</span>
                    @if (n.message) { <span class="block text-xs text-slate-400">{{ n.message }}</span> }
                  </span>
                </a>
              </li>
            }
          </ul>
        }
      </div>
    }
  `,
})
export class NotificationBell {
  protected readonly notifications = inject(Notifications);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  protected readonly icons = KIND_ICONS;
  protected readonly open = signal(false);

  protected label() {
    const total = this.notifications.total();
    return total === 0 ? 'Notifications' : `Notifications (${total})`;
  }

  protected tone(n: AppNotification) {
    return n.severity === 'Error' ? 'text-rose-400' : n.severity === 'Warning' ? 'text-amber-400' : 'text-accent-300';
  }

  protected onDocumentClick(event: MouseEvent) {
    if (this.open() && !this.host.nativeElement.contains(event.target as Node)) this.open.set(false);
  }
}
