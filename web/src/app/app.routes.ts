import { Routes } from '@angular/router';
import { authGuard, guestGuard, roleGuard } from './core/auth';
import { Shell } from './layout/shell';

export const routes: Routes = [
  { path: 'login', canActivate: [guestGuard], loadComponent: () => import('./pages/login').then(m => m.LoginPage), title: 'Login · Krakarr' },
  {
    path: '',
    component: Shell,
    canActivate: [authGuard],
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'movies' },
      { path: 'discover', loadComponent: () => import('./pages/discover').then(m => m.DiscoverPage), title: 'Discover · Krakarr' },
      { path: 'discover/:media/:list', loadComponent: () => import('./pages/discover-list').then(m => m.DiscoverListPage), title: 'Discover · Krakarr' },
      { path: 'movies', loadComponent: () => import('./pages/movies/movie-list').then(m => m.MovieListPage), title: 'Movies · Krakarr' },
      { path: 'movies/import', canActivate: [roleGuard('Manager')], loadComponent: () => import('./pages/library-import').then(m => m.LibraryImportPage), data: { kind: 'movies' }, title: 'Import movies · Krakarr' },
      { path: 'movies/add', canActivate: [roleGuard('Manager')], loadComponent: () => import('./pages/movies/add-movie').then(m => m.AddMoviePage), title: 'Add movie · Krakarr' },
      { path: 'movies/:id', loadComponent: () => import('./pages/movies/movie-detail').then(m => m.MovieDetailPage), title: 'Movie · Krakarr' },
      { path: 'series', loadComponent: () => import('./pages/series/series-list').then(m => m.SeriesListPage), title: 'Series · Krakarr' },
      { path: 'series/import', canActivate: [roleGuard('Manager')], loadComponent: () => import('./pages/library-import').then(m => m.LibraryImportPage), data: { kind: 'series' }, title: 'Import series · Krakarr' },
      { path: 'series/add', canActivate: [roleGuard('Manager')], loadComponent: () => import('./pages/series/add-series').then(m => m.AddSeriesPage), title: 'Add series · Krakarr' },
      { path: 'series/:id', loadComponent: () => import('./pages/series/series-detail').then(m => m.SeriesDetailPage), title: 'Series · Krakarr' },
      { path: 'calendar', loadComponent: () => import('./pages/calendar').then(m => m.CalendarPage), title: 'Calendar · Krakarr' },
      { path: 'subtitles', canActivate: [roleGuard('Manager')], loadComponent: () => import('./pages/subtitles').then(m => m.SubtitlesPage), title: 'Subtitles · Krakarr' },
      { path: 'indexers', canActivate: [roleGuard('Admin')], loadComponent: () => import('./pages/indexers').then(m => m.IndexersPage), title: 'Indexers · Krakarr' },
      { path: 'search', canActivate: [roleGuard('Manager')], loadComponent: () => import('./pages/search').then(m => m.SearchPage), title: 'Search · Krakarr' },
      { path: 'downloads', canActivate: [roleGuard('Manager')], loadComponent: () => import('./pages/downloads').then(m => m.DownloadsPage), title: 'Downloads · Krakarr' },
      { path: 'activity', canActivate: [roleGuard('Manager')], loadComponent: () => import('./pages/activity').then(m => m.ActivityPage), title: 'Activity · Krakarr' },
      { path: 'settings', loadComponent: () => import('./pages/settings/settings').then(m => m.SettingsPage), title: 'Settings · Krakarr' },
    ],
  },
  { path: '**', redirectTo: '' },
];
