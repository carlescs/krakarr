import { Component, DestroyRef, ElementRef, afterNextRender, afterRenderEffect, inject, input, signal, viewChild } from '@angular/core';

/** A title's overview, clamped to a few lines with a toggle to read the rest when it doesn't fit. */
@Component({
  selector: 'kr-overview',
  templateUrl: './overview.html',
  styleUrl: './overview.scss',
  host: { class: 'block max-w-3xl' },
})
export class Overview {
  readonly text = input<string | null | undefined>();

  protected readonly expanded = signal(false);
  protected readonly overflowing = signal(false);
  private readonly body = viewChild.required<ElementRef<HTMLElement>>('body');

  constructor() {
    // Re-measure whenever the text changes or the clamp comes back.
    afterRenderEffect(() => {
      this.text();
      if (!this.expanded()) this.measure();
    });
    // …and when the width changes (window resize, sidebar toggle).
    const destroyRef = inject(DestroyRef);
    afterNextRender(() => {
      if (typeof ResizeObserver === 'undefined') return;
      const observer = new ResizeObserver(() => { if (!this.expanded()) this.measure(); });
      observer.observe(this.body().nativeElement);
      destroyRef.onDestroy(() => observer.disconnect());
    });
  }

  protected toggle(): void {
    this.expanded.update(v => !v);
  }

  private measure(): void {
    const el = this.body().nativeElement;
    this.overflowing.set(el.scrollHeight > el.clientHeight + 1);
  }
}
