import { Component, OnDestroy, OnInit, input, output } from '@angular/core';

let openModals = 0;

/**
 * Dialog that is centred on larger screens and becomes a bottom sheet on phones,
 * with a scrollable body and the footer actions always in reach.
 */
@Component({
  selector: 'kr-modal',
  host: { '(document:keydown.escape)': 'closed.emit()' },
  templateUrl: './modal.html',
  styleUrl: './modal.scss',
})
export class Modal implements OnInit, OnDestroy {
  readonly title = input('');
  readonly width = input('max-w-lg');
  readonly closed = output<void>();

  // Keep the page behind the dialog from scrolling (matters most on touch devices).
  ngOnInit() { if (openModals++ === 0) document.body.style.overflow = 'hidden'; }
  ngOnDestroy() { if (--openModals === 0) document.body.style.overflow = ''; }
}
