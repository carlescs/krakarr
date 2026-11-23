import { CdkVirtualScrollViewport } from '@angular/cdk/scrolling';
import { describe, expect, it, vi } from 'vitest';
import { VariableHeightVirtualScrollStrategy, gridMetrics } from './virtual-scroll';

describe('gridMetrics', () => {
  it('matches responsive column breakpoints for movies and series', () => {
    expect(gridMetrics(360, 400, 'movie').cols).toBe(3);
    expect(gridMetrics(720, 768, 'movie').cols).toBe(4);
    expect(gridMetrics(900, 1024, 'movie').cols).toBe(5);
    expect(gridMetrics(1100, 1280, 'movie').cols).toBe(6);
    expect(gridMetrics(1400, 1536, 'movie').cols).toBe(8);
  });

  it('adds extra vertical space for the series progress bar', () => {
    const movie = gridMetrics(1000, 1280, 'movie');
    const series = gridMetrics(1000, 1280, 'series');
    expect(series.cols).toBe(movie.cols);
    expect(series.rowHeight).toBeGreaterThan(movie.rowHeight);
  });
});

describe('VariableHeightVirtualScrollStrategy', () => {
  function mockViewport(dataLength: number, viewportSize = 600) {
    let scrollOffset = 0;
    let renderedRange = { start: 0, end: 0 };
    let renderedOffset = 0;
    let totalSize = 0;
    const viewport = {
      getDataLength: () => dataLength,
      getViewportSize: () => viewportSize,
      measureScrollOffset: () => scrollOffset,
      getRenderedRange: () => renderedRange,
      setTotalContentSize: vi.fn((s: number) => { totalSize = s; }),
      setRenderedRange: vi.fn((r: { start: number; end: number }) => { renderedRange = r; }),
      setRenderedContentOffset: vi.fn((o: number) => { renderedOffset = o; }),
      scrollToOffset: vi.fn((o: number) => { scrollOffset = o; }),
    };
    return {
      viewport: viewport as unknown as CdkVirtualScrollViewport,
      setScroll: (o: number) => { scrollOffset = o; },
      state: () => ({ scrollOffset, renderedRange, renderedOffset, totalSize }),
      mocks: viewport,
    };
  }

  it('computes exact total height, rendered range, and offsets across heterogeneous row heights', () => {
    // 1 header (48px) + 10 card rows (300px) + 1 header (64px) + 10 card rows (300px) = 6112px
    const heights = [48, ...Array(10).fill(300), 64, ...Array(10).fill(300)];
    const strategy = new VariableHeightVirtualScrollStrategy(200, 400);
    const { viewport, setScroll, state, mocks } = mockViewport(heights.length, 600);

    strategy.update(heights, 200, 400);
    strategy.attach(viewport);
    expect(state().totalSize).toBe(6112);
    expect(state().renderedRange.start).toBe(0);
    expect(state().renderedOffset).toBe(0);
    expect(state().renderedRange.end).toBeGreaterThan(2);
    expect(state().renderedRange.end).toBeLessThan(heights.length);

    // Scroll to the second group header (offset = 48 + 3000 = 3048)
    setScroll(3048);
    strategy.onContentScrolled();
    expect(state().renderedRange.start).toBeGreaterThan(0);
    expect(state().renderedOffset).toBeGreaterThan(0);

    // scrollToIndex jumps directly to prefix-sum offset of the second header (index 11)
    strategy.scrollToIndex(11, 'auto');
    expect(mocks.scrollToOffset).toHaveBeenCalledWith(3048, 'auto');
  });

  it('handles empty lists cleanly', () => {
    const strategy = new VariableHeightVirtualScrollStrategy();
    const { viewport, state } = mockViewport(0, 600);
    strategy.update([]);
    strategy.attach(viewport);
    expect(state().totalSize).toBe(0);
    expect(state().renderedRange).toEqual({ start: 0, end: 0 });
    expect(state().renderedOffset).toBe(0);
  });
});
