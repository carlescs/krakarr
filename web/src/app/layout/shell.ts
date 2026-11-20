import { Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { Auth } from '../core/auth';
import { Events } from '../core/events';
import { SpeedPipe } from '../core/format';

interface NavItem { path: string; label: string; icon: string; section?: string; }

// Heroicons-style outline paths (24x24).
const ICONS: Record<string, string> = {
  film: 'M3.375 19.5h17.25M3.375 19.5A1.125 1.125 0 0 1 2.25 18.375V5.625C2.25 5.004 2.754 4.5 3.375 4.5h17.25c.621 0 1.125.504 1.125 1.125v12.75c0 .621-.504 1.125-1.125 1.125M3.375 19.5h1.5m15.75 0h-1.5M6 4.5v15m12-15v15M2.25 9h3.75m12 0h3.75M2.25 15h3.75m12 0h3.75',
  tv: 'M6 20.25h12m-7.5-3v3m3-3v3m-10.125-3h17.25c.621 0 1.125-.504 1.125-1.125V4.875c0-.621-.504-1.125-1.125-1.125H3.375c-.621 0-1.125.504-1.125 1.125v11.25c0 .621.504 1.125 1.125 1.125Z',
  cc: 'M7.5 8.25h9m-9 3H12m-9.75 1.51c0 1.6 1.123 2.994 2.707 3.227 1.129.166 2.27.293 3.423.379.35.026.67.21.865.501L12 21l2.755-4.133a1.14 1.14 0 0 1 .865-.501 48.172 48.172 0 0 0 3.423-.379c1.584-.233 2.707-1.626 2.707-3.228V6.741c0-1.602-1.123-2.995-2.707-3.228A48.394 48.394 0 0 0 12 3c-2.392 0-4.744.175-7.043.513C3.373 3.746 2.25 5.14 2.25 6.741v6.018Z',
  search: 'm21 21-5.197-5.197m0 0A7.5 7.5 0 1 0 5.196 5.196a7.5 7.5 0 0 0 10.607 10.607Z',
  server: 'M21.75 17.25v-.228a4.5 4.5 0 0 0-.12-1.03l-2.268-9.64a3.375 3.375 0 0 0-3.285-2.602H7.923a3.375 3.375 0 0 0-3.285 2.602l-2.268 9.64a4.5 4.5 0 0 0-.12 1.03v.228m19.5 0a3 3 0 0 1-3 3H5.25a3 3 0 0 1-3-3m19.5 0a3 3 0 0 0-3-3H5.25a3 3 0 0 0-3 3m16.5 0h.008v.008h-.008v-.008Zm-3 0h.008v.008h-.008v-.008Z',
  download: 'M3 16.5v2.25A2.25 2.25 0 0 0 5.25 21h13.5A2.25 2.25 0 0 0 21 18.75V16.5M16.5 12 12 16.5m0 0L7.5 12m4.5 4.5V3',
  clock: 'M12 6v6h4.5m4.5 0a9 9 0 1 1-18 0 9 9 0 0 1 18 0Z',
  cog: 'M9.594 3.94c.09-.542.56-.94 1.11-.94h2.593c.55 0 1.02.398 1.11.94l.213 1.281c.063.374.313.686.645.87.074.04.147.083.22.127.325.196.72.257 1.075.124l1.217-.456a1.125 1.125 0 0 1 1.37.49l1.296 2.247a1.125 1.125 0 0 1-.26 1.431l-1.003.827c-.293.241-.438.613-.43.992a7.723 7.723 0 0 1 0 .255c-.008.378.137.75.43.991l1.004.827c.424.35.534.955.26 1.43l-1.298 2.247a1.125 1.125 0 0 1-1.369.491l-1.217-.456c-.355-.133-.75-.072-1.076.124a6.47 6.47 0 0 1-.22.128c-.331.183-.581.495-.644.869l-.213 1.281c-.09.543-.56.94-1.11.94h-2.594c-.55 0-1.019-.398-1.11-.94l-.213-1.281c-.062-.374-.312-.686-.644-.87a6.52 6.52 0 0 1-.22-.127c-.325-.196-.72-.257-1.076-.124l-1.217.456a1.125 1.125 0 0 1-1.369-.49l-1.297-2.247a1.125 1.125 0 0 1 .26-1.431l1.004-.827c.292-.24.437-.613.43-.991a6.932 6.932 0 0 1 0-.255c.007-.38-.138-.751-.43-.992l-1.004-.827a1.125 1.125 0 0 1-.26-1.43l1.297-2.247a1.125 1.125 0 0 1 1.37-.491l1.216.456c.356.133.751.072 1.076-.124.072-.044.146-.086.22-.128.332-.183.582-.495.644-.869l.214-1.28Z M15 12a3 3 0 1 1-6 0 3 3 0 0 1 6 0Z',
  sparkles: 'M9.813 15.904 9 18.75l-.813-2.846a4.5 4.5 0 0 0-3.09-3.09L2.25 12l2.846-.813a4.5 4.5 0 0 0 3.09-3.09L9 5.25l.813 2.846a4.5 4.5 0 0 0 3.09 3.09L15.75 12l-2.846.813a4.5 4.5 0 0 0-3.09 3.09ZM18.259 8.715 18 9.75l-.259-1.035a3.375 3.375 0 0 0-2.455-2.456L14.25 6l1.036-.259a3.375 3.375 0 0 0 2.455-2.456L18 2.25l.259 1.035a3.375 3.375 0 0 0 2.456 2.456L21.75 6l-1.035.259a3.375 3.375 0 0 0-2.456 2.456ZM16.894 20.567 16.5 21.75l-.394-1.183a2.25 2.25 0 0 0-1.423-1.423L13.5 18.75l1.183-.394a2.25 2.25 0 0 0 1.423-1.423l.394-1.183.394 1.183a2.25 2.25 0 0 0 1.423 1.423l1.183.394-1.183.394a2.25 2.25 0 0 0-1.423 1.423Z',
  menu: 'M3.75 6.75h16.5M3.75 12h16.5m-16.5 5.25h16.5',
};

@Component({
  selector: 'kr-shell',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, SpeedPipe],
  template: `
    <div class="flex h-full">
      <!-- Sidebar -->
      <aside class="fixed inset-y-0 left-0 z-40 flex w-60 flex-col border-r border-surface-800 bg-surface-900/95 backdrop-blur transition-transform lg:translate-x-0"
             [class.-translate-x-full]="!menuOpen()">
        <a routerLink="/movies" class="flex items-center gap-2.5 px-5 py-5">
          <img src="favicon.svg" alt="" class="size-8" />
          <span class="text-lg font-bold tracking-tight text-white">Krakarr</span>
        </a>
        <nav class="flex-1 space-y-0.5 overflow-y-auto px-3 pb-4">
          @for (item of nav; track item.path) {
            @if (item.section) {
              <div class="px-3 pt-4 pb-1.5 text-[11px] font-semibold uppercase tracking-wider text-slate-500">{{ item.section }}</div>
            }
            <a [routerLink]="item.path" routerLinkActive="!bg-surface-700/70 !text-white" (click)="menuOpen.set(false)"
               class="flex items-center gap-3 rounded-lg px-3 py-2 text-sm font-medium text-slate-400 transition hover:bg-surface-800 hover:text-slate-100">
              <svg class="size-5 shrink-0" fill="none" viewBox="0 0 24 24" stroke-width="1.6" stroke="currentColor">
                <path stroke-linecap="round" stroke-linejoin="round" [attr.d]="icons[item.icon]" />
              </svg>
              <span class="flex-1">{{ item.label }}</span>
              @if (item.path === '/downloads' && activeDownloads() > 0) {
                <span class="badge bg-accent-500/15 text-accent-300">{{ activeDownloads() }}</span>
              }
            </a>
          }
        </nav>
        <div class="border-t border-surface-800 px-5 py-3 text-xs text-slate-500">
          <div class="flex items-center justify-between">
            <span class="flex items-center gap-1.5">
              <span class="size-2 rounded-full" [class]="events.connected() ? 'bg-emerald-400' : 'bg-amber-400'"></span>
              {{ events.connected() ? 'Live' : 'Connecting…' }}
            </span>
            <span>↓ {{ totalDown() | speed }} · ↑ {{ totalUp() | speed }}</span>
          </div>
        </div>
      </aside>
      @if (menuOpen()) {
        <div class="fixed inset-0 z-30 bg-black/50 lg:hidden" (click)="menuOpen.set(false)"></div>
      }

      <!-- Main -->
      <div class="flex min-w-0 flex-1 flex-col lg:pl-60">
        <header class="sticky top-0 z-20 flex h-14 items-center gap-3 border-b border-surface-800 bg-surface-950/80 px-4 backdrop-blur lg:px-8">
          <button class="btn-ghost -ml-2 lg:hidden" (click)="menuOpen.set(true)" aria-label="Open menu">
            <svg class="size-6" fill="none" viewBox="0 0 24 24" stroke-width="1.6" stroke="currentColor"><path stroke-linecap="round" [attr.d]="icons['menu']" /></svg>
          </button>
          <div class="flex-1"></div>
          <span class="text-sm text-slate-400">{{ auth.status()?.username }}</span>
          <button class="btn-ghost btn-sm" (click)="logout()">Sign out</button>
        </header>
        <main class="flex-1 px-4 py-6 lg:px-8">
          <router-outlet />
        </main>
      </div>
    </div>
  `,
})
export class Shell implements OnInit, OnDestroy {
  protected readonly auth = inject(Auth);
  protected readonly events = inject(Events);
  private readonly router = inject(Router);
  protected readonly icons = ICONS;
  protected readonly menuOpen = signal(false);

  protected readonly nav: NavItem[] = [
    { path: '/discover', label: 'Discover', icon: 'sparkles', section: 'Library' },
    { path: '/movies', label: 'Movies', icon: 'film' },
    { path: '/series', label: 'Series', icon: 'tv' },
    { path: '/subtitles', label: 'Subtitles', icon: 'cc' },
    { path: '/search', label: 'Search', icon: 'search', section: 'Sources' },
    { path: '/indexers', label: 'Indexers', icon: 'server' },
    { path: '/downloads', label: 'Downloads', icon: 'download' },
    { path: '/activity', label: 'Activity', icon: 'clock', section: 'System' },
    { path: '/settings', label: 'Settings', icon: 'cog' },
  ];

  protected readonly activeDownloads = computed(() =>
    this.events.downloads().filter(d => d.state === 'Downloading' || d.state === 'FetchingMetadata' || d.state === 'Queued').length);
  protected readonly totalDown = computed(() => this.events.downloads().reduce((s, d) => s + d.downloadRate, 0));
  protected readonly totalUp = computed(() => this.events.downloads().reduce((s, d) => s + d.uploadRate, 0));

  ngOnInit() { this.events.start(); }
  ngOnDestroy() { this.events.stop(); }

  async logout() {
    await this.auth.logout();
    await this.events.stop();
    this.router.navigateByUrl('/login');
  }
}
