import { Component, ElementRef, inject, input, signal } from '@angular/core';

/**
 * A "⋯" button that opens a small popover with secondary actions. Project the items as
 * `<a class="menu-item">` or `<button class="menu-item">`; clicking one closes the menu.
 */
@Component({
  selector: 'kr-more-menu',
  host: {
    class: 'relative inline-block',
    '(document:click)': 'onDocumentClick($event)',
    '(document:keydown.escape)': 'open.set(false)',
  },
  template: `
    <button type="button" class="btn-ghost h-full px-2.5" [attr.aria-label]="label()" [attr.aria-expanded]="open()" aria-haspopup="menu"
            (click)="open.set(!open())">
      <svg class="size-5" viewBox="0 0 20 20" fill="currentColor" aria-hidden="true">
        <path d="M3 10a1.5 1.5 0 1 1 3 0 1.5 1.5 0 0 1-3 0Zm5.5 0a1.5 1.5 0 1 1 3 0 1.5 1.5 0 0 1-3 0ZM15.5 8.5a1.5 1.5 0 1 0 0 3 1.5 1.5 0 0 0 0-3Z" />
      </svg>
    </button>
    @if (open()) {
      <div role="menu" class="card absolute right-0 top-full z-20 mt-1 min-w-52 py-1 shadow-lg" (click)="open.set(false)">
        <ng-content />
      </div>
    }
  `,
})
export class MoreMenu {
  readonly label = input('More actions');
  protected readonly open = signal(false);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  protected onDocumentClick(event: MouseEvent) {
    if (this.open() && !this.host.nativeElement.contains(event.target as Node)) this.open.set(false);
  }
}
