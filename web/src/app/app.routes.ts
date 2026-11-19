import { Routes } from '@angular/router';
import { authGuard, guestGuard } from './core/auth';
import { Shell } from './layout/shell';

export const routes: Routes = [
  { path: 'login', canActivate: [guestGuard], loadComponent: () => import('./pages/login').then(m => m.LoginPage), title: 'Login · Krakarr' },
  {
    path: '',
    component: Shell,
    canActivate: [authGuard],
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'movies' },
      { path: 'movies', loadComponent: () => import('./pages/movies/movie-list').then(m => m.MovieListPage), title: 'Movies · Krakarr' },
      { path: 'movies/add', loadComponent: () => import('./pages/movies/add-movie').then(m => m.AddMoviePage), title: 'Add movie · Krakarr' },
      { path: 'movies/:id', loadComponent: () => import('./pages/movies/movie-detail').then(m => m.MovieDetailPage), title: 'Movie · Krakarr' },
      { path: 'series', loadComponent: () => import('./pages/series').then(m => m.SeriesPage), title: 'Series · Krakarr' },
      { path: 'subtitles', loadComponent: () => import('./pages/subtitles').then(m => m.SubtitlesPage), title: 'Subtitles · Krakarr' },
      { path: 'indexers', loadComponent: () => import('./pages/indexers').then(m => m.IndexersPage), title: 'Indexers · Krakarr' },
      { path: 'search', loadComponent: () => import('./pages/search').then(m => m.SearchPage), title: 'Search · Krakarr' },
      { path: 'downloads', loadComponent: () => import('./pages/downloads').then(m => m.DownloadsPage), title: 'Downloads · Krakarr' },
      { path: 'activity', loadComponent: () => import('./pages/activity').then(m => m.ActivityPage), title: 'Activity · Krakarr' },
      { path: 'settings', loadComponent: () => import('./pages/settings/settings').then(m => m.SettingsPage), title: 'Settings · Krakarr' },
    ],
  },
  { path: '**', redirectTo: '' },
];
