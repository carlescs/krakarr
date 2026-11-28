import { NgTemplateOutlet } from '@angular/common';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api, errorMessage } from '../../../core/api';
import { EmbySettings, JellyfinSettings, MediaServerTestResult, MediaServerUser, PathMapping, PlexSettings } from '../../../core/models';
import { Toasts } from '../../../core/toast';

type Server = 'jellyfin' | 'emby' | 'plex';
type UserServer = 'jellyfin' | 'emby';

const SERVERS: readonly Server[] = ['jellyfin', 'emby', 'plex'];
const NAMES: Record<Server, string> = { jellyfin: 'Jellyfin', emby: 'Emby', plex: 'Plex' };

const blankJellyfin = (): JellyfinSettings => ({ url: null, apiKey: null, publicUrl: null, refreshLibrary: true, pathMappings: [], playStatusUserId: null });
const blankEmby = (): EmbySettings => ({ url: null, apiKey: null, pathMappings: [], playStatusUserId: null });
const blankPlex = (): PlexSettings => ({ url: null, token: null, pathMappings: [], playStatus: false });

/**
 * Admin-only media servers. Only the servers in use show a panel: the picker at the top adds one, the ✕ on a panel
 * removes it (clearing its settings). Jellyfin adds an "Open in Jellyfin" button to titles; Jellyfin, Emby and Plex are
 * asked to rescan a folder right after Krakarr imports, renames or deletes files in it, and can report watched state.
 * Each server can map Krakarr's library paths to the paths it sees.
 */
@Component({
  selector: 'kr-media-server-settings',
  imports: [FormsModule, NgTemplateOutlet],
  templateUrl: './media-servers.html',
  styleUrl: './media-servers.scss',
})
export class MediaServerSettings implements OnInit {
  private readonly api = inject(Api);
  private readonly toasts = inject(Toasts);

  protected readonly names = NAMES;
  protected readonly jellyfin = signal<JellyfinSettings | null>(null);
  protected readonly emby = signal<EmbySettings | null>(null);
  protected readonly plex = signal<PlexSettings | null>(null);
  /** Servers with a panel, in the fixed order of {@link SERVERS}. */
  protected readonly enabled = signal<Server[]>([]);
  /** Servers whose settings are stored (with a URL), so removing them has to clear them on the server too. */
  private readonly stored = signal<ReadonlySet<Server>>(new Set());
  protected readonly enabledNames = computed(() => this.enabled().map(s => NAMES[s]).join(', '));
  protected readonly available = computed(() => SERVERS.filter(s => !this.enabled().includes(s)));
  protected readonly loaded = computed(() => !!this.jellyfin() && !!this.emby() && !!this.plex());
  protected readonly results = signal<Partial<Record<Server, MediaServerTestResult>>>({});
  protected readonly testing = signal<Server | null>(null);
  protected readonly saving = signal(false);
  protected readonly removing = signal<Server | null>(null);
  /** Users of Jellyfin and Emby, to pick whose play status to show (absent until loaded). */
  protected readonly users = signal<Partial<Record<UserServer, MediaServerUser[]>>>({});
  protected readonly usersError = signal<Partial<Record<UserServer, string>>>({});
  protected readonly loadingUsers = signal<UserServer | null>(null);

  async ngOnInit() {
    try {
      const [jellyfin, emby, plex] = await Promise.all([this.api.settings('jellyfin'), this.api.settings('emby'), this.api.settings('plex')]);
      this.apply(jellyfin, emby, plex);
      const stored = SERVERS.filter(s => this.isSet(s));
      this.stored.set(new Set(stored));
      this.enabled.set(stored);
      // Fill the user pickers of the servers already set up; failures just leave them to the "Load users" button.
      for (const s of ['jellyfin', 'emby'] as const) if (stored.includes(s)) this.loadUsers(s, true);
    } catch (e) {
      this.toasts.error(errorMessage(e));
    }
  }

  protected resultFor(server: Server) { return this.results()[server]; }

  protected nameOf(server: Server) { return NAMES[server]; }

  protected userError(server: UserServer) { return this.usersError()[server]; }

  protected addMapping(list: PathMapping[]) { list.push({ from: '', to: '' }); }

  protected removeMapping(list: PathMapping[], index: number) { list.splice(index, 1); }

  /** Adds the panel of the server picked in the dropdown. */
  protected add(event: Event) {
    const select = event.target as HTMLSelectElement;
    const server = select.value as Server;
    select.value = '';
    if (!SERVERS.includes(server) || this.enabled().includes(server)) return;
    this.enabled.update(list => SERVERS.filter(s => s === server || list.includes(s)));
  }

  /** Removes a server after confirmation, clearing its stored settings so Krakarr stops talking to it. */
  async remove(server: Server) {
    if (this.removing() || !confirm(`Remove ${NAMES[server]}? Its settings are cleared and Krakarr stops talking to it.`)) return;
    if (this.stored().has(server)) {
      this.removing.set(server);
      try {
        if (server === 'jellyfin') this.jellyfin.set(this.normalize(await this.api.saveSettings('jellyfin', blankJellyfin())));
        else if (server === 'emby') this.emby.set(this.normalize(await this.api.saveSettings('emby', blankEmby())));
        else this.plex.set(this.normalize(await this.api.saveSettings('plex', blankPlex())));
        this.stored.update(s => new Set([...s].filter(x => x !== server)));
        this.toasts.success(`${NAMES[server]} removed`);
      } catch (e) {
        this.toasts.error(errorMessage(e));
        return;
      } finally {
        this.removing.set(null);
      }
    } else {
      if (server === 'jellyfin') this.jellyfin.set(blankJellyfin());
      else if (server === 'emby') this.emby.set(blankEmby());
      else this.plex.set(blankPlex());
    }
    this.enabled.update(list => list.filter(s => s !== server));
    this.results.update(r => ({ ...r, [server]: undefined }));
    if (server !== 'plex') this.users.update(u => ({ ...u, [server]: undefined }));
  }

  /** Saves the servers that have a panel (removed ones were cleared when they were removed). */
  async save() {
    const [jellyfin, emby, plex] = [this.jellyfin(), this.emby(), this.plex()];
    if (!jellyfin || !emby || !plex || this.saving()) return;
    const enabled = this.enabled();
    this.saving.set(true);
    try {
      const [j, e, p] = await Promise.all([
        enabled.includes('jellyfin') ? this.api.saveSettings('jellyfin', jellyfin) : jellyfin,
        enabled.includes('emby') ? this.api.saveSettings('emby', emby) : emby,
        enabled.includes('plex') ? this.api.saveSettings('plex', plex) : plex,
      ]);
      this.apply(j, e, p);
      this.stored.set(new Set(SERVERS.filter(s => this.isSet(s))));
      this.toasts.success('Settings saved');
    } catch (e) {
      this.toasts.error(errorMessage(e));
    } finally {
      this.saving.set(false);
    }
  }

  async test(server: Server) {
    this.testing.set(server);
    let result: MediaServerTestResult;
    try {
      result = server === 'jellyfin' ? await this.api.testJellyfin(this.jellyfin()!)
        : server === 'emby' ? await this.api.testEmby(this.emby()!)
        : await this.api.testPlex(this.plex()!);
    } catch (e) {
      result = { success: false, message: errorMessage(e) };
    } finally {
      this.testing.set(null);
    }
    this.results.update(r => ({ ...r, [server]: result }));
  }

  /** Lists the server's users for the play status picker; `quiet` keeps a failure out of sight (page load). */
  async loadUsers(server: UserServer, quiet = false) {
    const settings = server === 'jellyfin' ? this.jellyfin() : this.emby();
    if (!settings?.url || !settings.apiKey) {
      if (!quiet) this.usersError.update(e => ({ ...e, [server]: `Enter the ${NAMES[server]} URL and an API key first` }));
      return;
    }
    this.loadingUsers.set(server);
    try {
      const users = server === 'jellyfin' ? await this.api.jellyfinUsers(settings) : await this.api.embyUsers(settings);
      this.users.update(u => ({ ...u, [server]: users }));
      this.usersError.update(e => ({ ...e, [server]: undefined }));
    } catch (e) {
      if (!quiet) this.usersError.update(errors => ({ ...errors, [server]: errorMessage(e) }));
    } finally {
      this.loadingUsers.set(null);
    }
  }

  /** Users to offer, keeping the chosen one even before the list is loaded (shown by its id). */
  protected userOptions(server: UserServer, selected: string | null | undefined): MediaServerUser[] {
    const users = this.users()[server] ?? [];
    return selected && !users.some(u => u.id === selected) ? [{ id: selected, name: null }, ...users] : users;
  }

  private apply(jellyfin: JellyfinSettings, emby: EmbySettings, plex: PlexSettings) {
    this.jellyfin.set({ ...this.normalize(jellyfin), refreshLibrary: jellyfin.refreshLibrary ?? true });
    this.emby.set(this.normalize(emby));
    this.plex.set({ ...this.normalize(plex), playStatus: plex.playStatus ?? false });
  }

  private normalize<T extends { pathMappings?: PathMapping[] }>(s: T): T { return { ...s, pathMappings: s.pathMappings ?? [] }; }

  private isSet(server: Server) {
    const s = server === 'jellyfin' ? this.jellyfin() : server === 'emby' ? this.emby() : this.plex();
    return !!s?.url?.trim();
  }
}
