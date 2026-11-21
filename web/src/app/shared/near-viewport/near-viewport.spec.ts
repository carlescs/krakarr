import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { NearViewport } from './near-viewport';

@Component({
  imports: [NearViewport],
  template: `<div (krNearViewport)="hits = hits + 1"></div>`,
})
class Host { hits = 0; }

describe('NearViewport', () => {
  afterEach(() => vi.unstubAllGlobals());

  it('emits straight away without IntersectionObserver', async () => {
    vi.stubGlobal('IntersectionObserver', undefined);
    const fixture = TestBed.createComponent(Host);
    await fixture.whenStable();
    expect(fixture.componentInstance.hits).toBe(1);
  });

  it('emits when the element comes near the viewport and disconnects when destroyed', async () => {
    let callback!: (entries: Partial<IntersectionObserverEntry>[]) => void;
    const disconnect = vi.fn();
    vi.stubGlobal('IntersectionObserver', class {
      constructor(cb: typeof callback, readonly options: IntersectionObserverInit) { callback = cb; }
      observe() {}
      unobserve() {}
      disconnect = disconnect;
    });
    const fixture = TestBed.createComponent(Host);
    await fixture.whenStable();
    expect(fixture.componentInstance.hits).toBe(0);

    callback([{ isIntersecting: false }]);
    callback([{ isIntersecting: true }]);
    expect(fixture.componentInstance.hits).toBe(1);

    fixture.destroy();
    expect(disconnect).toHaveBeenCalled();
  });
});
