import { Component, input, output } from '@angular/core';

@Component({
  selector: 'kr-modal',
  template: `
    <div class="fixed inset-0 z-50 flex items-start justify-center overflow-y-auto bg-black/60 p-4 pt-[8vh] backdrop-blur-sm" (click)="closed.emit()">
      <div class="card w-full" [class]="width()" (click)="$event.stopPropagation()" role="dialog" aria-modal="true">
        <div class="flex items-center justify-between border-b border-surface-700 px-5 py-4">
          <h2 class="text-base font-semibold text-white">{{ title() }}</h2>
          <button class="text-slate-400 hover:text-white" (click)="closed.emit()" aria-label="Close">✕</button>
        </div>
        <div class="p-5"><ng-content /></div>
        <div class="flex justify-end gap-2 border-t border-surface-700 px-5 py-3 empty:hidden"><ng-content select="[footer]" /></div>
      </div>
    </div>
  `,
})
export class Modal {
  readonly title = input('');
  readonly width = input('max-w-lg');
  readonly closed = output<void>();
}
