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
  templateUrl: './more-menu.html',
  styleUrl: './more-menu.scss',
})
export class MoreMenu {
  readonly label = input('More actions');
  protected readonly open = signal(false);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);

  protected onDocumentClick(event: MouseEvent) {
    if (this.open() && !this.host.nativeElement.contains(event.target as Node)) this.open.set(false);
  }
}
