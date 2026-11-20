import { Component, OnDestroy, OnInit, input, output } from '@angular/core';

let openModals = 0;

/**
 * Dialog that is centred on larger screens and becomes a bottom sheet on phones,
 * with a scrollable body and the footer actions always in reach.
 */
@Component({
  selector: 'kr-modal',
  host: { '(document:keydown.escape)': 'closed.emit()' },
  template: `
    <div class="fixed inset-0 z-50 flex items-end justify-center bg-black/60 backdrop-blur-sm sm:items-start sm:overflow-y-auto sm:p-4 sm:pt-[8vh]" (click)="closed.emit()">
      <div class="card flex max-h-[calc(100dvh-env(safe-area-inset-top)-0.75rem)] w-full flex-col rounded-b-none border-b-0 sm:max-h-none sm:rounded-b-xl sm:border-b"
           [class]="width()" (click)="$event.stopPropagation()" role="dialog" aria-modal="true" [attr.aria-label]="title()">
        <div class="flex shrink-0 items-center gap-3 border-b border-surface-700 py-3 pr-2 pl-4 sm:py-4 sm:pr-3 sm:pl-5">
          <h2 class="min-w-0 flex-1 truncate text-base font-semibold text-white" [title]="title()">{{ title() }}</h2>
          <button class="btn-ghost px-2.5" (click)="closed.emit()" aria-label="Close">✕</button>
        </div>
        <div class="min-h-0 flex-1 overflow-y-auto overscroll-contain px-4 pt-4 pb-[max(1rem,env(safe-area-inset-bottom))] sm:overflow-visible sm:p-5"><ng-content /></div>
        <div class="flex shrink-0 flex-wrap justify-end gap-2 border-t border-surface-700 px-4 pt-3 pb-[max(0.75rem,env(safe-area-inset-bottom))] empty:hidden sm:px-5 sm:pb-3"><ng-content select="[footer]" /></div>
      </div>
    </div>
  `,
})
export class Modal implements OnInit, OnDestroy {
  readonly title = input('');
  readonly width = input('max-w-lg');
  readonly closed = output<void>();

  // Keep the page behind the dialog from scrolling (matters most on touch devices).
  ngOnInit() { if (openModals++ === 0) document.body.style.overflow = 'hidden'; }
  ngOnDestroy() { if (--openModals === 0) document.body.style.overflow = ''; }
}
