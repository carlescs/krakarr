import { ActivatedRouteSnapshot, ViewTransitionInfo } from '@angular/router';

/** The path of the deepest active route, without query params or fragment. */
export function routePath(root: ActivatedRouteSnapshot): string {
  const segments: string[] = [];
  for (let route: ActivatedRouteSnapshot | null = root; route; route = route.firstChild) {
    segments.push(...route.url.map(s => s.path));
  }
  return '/' + segments.join('/');
}

/**
 * Animates only real page changes. Navigations that keep the path and change just query params
 * (filters, tabs, search terms) skip the transition, so typing or toggling a filter doesn't flash the page.
 */
export function onPageTransition({ transition, from, to }: ViewTransitionInfo): void {
  if (routePath(from) === routePath(to)) transition.skipTransition();
}
