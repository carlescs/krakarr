import { inject } from '@angular/core';
import { Routes } from '@angular/router';
import { Auth, authGuard, guestGuard, roleGuard } from './core/auth';
import { calendarRouteMatcher } from './core/calendar';
import { Shell } from './layout/shell/shell';

/** Staff land on the dashboard, plain users on the library. Runs before the guards, so it may have to load the status. */
async function homeRedirect(): Promise<string> {
  const auth = inject(Auth);
  const status = auth.status() ?? (await auth.refresh().catch(() => null));
  return status?.role === 'Admin' || status?.role === 'Manager' ? 'dashboard' : 'movies';
}

export const routes: Routes = [
  { path: 'login', canActivate: [guestGuard], loadComponent: () => import('./pages/login/login').then(m => m.LoginPage), title: 'Login · Krakarr' },
  {
    path: '',
    component: Shell,
    canActivate: [authGuard],
    children: [
      { path: '', pathMatch: 'full', redirectTo: homeRedirect },
      { path: 'dashboard', canActivate: [roleGuard('Manager')], loadComponent: () => import('./pages/dashboard/dashboard').then(m => m.DashboardPage), title: 'Dashboard · Krakarr' },
      { path: 'discover', loadComponent: () => import('./pages/discover/discover').then(m => m.DiscoverPage), title: 'Discover · Krakarr' },
      { path: 'discover/search', loadComponent: () => import('./pages/unified-search/unified-search').then(m => m.UnifiedSearchPage), title: 'Search · Krakarr' },
      { path: 'discover/:media/:list', loadComponent: () => import('./pages/discover-list/discover-list').then(m => m.DiscoverListPage), title: 'Discover · Krakarr' },
      { path: 'movies', loadComponent: () => import('./pages/movies/movie-list/movie-list').then(m => m.MovieListPage), title: 'Movies · Krakarr' },
      { path: 'movies/import', canActivate: [roleGuard('Manager')], loadComponent: () => import('./pages/library-import/library-import').then(m => m.LibraryImportPage), data: { kind: 'movies' }, title: 'Import movies · Krakarr' },
      // The separate movie, series and people searches became tabs of the one search page; keep old links working.
      { path: 'movies/add', pathMatch: 'full', redirectTo: '/discover/search?type=movies' },
      { path: 'movies/tmdb/:tmdbId', loadComponent: () => import('./pages/title-preview/title-preview').then(m => m.TitlePreviewPage), data: { kind: 'movie' }, title: 'Movie · Krakarr' },
      { path: 'movies/collections/:id', loadComponent: () => import('./pages/movies/collection-detail/collection-detail').then(m => m.CollectionDetailPage), title: 'Collection · Krakarr' },
      { path: 'movies/:id', loadComponent: () => import('./pages/movies/movie-detail/movie-detail').then(m => m.MovieDetailPage), title: 'Movie · Krakarr' },
      { path: 'series', loadComponent: () => import('./pages/series/series-list/series-list').then(m => m.SeriesListPage), title: 'Series · Krakarr' },
      { path: 'series/import', canActivate: [roleGuard('Manager')], loadComponent: () => import('./pages/library-import/library-import').then(m => m.LibraryImportPage), data: { kind: 'series' }, title: 'Import series · Krakarr' },
      { path: 'series/add', pathMatch: 'full', redirectTo: '/discover/search?type=series' },
      { path: 'series/tmdb/:tmdbId', loadComponent: () => import('./pages/title-preview/title-preview').then(m => m.TitlePreviewPage), data: { kind: 'series' }, title: 'Series · Krakarr' },
      { path: 'series/:id', loadComponent: () => import('./pages/series/series-detail/series-detail').then(m => m.SeriesDetailPage), title: 'Series · Krakarr' },
      { path: 'keywords', loadComponent: () => import('./pages/keyword-search/keyword-search').then(m => m.KeywordSearchPage), title: 'Keywords · Krakarr' },
      { path: 'people', pathMatch: 'full', redirectTo: '/discover/search?type=people' },
      { path: 'people/:id', loadComponent: () => import('./pages/person/person').then(m => m.PersonPage), title: 'Person · Krakarr' },
      { path: 'requests', loadComponent: () => import('./pages/requests/requests').then(m => m.RequestsPage), title: 'Requests · Krakarr' },
      { matcher: calendarRouteMatcher, loadComponent: () => import('./pages/calendar/calendar').then(m => m.CalendarPage), title: 'Calendar · Krakarr' },
      { path: 'subtitles', canActivate: [roleGuard('Manager')], loadComponent: () => import('./pages/subtitles/subtitles').then(m => m.SubtitlesPage), title: 'Subtitles · Krakarr' },
      { path: 'indexers', canActivate: [roleGuard('Admin')], loadComponent: () => import('./pages/indexers/indexers').then(m => m.IndexersPage), title: 'Indexers · Krakarr' },
      { path: 'search', canActivate: [roleGuard('Manager')], loadComponent: () => import('./pages/search/search').then(m => m.SearchPage), title: 'Search · Krakarr' },
      { path: 'downloads', canActivate: [roleGuard('Manager')], loadComponent: () => import('./pages/downloads/downloads').then(m => m.DownloadsPage), title: 'Downloads · Krakarr' },
      { path: 'activity', canActivate: [roleGuard('Manager')], loadComponent: () => import('./pages/activity/activity').then(m => m.ActivityPage), title: 'Activity · Krakarr' },
      { path: 'settings', loadComponent: () => import('./pages/settings/settings/settings').then(m => m.SettingsPage), title: 'Settings · Krakarr' },
      { path: 'settings/:tab', loadComponent: () => import('./pages/settings/settings/settings').then(m => m.SettingsPage), title: 'Settings · Krakarr' },
    ],
  },
  { path: '**', redirectTo: '' },
];
