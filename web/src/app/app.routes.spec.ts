import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Route, Router, provideRouter } from '@angular/router';
import { describe, expect, it } from 'vitest';
import { routes } from './app.routes';

@Component({ template: '' })
class Blank {}

describe('routes', () => {
  // The old movie, series and people searches are tabs of the one search page now; bookmarks must still land there.
  it.each([
    ['/movies/add', '/discover/search?type=movies'],
    ['/series/add', '/discover/search?type=series'],
    ['/people', '/discover/search?type=people'],
  ])('redirects %s to %s', async (from, to) => {
    const shell = routes.find(r => r.children)!.children!;
    const redirects: Route[] = shell.filter(r => r.redirectTo && typeof r.redirectTo === 'string');
    TestBed.configureTestingModule({
      providers: [provideRouter([...redirects, { path: 'discover/search', component: Blank }, { path: 'people/:id', component: Blank }])],
    });
    const router = TestBed.inject(Router);
    await router.navigateByUrl(from);
    expect(router.url).toBe(to);
    await router.navigateByUrl('/people/6384');
    expect(router.url).toBe('/people/6384');
  });
});
