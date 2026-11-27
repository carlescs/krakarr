import { Component, DestroyRef, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { filter, map } from 'rxjs';
import { NavigationEnd, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { Auth } from '../../core/auth';
import { Events } from '../../core/events';
import { SpeedPipe } from '../../core/format';
import { Notifications } from '../../core/notifications';
import { PushNavigation } from '../../core/push';
import { NotificationBell } from '../../shared/notification-bell/notification-bell';
import { BackgroundTasksMenu } from '../../shared/background-tasks/background-tasks';
import { BackgroundTasks } from '../../core/background-tasks';

interface NavItem { path: string; label: string; icon: string; section?: string; access?: 'staff' | 'admin'; }

// Heroicons-style outline paths (24x24).
const ICONS: Record<string, string> = {
  chart: 'M3 13.125C3 12.504 3.504 12 4.125 12h2.25c.621 0 1.125.504 1.125 1.125v6.75C7.5 20.496 6.996 21 6.375 21h-2.25A1.125 1.125 0 0 1 3 19.875v-6.75ZM9.75 8.625c0-.621.504-1.125 1.125-1.125h2.25c.621 0 1.125.504 1.125 1.125v11.25c0 .621-.504 1.125-1.125 1.125h-2.25a1.125 1.125 0 0 1-1.125-1.125V8.625ZM16.5 4.125c0-.621.504-1.125 1.125-1.125h2.25C20.496 3 21 3.504 21 4.125v15.75c0 .621-.504 1.125-1.125 1.125h-2.25a1.125 1.125 0 0 1-1.125-1.125V4.125Z',
  film: 'M3.375 19.5h17.25M3.375 19.5A1.125 1.125 0 0 1 2.25 18.375V5.625C2.25 5.004 2.754 4.5 3.375 4.5h17.25c.621 0 1.125.504 1.125 1.125v12.75c0 .621-.504 1.125-1.125 1.125M3.375 19.5h1.5m15.75 0h-1.5M6 4.5v15m12-15v15M2.25 9h3.75m12 0h3.75M2.25 15h3.75m12 0h3.75',
  tv: 'M6 20.25h12m-7.5-3v3m3-3v3m-10.125-3h17.25c.621 0 1.125-.504 1.125-1.125V4.875c0-.621-.504-1.125-1.125-1.125H3.375c-.621 0-1.125.504-1.125 1.125v11.25c0 .621.504 1.125 1.125 1.125Z',
  cc: 'M7.5 8.25h9m-9 3H12m-9.75 1.51c0 1.6 1.123 2.994 2.707 3.227 1.129.166 2.27.293 3.423.379.35.026.67.21.865.501L12 21l2.755-4.133a1.14 1.14 0 0 1 .865-.501 48.172 48.172 0 0 0 3.423-.379c1.584-.233 2.707-1.626 2.707-3.228V6.741c0-1.602-1.123-2.995-2.707-3.228A48.394 48.394 0 0 0 12 3c-2.392 0-4.744.175-7.043.513C3.373 3.746 2.25 5.14 2.25 6.741v6.018Z',
  search: 'm21 21-5.197-5.197m0 0A7.5 7.5 0 1 0 5.196 5.196a7.5 7.5 0 0 0 10.607 10.607Z',
  server: 'M21.75 17.25v-.228a4.5 4.5 0 0 0-.12-1.03l-2.268-9.64a3.375 3.375 0 0 0-3.285-2.602H7.923a3.375 3.375 0 0 0-3.285 2.602l-2.268 9.64a4.5 4.5 0 0 0-.12 1.03v.228m19.5 0a3 3 0 0 1-3 3H5.25a3 3 0 0 1-3-3m19.5 0a3 3 0 0 0-3-3H5.25a3 3 0 0 0-3 3m16.5 0h.008v.008h-.008v-.008Zm-3 0h.008v.008h-.008v-.008Z',
  download: 'M3 16.5v2.25A2.25 2.25 0 0 0 5.25 21h13.5A2.25 2.25 0 0 0 21 18.75V16.5M16.5 12 12 16.5m0 0L7.5 12m4.5 4.5V3',
  clock: 'M12 6v6h4.5m4.5 0a9 9 0 1 1-18 0 9 9 0 0 1 18 0Z',
  cog: 'M9.594 3.94c.09-.542.56-.94 1.11-.94h2.593c.55 0 1.02.398 1.11.94l.213 1.281c.063.374.313.686.645.87.074.04.147.083.22.127.325.196.72.257 1.075.124l1.217-.456a1.125 1.125 0 0 1 1.37.49l1.296 2.247a1.125 1.125 0 0 1-.26 1.431l-1.003.827c-.293.241-.438.613-.43.992a7.723 7.723 0 0 1 0 .255c-.008.378.137.75.43.991l1.004.827c.424.35.534.955.26 1.43l-1.298 2.247a1.125 1.125 0 0 1-1.369.491l-1.217-.456c-.355-.133-.75-.072-1.076.124a6.47 6.47 0 0 1-.22.128c-.331.183-.581.495-.644.869l-.213 1.281c-.09.543-.56.94-1.11.94h-2.594c-.55 0-1.019-.398-1.11-.94l-.213-1.281c-.062-.374-.312-.686-.644-.87a6.52 6.52 0 0 1-.22-.127c-.325-.196-.72-.257-1.076-.124l-1.217.456a1.125 1.125 0 0 1-1.369-.49l-1.297-2.247a1.125 1.125 0 0 1 .26-1.431l1.004-.827c.292-.24.437-.613.43-.991a6.932 6.932 0 0 1 0-.255c.007-.38-.138-.751-.43-.992l-1.004-.827a1.125 1.125 0 0 1-.26-1.43l1.297-2.247a1.125 1.125 0 0 1 1.37-.491l1.216.456c.356.133.751.072 1.076-.124.072-.044.146-.086.22-.128.332-.183.582-.495.644-.869l.214-1.28Z M15 12a3 3 0 1 1-6 0 3 3 0 0 1 6 0Z',
  sparkles: 'M9.813 15.904 9 18.75l-.813-2.846a4.5 4.5 0 0 0-3.09-3.09L2.25 12l2.846-.813a4.5 4.5 0 0 0 3.09-3.09L9 5.25l.813 2.846a4.5 4.5 0 0 0 3.09 3.09L15.75 12l-2.846.813a4.5 4.5 0 0 0-3.09 3.09ZM18.259 8.715 18 9.75l-.259-1.035a3.375 3.375 0 0 0-2.455-2.456L14.25 6l1.036-.259a3.375 3.375 0 0 0 2.455-2.456L18 2.25l.259 1.035a3.375 3.375 0 0 0 2.456 2.456L21.75 6l-1.035.259a3.375 3.375 0 0 0-2.456 2.456ZM16.894 20.567 16.5 21.75l-.394-1.183a2.25 2.25 0 0 0-1.423-1.423L13.5 18.75l1.183-.394a2.25 2.25 0 0 0 1.423-1.423l.394-1.183.394 1.183a2.25 2.25 0 0 0 1.423 1.423l1.183.394-1.183.394a2.25 2.25 0 0 0-1.423 1.423Z',
  calendar: 'M6.75 3v2.25M17.25 3v2.25M3 18.75V7.5a2.25 2.25 0 0 1 2.25-2.25h13.5A2.25 2.25 0 0 1 21 7.5v11.25m-18 0A2.25 2.25 0 0 0 5.25 21h13.5A2.25 2.25 0 0 0 21 18.75m-18 0v-7.5A2.25 2.25 0 0 1 5.25 9h13.5A2.25 2.25 0 0 1 21 11.25v7.5',
  menu: 'M3.75 6.75h16.5M3.75 12h16.5m-16.5 5.25h16.5',
  inbox: 'M2.25 13.5h3.86a2.25 2.25 0 0 1 2.012 1.244l.256.512a2.25 2.25 0 0 0 2.013 1.244h3.218a2.25 2.25 0 0 0 2.013-1.244l.256-.512a2.25 2.25 0 0 1 2.013-1.244h3.859m-19.5.338V18a2.25 2.25 0 0 0 2.25 2.25h15A2.25 2.25 0 0 0 21.75 18v-4.162c0-.224-.034-.447-.1-.661L19.24 5.338a2.25 2.25 0 0 0-2.15-1.588H6.911a2.25 2.25 0 0 0-2.15 1.588L2.35 13.177a2.25 2.25 0 0 0-.1.661Z',
};

@Component({
  selector: 'kr-shell',
  host: { '(document:keydown.escape)': 'menuOpen.set(false)' },
  imports: [RouterOutlet, RouterLink, RouterLinkActive, SpeedPipe, NotificationBell, BackgroundTasksMenu],
  templateUrl: './shell.html',
  styleUrl: './shell.scss',
})
export class Shell implements OnInit, OnDestroy {
  protected readonly auth = inject(Auth);
  protected readonly events = inject(Events);
  private readonly router = inject(Router);
  protected readonly icons = ICONS;
  private readonly notifications = inject(Notifications);
  private readonly backgroundTasks = inject(BackgroundTasks);
  /** Requests waiting for approval (staff), for the sidebar badge. */
  protected readonly pendingRequests = computed(() => this.notifications.count('requests.pending'));
  protected readonly menuOpen = signal(false);

  private readonly allNav: NavItem[] = [
    { path: '/dashboard', label: 'Dashboard', icon: 'chart', access: 'staff' },
    { path: '/discover', label: 'Discover', icon: 'sparkles', section: 'Library' },
    { path: '/movies', label: 'Movies', icon: 'film' },
    { path: '/series', label: 'Series', icon: 'tv' },
    { path: '/calendar', label: 'Calendar', icon: 'calendar' },
    { path: '/requests', label: 'Requests', icon: 'inbox' },
    { path: '/subtitles', label: 'Subtitles', icon: 'cc', access: 'staff' },
    { path: '/search', label: 'Search', icon: 'search', section: 'Sources', access: 'staff' },
    { path: '/indexers', label: 'Indexers', icon: 'server', access: 'admin' },
    { path: '/downloads', label: 'Downloads', icon: 'download', access: 'staff' },
    { path: '/activity', label: 'Activity', icon: 'clock', section: 'System', access: 'staff' },
    { path: '/settings', label: 'Settings', icon: 'cog' },
  ];

  /** Navigation for the signed-in role; non-admins only reach their own account under /settings. */
  protected readonly nav = computed<NavItem[]>(() => {
    const items = this.allNav.filter(n => !n.access || (n.access === 'admin' ? this.auth.isAdmin() : this.auth.isStaff()));
    if (this.auth.isAdmin()) return items;
    return items.map(n => n.path === '/settings' ? { ...n, label: 'Account', section: this.auth.isStaff() ? n.section : 'You' } : n);
  });

  /** Primary destinations shown in the mobile tab bar; everything else lives behind "More". */
  protected readonly tabs = computed(() => {
    const paths = this.auth.isStaff() ? ['/dashboard', '/movies', '/series', '/downloads'] : ['/discover', '/movies', '/series', '/calendar'];
    return paths.map(p => this.nav().find(n => n.path === p)!);
  });

  private readonly url = toSignal(
    this.router.events.pipe(filter(e => e instanceof NavigationEnd), map(e => (e as NavigationEnd).urlAfterRedirects)),
    { initialValue: this.router.url });
  protected readonly current = computed(() => this.nav().find(n => this.url().startsWith(n.path)));
  protected readonly moreActive = computed(() => !this.tabs().some(t => this.url().startsWith(t.path)));

  protected readonly activeDownloads = computed(() =>
    this.events.downloads().filter(d => d.state === 'Downloading' || d.state === 'FetchingMetadata' || d.state === 'Queued').length);
  protected readonly totalDown = computed(() => this.events.downloads().reduce((s, d) => s + d.downloadRate, 0));
  protected readonly totalUp = computed(() => this.events.downloads().reduce((s, d) => s + d.uploadRate, 0));

  private readonly pushNavigation = inject(PushNavigation);
  private readonly destroyRef = inject(DestroyRef);

  ngOnInit() {
    this.events.start();
    this.notifications.start();
    this.backgroundTasks.start();
    this.pushNavigation.listen(this.destroyRef);
  }
  ngOnDestroy() {
    this.notifications.stop();
    this.backgroundTasks.stop();
    this.events.stop();
  }

  async logout() {
    await this.auth.logout();
    this.notifications.stop();
    this.backgroundTasks.stop();
    await this.events.stop();
    this.router.navigateByUrl('/login');
  }
}
