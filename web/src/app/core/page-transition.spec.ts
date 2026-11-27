import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRouteSnapshot, Router, provideRouter } from '@angular/router';
import { describe, expect, it, vi } from 'vitest';
import { onPageTransition, routePath } from './page-transition';

@Component({ template: '' })
class Blank {}

async function snapshot(url: string): Promise<ActivatedRouteSnapshot> {
  const router = TestBed.inject(Router);
  await router.navigateByUrl(url);
  return router.routerState.snapshot.root;
}

describe('page transitions', () => {
  function setup() {
    TestBed.configureTestingModule({
      providers: [provideRouter([{ path: 'movies', children: [{ path: '', component: Blank }, { path: ':id', component: Blank }] }])],
    });
  }

  it('reads the full path of the active route', async () => {
    setup();
    expect(routePath(await snapshot('/movies/42?tab=files'))).toBe('/movies/42');
  });

  it('skips the transition when only query params change', async () => {
    setup();
    const from = await snapshot('/movies?sort=title');
    const to = await snapshot('/movies?sort=added');
    const skipTransition = vi.fn();
    onPageTransition({ transition: { skipTransition } as never, from, to });
    expect(skipTransition).toHaveBeenCalled();
  });

  it('animates a change of page', async () => {
    setup();
    const from = await snapshot('/movies');
    const to = await snapshot('/movies/42');
    const skipTransition = vi.fn();
    onPageTransition({ transition: { skipTransition } as never, from, to });
    expect(skipTransition).not.toHaveBeenCalled();
  });
});
