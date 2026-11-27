import { CdkVirtualScrollViewport, VIRTUAL_SCROLL_STRATEGY, VirtualScrollStrategy } from '@angular/cdk/scrolling';
import { DestroyRef, Directive, ElementRef, OnChanges, afterNextRender, forwardRef, inject, input } from '@angular/core';
import { Subject, distinctUntilChanged } from 'rxjs';

const SLOTS: number[][] = [];

/** `[0, 1, ..., count - 1]`, shared between calls so grid rows don't each allocate their own index list. */
export function gridSlots(count: number): readonly number[] {
  let slots = SLOTS[count];
  if (!slots) {
    slots = Array.from({ length: count }, (_, i) => i);
    SLOTS[count] = slots;
  }
  return slots;
}

export interface GridMetrics {
  cols: number;
  rowHeight: number;
  gapX: number;
}

/**
 * Computes column count and exact pixel row height for the responsive poster grid, matching `POSTER_GRID`
 * breakpoints (`3` -> `md:4` -> `lg:5` -> `xl:6` -> `2xl:8`) and `kr-poster`'s 2:3 aspect ratio.
 */
export function gridMetrics(containerWidth: number, viewportWidth: number, variant: 'movie' | 'series'): GridMetrics {
  const vw = viewportWidth > 0 ? viewportWidth : 1024;
  const cols = vw >= 1536 ? 8 : vw >= 1280 ? 6 : vw >= 1024 ? 5 : vw >= 768 ? 4 : 3;
  const gapX = vw >= 640 ? 16 : 12;
  // Fall back to an estimated content width when the element hasn't been laid out (e.g. jsdom).
  const width = containerWidth > 0 ? containerWidth : Math.max(320, vw >= 1024 ? vw - 304 : vw - 32);
  const cardWidth = Math.max(80, (width - (cols - 1) * gapX) / cols);
  const posterHeight = cardWidth * 1.5;
  // Below-poster text + bottom row padding (pb-4 = 16px). Series cards also have a progress bar (mt-2 + h-1.5 = 14px).
  const metaHeight = variant === 'series'
    ? (vw >= 640 ? 74 : 68)
    : (vw >= 640 ? 60 : 54);
  return { cols, rowHeight: Math.ceil(posterHeight + metaHeight), gapX };
}

/**
 * Tracks an element's width and `window.innerWidth` via `ResizeObserver` (falling back to `window` resize events when
 * `ResizeObserver` is unavailable, e.g. in jsdom). Returns a teardown function.
 */
export function observeContainerWidth(
  element: HTMLElement,
  onChange: (containerWidth: number, viewportWidth: number) => void,
): () => void {
  const measure = () => onChange(element.clientWidth, typeof window !== 'undefined' ? window.innerWidth : 1024);
  measure();

  let observer: ResizeObserver | null = null;
  if (typeof ResizeObserver !== 'undefined') {
    observer = new ResizeObserver(measure);
    observer.observe(element);
  }
  const onResize = () => measure();
  if (typeof window !== 'undefined') window.addEventListener('resize', onResize, { passive: true });
  return () => {
    observer?.disconnect();
    if (typeof window !== 'undefined') window.removeEventListener('resize', onResize);
  };
}

/**
 * Angular CDK `VirtualScrollStrategy` for lists whose rows have known, possibly heterogeneous heights
 * (e.g. 48px collection headers interleaved with poster grid rows). Uses a prefix-sum array for O(log N)
 * scroll-offset lookups and exact total content height.
 */
export class VariableHeightVirtualScrollStrategy implements VirtualScrollStrategy {
  private readonly scrolledIndexSubject = new Subject<number>();
  readonly scrolledIndexChange = this.scrolledIndexSubject.pipe(distinctUntilChanged());

  private viewport: CdkVirtualScrollViewport | null = null;
  private heights: readonly number[] = [];
  private offsets: number[] = [0];

  constructor(
    private minBufferPx = 600,
    private maxBufferPx = 1200,
  ) {}

  attach(viewport: CdkVirtualScrollViewport): void {
    this.viewport = viewport;
    this.updateTotalContentSize();
    this.updateRenderedRange(true);
  }

  detach(): void {
    this.scrolledIndexSubject.complete();
    this.viewport = null;
  }

  update(heights: readonly number[], minBufferPx = this.minBufferPx, maxBufferPx = this.maxBufferPx): void {
    this.minBufferPx = minBufferPx;
    this.maxBufferPx = maxBufferPx;
    this.heights = heights;
    this.offsets = new Array(heights.length + 1);
    this.offsets[0] = 0;
    for (let i = 0; i < heights.length; i++) {
      this.offsets[i + 1] = this.offsets[i] + heights[i];
    }
    this.updateTotalContentSize();
    this.updateRenderedRange(true);
  }

  onContentScrolled(): void {
    this.updateRenderedRange(false);
  }

  onDataLengthChanged(): void {
    this.updateTotalContentSize();
    this.updateRenderedRange(true);
  }

  onContentRendered(): void { /* no-op */ }
  onRenderedOffsetChanged(): void { /* no-op */ }

  scrollToIndex(index: number, behavior: ScrollBehavior): void {
    if (!this.viewport || this.heights.length === 0) return;
    const clamped = Math.max(0, Math.min(this.heights.length - 1, index));
    this.viewport.scrollToOffset(this.offsets[clamped], behavior);
  }

  private updateTotalContentSize(): void {
    if (!this.viewport) return;
    const dataLength = Math.min(this.viewport.getDataLength(), this.heights.length);
    this.viewport.setTotalContentSize(this.offsets[dataLength] ?? 0);
  }

  private updateRenderedRange(force: boolean): void {
    if (!this.viewport) return;
    const dataLength = Math.min(this.viewport.getDataLength(), this.heights.length);
    if (dataLength === 0) {
      this.viewport.setRenderedRange({ start: 0, end: 0 });
      this.viewport.setRenderedContentOffset(0);
      this.scrolledIndexSubject.next(0);
      return;
    }

    const totalSize = this.offsets[dataLength];
    const scrollOffset = Math.max(0, Math.min(this.viewport.measureScrollOffset(), totalSize));
    const viewportSize = this.viewport.getViewportSize();
    const firstVisible = this.indexAtOffset(scrollOffset, dataLength);
    const current = this.viewport.getRenderedRange();

    if (!force && current.start < current.end && current.end <= dataLength) {
      const renderedStartPx = this.offsets[current.start];
      const renderedEndPx = this.offsets[current.end];
      const startBuffer = scrollOffset - renderedStartPx;
      const endBuffer = renderedEndPx - (scrollOffset + viewportSize);
      const needsMoreBefore = startBuffer < this.minBufferPx && current.start > 0;
      const needsMoreAfter = endBuffer < this.minBufferPx && current.end < dataLength;
      if (!needsMoreBefore && !needsMoreAfter) {
        this.scrolledIndexSubject.next(firstVisible);
        return;
      }
    }

    const startPx = Math.max(0, scrollOffset - this.maxBufferPx);
    const endPx = Math.min(totalSize, scrollOffset + viewportSize + this.maxBufferPx);
    const start = this.indexAtOffset(startPx, dataLength);
    const end = Math.min(dataLength, this.indexAtOffset(endPx, dataLength) + 1);

    this.viewport.setRenderedRange({ start, end });
    this.viewport.setRenderedContentOffset(this.offsets[start]);
    this.scrolledIndexSubject.next(firstVisible);
  }

  /** Largest index `i` in `[0, count - 1]` with `offsets[i] <= px`. */
  private indexAtOffset(px: number, count: number): number {
    let lo = 0;
    let hi = count - 1;
    while (lo < hi) {
      const mid = (lo + hi + 1) >> 1;
      if (this.offsets[mid] <= px) lo = mid;
      else hi = mid - 1;
    }
    return lo;
  }
}

/** Provides `VariableHeightVirtualScrollStrategy` to a `cdk-virtual-scroll-viewport`. */
@Directive({
  selector: 'cdk-virtual-scroll-viewport[krRowHeights]',
  providers: [{
    provide: VIRTUAL_SCROLL_STRATEGY,
    useFactory: (dir: KrRowHeights) => dir.strategy,
    deps: [forwardRef(() => KrRowHeights)],
  }],
})
export class KrRowHeights implements OnChanges {
  readonly krRowHeights = input.required<readonly number[]>();
  readonly minBufferPx = input(600);
  readonly maxBufferPx = input(1200);

  readonly strategy = new VariableHeightVirtualScrollStrategy();

  ngOnChanges(): void {
    this.strategy.update(this.krRowHeights(), this.minBufferPx(), this.maxBufferPx());
  }
}

/**
 * Reserves room for a virtualized table's `<thead>`. The viewport sizes itself from rows × item size, but the header
 * sits in the same scrolled content above the rows, so without this the last rows hang out below the viewport (and
 * the card around it). Keeps the viewport's bottom padding equal to the header's height, following its resizes.
 */
@Directive({ selector: 'cdk-virtual-scroll-viewport[krVirtualTable]' })
export class KrVirtualTable {
  constructor() {
    const host = inject<ElementRef<HTMLElement>>(ElementRef).nativeElement;
    const destroyRef = inject(DestroyRef);
    afterNextRender(() => {
      const thead = host.querySelector('thead');
      if (!thead) return;
      const apply = () => { host.style.paddingBottom = `${thead.offsetHeight}px`; };
      apply();
      if (typeof ResizeObserver === 'undefined') return;
      const observer = new ResizeObserver(apply);
      observer.observe(thead);
      destroyRef.onDestroy(() => observer.disconnect());
    });
  }
}
