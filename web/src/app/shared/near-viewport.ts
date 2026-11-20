import { Directive, ElementRef, OnDestroy, OnInit, inject, output } from '@angular/core';

/**
 * Emits while the element is within `rootMargin` of the viewport. After each emission it checks again, so a sentinel
 * that is still visible once more content has rendered keeps asking for more until it is pushed out of view.
 */
@Directive({ selector: '[krNearViewport]' })
export class NearViewport implements OnInit, OnDestroy {
  readonly krNearViewport = output<void>();

  private readonly el = inject(ElementRef<HTMLElement>).nativeElement as HTMLElement;
  private observer?: IntersectionObserver;
  private frame = 0;

  ngOnInit() {
    if (typeof IntersectionObserver === 'undefined') {
      this.krNearViewport.emit();
      return;
    }
    this.observer = new IntersectionObserver(entries => {
      if (!entries.some(e => e.isIntersecting)) return;
      this.krNearViewport.emit();
      // An observer only reports changes: observe afresh after the new content renders to get the current state.
      cancelAnimationFrame(this.frame);
      this.frame = requestAnimationFrame(() => {
        this.observer?.unobserve(this.el);
        this.observer?.observe(this.el);
      });
    }, { rootMargin: '1200px 0px' });
    this.observer.observe(this.el);
  }

  ngOnDestroy() {
    cancelAnimationFrame(this.frame);
    this.observer?.disconnect();
  }
}
