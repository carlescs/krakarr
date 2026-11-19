import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api, errorMessage } from '../../core/api';
import { BytesPipe } from '../../core/format';
import {
  AutomationSettings, DirectoryListing, DownloadClientSettings, GeneralSettings, MediaManagementSettings, MetadataSettings, Quality,
  QualityProfile, RootFolder, SystemStatus,
} from '../../core/models';
import { Toasts } from '../../core/toast';
import { Modal } from '../../shared/modal';

type Tab = 'media' | 'profiles' | 'downloads' | 'metadata' | 'general';

@Component({
  selector: 'kr-settings',
  imports: [FormsModule, Modal, BytesPipe],
  template: `
    <h1 class="page-title mb-6">Settings</h1>
    <div class="mb-6 flex gap-1 overflow-x-auto border-b border-surface-700">
      @for (t of tabs; track t.id) {
        <button class="-mb-px whitespace-nowrap border-b-2 px-4 py-2 text-sm font-medium transition"
                [class]="tab() === t.id ? 'border-accent-400 text-white' : 'border-transparent text-slate-400 hover:text-slate-200'"
                (click)="tab.set(t.id)">{{ t.label }}</button>
      }
    </div>

    <!-- Media management -->
    @if (tab() === 'media') {
      <div class="space-y-6">
        <section class="card p-5">
          <div class="mb-4 flex items-center">
            <h2 class="flex-1 font-semibold text-white">Root folders</h2>
            <button class="btn-secondary btn-sm" (click)="openBrowser()">+ Add root folder</button>
          </div>
          @if (rootFolders().length === 0) {
            <p class="text-sm text-amber-300">Add at least one root folder where movies will be stored (e.g. /media/movies).</p>
          }
          <ul class="divide-y divide-surface-800">
            @for (r of rootFolders(); track r.id) {
              <li class="flex items-center gap-3 py-2 text-sm">
                <span class="flex-1 font-mono">{{ r.path }}</span>
                @if (!r.accessible) { <span class="badge bg-rose-500/15 text-rose-300">Not accessible</span> }
                @if (r.freeSpace != null) { <span class="text-slate-400">{{ r.freeSpace | bytes }} free</span> }
                <button class="btn-ghost btn-sm text-rose-300" (click)="deleteRoot(r)">Remove</button>
              </li>
            }
          </ul>
        </section>
        @if (media(); as m) {
          <section class="card space-y-4 p-5">
            <h2 class="font-semibold text-white">File management</h2>
            <label class="flex items-center gap-2 text-sm"><input type="checkbox" class="accent-accent-500" [(ngModel)]="m.renameFiles" /> Rename movie files</label>
            <div><label class="label">Movie folder format</label><input class="input font-mono" [(ngModel)]="m.movieFolderFormat" /></div>
            <div>
              <label class="label">Movie file format</label><input class="input font-mono" [(ngModel)]="m.movieFileFormat" />
              <p class="hint">Tokens: {{ namingTokens }}</p>
            </div>
            <label class="flex items-center gap-2 text-sm"><input type="checkbox" class="accent-accent-500" [(ngModel)]="m.useHardlinks" /> Use hardlinks instead of copies (keeps seeding without doubling disk usage)</label>
            <div class="max-w-xs"><label class="label">Minimum video size (MB)</label><input class="input" type="number" min="0" [(ngModel)]="m.minimumVideoFileSizeMb" /></div>
            <div class="flex justify-end"><button class="btn-primary" (click)="save('media', m)">Save</button></div>
          </section>
        }
      </div>
    }

    <!-- Quality profiles -->
    @if (tab() === 'profiles') {
      <div class="mb-4 flex justify-end"><button class="btn-secondary btn-sm" (click)="editProfile(null)">+ Add profile</button></div>
      <div class="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
        @for (p of profiles(); track p.id) {
          <button class="card flex flex-col items-start justify-start p-5 text-left transition hover:border-accent-500/50" (click)="editProfile(p)">
            <div class="font-semibold text-white">{{ p.name }}</div>
            <div class="mt-2 flex flex-wrap gap-1">
              @for (q of qualitiesFor(p); track q.id) {
                <span class="badge" [class]="q.id === p.cutoffQualityId ? 'bg-accent-500/20 text-accent-300' : 'bg-surface-700 text-slate-300'">{{ q.name }}</span>
              }
            </div>
          </button>
        }
      </div>
    }

    <!-- Download client -->
    @if (tab() === 'downloads') {
      @if (downloads(); as d) {
        <section class="card grid gap-4 p-5 sm:grid-cols-2">
          <h2 class="font-semibold text-white sm:col-span-2">Built-in torrent client</h2>
          <div class="sm:col-span-2"><label class="label">Download directory</label><input class="input font-mono" [(ngModel)]="d.downloadDirectory" [placeholder]="status()?.downloadDirectory ?? ''" />
            <p class="hint">For hardlinks to work, downloads and your library must be on the same filesystem.</p></div>
          <div><label class="label">Listening port</label><input class="input" type="number" [(ngModel)]="d.listenPort" /></div>
          <div><label class="label">Max connections</label><input class="input" type="number" [(ngModel)]="d.maxConnections" /></div>
          <div><label class="label">Download limit (KiB/s, 0 = ∞)</label><input class="input" type="number" min="0" [(ngModel)]="d.maxDownloadRateKiB" /></div>
          <div><label class="label">Upload limit (KiB/s, 0 = ∞)</label><input class="input" type="number" min="0" [(ngModel)]="d.maxUploadRateKiB" /></div>
          <div><label class="label">Seed ratio limit (0 = none)</label><input class="input" type="number" step="0.1" min="0" [(ngModel)]="d.seedRatioLimit" /></div>
          <div><label class="label">Seed time limit (minutes, 0 = none)</label><input class="input" type="number" min="0" [(ngModel)]="d.seedTimeLimitMinutes" /></div>
          <label class="flex items-center gap-2 text-sm"><input type="checkbox" class="accent-accent-500" [(ngModel)]="d.enableDht" /> Enable DHT</label>
          <label class="flex items-center gap-2 text-sm"><input type="checkbox" class="accent-accent-500" [(ngModel)]="d.enablePortForwarding" /> UPnP / NAT-PMP port forwarding</label>
          <div class="flex justify-end sm:col-span-2"><button class="btn-primary" (click)="save('downloads', d)">Save</button></div>
        </section>
      }
      @if (automation(); as a) {
        <section class="card mt-6 grid gap-4 p-5 sm:grid-cols-2">
          <h2 class="font-semibold text-white sm:col-span-2">Automation</h2>
          <div><label class="label">RSS sync interval (minutes, 0 = off)</label><input class="input" type="number" min="0" [(ngModel)]="a.rssSyncIntervalMinutes" /></div>
          <div class="flex justify-end sm:col-span-2"><button class="btn-primary" (click)="save('automation', a)">Save</button></div>
        </section>
      }
    }

    <!-- Metadata -->
    @if (tab() === 'metadata') {
      @if (metadata(); as m) {
        <section class="card space-y-4 p-5">
          <div>
            <label class="label">TMDB API key or read access token</label>
            <input class="input font-mono" type="password" autocomplete="off" [(ngModel)]="m.tmdbApiKey" />
            <p class="hint">Free at themoviedb.org → Settings → API. Used for movie metadata.</p>
          </div>
          <div>
            <label class="label">TheTVDB API key</label>
            <input class="input font-mono" type="password" autocomplete="off" [(ngModel)]="m.tvdbApiKey" />
          </div>
          <div class="max-w-xs"><label class="label">TheTVDB subscriber PIN (optional)</label><input class="input" [(ngModel)]="m.tvdbPin" /></div>
          <div class="max-w-xs"><label class="label">Language</label><input class="input" [(ngModel)]="m.language" placeholder="en-US" /></div>
          <div class="flex justify-end"><button class="btn-primary" (click)="save('metadata', m)">Save</button></div>
        </section>
      }
    }

    <!-- General -->
    @if (tab() === 'general') {
      <div class="space-y-6">
        @if (general(); as g) {
          <section class="card space-y-4 p-5">
            <h2 class="font-semibold text-white">Security</h2>
            <div>
              <label class="label">API key</label>
              <div class="flex gap-2">
                <input class="input font-mono" readonly [value]="g.apiKey" />
                <button class="btn-secondary" (click)="copy(g.apiKey)">Copy</button>
                <button class="btn-secondary" (click)="regenerate()">Regenerate</button>
              </div>
              <p class="hint">Send as <code>X-Api-Key</code> header for automation.</p>
            </div>
            <div class="grid gap-4 sm:grid-cols-2">
              <div><label class="label">Username</label><input class="input" [(ngModel)]="newUsername" autocomplete="username" /></div>
              <div><label class="label">New password</label><input class="input" type="password" [(ngModel)]="newPassword" autocomplete="new-password" /></div>
            </div>
            <div class="flex justify-end"><button class="btn-secondary" (click)="changeCredentials()" [disabled]="!newPassword">Update credentials</button></div>
          </section>
        }
        @if (status(); as s) {
          <section class="card p-5">
            <h2 class="mb-3 font-semibold text-white">About</h2>
            <dl class="grid gap-x-8 gap-y-2 text-sm sm:grid-cols-[auto_1fr]">
              <dt class="text-slate-500">Version</dt><dd>{{ s.version }}</dd>
              <dt class="text-slate-500">Runtime</dt><dd>{{ s.runtime }}</dd>
              <dt class="text-slate-500">OS</dt><dd>{{ s.os }} {{ s.isDocker ? '(Docker)' : '' }}</dd>
              <dt class="text-slate-500">Database</dt><dd>{{ s.databaseProvider }}</dd>
              <dt class="text-slate-500">Config</dt><dd class="font-mono">{{ s.configDirectory }}</dd>
              <dt class="text-slate-500">Downloads</dt><dd class="font-mono">{{ s.downloadDirectory }}</dd>
            </dl>
          </section>
        }
      </div>
    }

    <!-- Profile editor -->
    @if (profileForm(); as p) {
      <kr-modal [title]="p.id ? 'Edit profile' : 'New profile'" width="max-w-2xl" (closed)="profileForm.set(null)">
        <div class="space-y-4">
          <div><label class="label">Name</label><input class="input" [(ngModel)]="p.name" /></div>
          <div>
            <label class="label">Allowed qualities</label>
            <div class="grid max-h-64 grid-cols-2 gap-1 overflow-y-auto rounded-lg border border-surface-700 p-2 sm:grid-cols-3">
              @for (q of selectableQualities(); track q.id) {
                <label class="flex items-center gap-2 rounded px-2 py-1 text-sm hover:bg-surface-800">
                  <input type="checkbox" class="accent-accent-500" [checked]="p.allowedQualityIds.includes(q.id)" (change)="toggleQuality(p, q.id)" /> {{ q.name }}
                </label>
              }
            </div>
          </div>
          <div class="grid gap-4 sm:grid-cols-3">
            <div>
              <label class="label">Upgrade until</label>
              <select class="input" [(ngModel)]="p.cutoffQualityId">
                @for (q of qualitiesFor(p); track q.id) { <option [ngValue]="q.id">{{ q.name }}</option> }
              </select>
            </div>
            <div><label class="label">Min size (MB)</label><input class="input" type="number" min="0" [(ngModel)]="p.minSizeMb" /></div>
            <div><label class="label">Max size (MB)</label><input class="input" type="number" min="0" [(ngModel)]="p.maxSizeMb" /></div>
          </div>
          <label class="flex items-center gap-2 text-sm"><input type="checkbox" class="accent-accent-500" [(ngModel)]="p.upgradeAllowed" /> Upgrades allowed</label>
        </div>
        <ng-container footer>
          @if (p.id) { <button class="btn-danger mr-auto" (click)="deleteProfile(p)">Delete</button> }
          <button class="btn-ghost" (click)="profileForm.set(null)">Cancel</button>
          <button class="btn-primary" (click)="saveProfile(p)">Save</button>
        </ng-container>
      </kr-modal>
    }

    <!-- Folder browser -->
    @if (browser(); as b) {
      <kr-modal title="Add root folder" width="max-w-xl" (closed)="browser.set(null)">
        <input class="input mb-3 font-mono" [(ngModel)]="browsePath" (keyup.enter)="browse(browsePath)" />
        <div class="max-h-72 overflow-y-auto rounded-lg border border-surface-700">
          @if (b.parent) {
            <button class="block w-full px-3 py-1.5 text-left font-mono text-sm text-slate-400 hover:bg-surface-800" (click)="browse(b.parent)">..</button>
          }
          @for (d of b.directories; track d.path) {
            <button class="block w-full px-3 py-1.5 text-left font-mono text-sm hover:bg-surface-800" (click)="browse(d.path)">📁 {{ d.name }}</button>
          }
        </div>
        <ng-container footer>
          <button class="btn-ghost" (click)="browser.set(null)">Cancel</button>
          <button class="btn-primary" (click)="addRoot()">Use {{ browsePath }}</button>
        </ng-container>
      </kr-modal>
    }
  `,
})
export class SettingsPage implements OnInit {
  private readonly api = inject(Api);
  private readonly toasts = inject(Toasts);

  protected readonly tabs: { id: Tab; label: string }[] = [
    { id: 'media', label: 'Media management' },
    { id: 'profiles', label: 'Quality profiles' },
    { id: 'downloads', label: 'Download client' },
    { id: 'metadata', label: 'Metadata' },
    { id: 'general', label: 'General' },
  ];
  protected readonly tab = signal<Tab>('media');

  protected readonly rootFolders = signal<RootFolder[]>([]);
  protected readonly profiles = signal<QualityProfile[]>([]);
  protected readonly qualities = signal<Quality[]>([]);
  protected readonly media = signal<MediaManagementSettings | null>(null);
  protected readonly downloads = signal<DownloadClientSettings | null>(null);
  protected readonly automation = signal<AutomationSettings | null>(null);
  protected readonly metadata = signal<MetadataSettings | null>(null);
  protected readonly general = signal<GeneralSettings | null>(null);
  protected readonly status = signal<SystemStatus | null>(null);
  protected readonly profileForm = signal<QualityProfile | null>(null);
  protected readonly browser = signal<DirectoryListing | null>(null);
  protected readonly selectableQualities = computed(() => this.qualities().filter(q => q.id !== 0));
  protected readonly namingTokens = ['Movie Title', 'Movie CleanTitle', 'Original Title', 'Release Year', 'Quality', 'Release Group', 'TmdbId', 'ImdbId']
    .map(t => `{${t}}`).join(', ');
  protected browsePath = '/';
  protected newUsername = '';
  protected newPassword = '';

  async ngOnInit() {
    const [roots, profiles, qualities, media, downloads, automation, metadata, general, status] = await Promise.all([
      this.api.rootFolders(), this.api.qualityProfiles(), this.api.qualities(), this.api.settings('media'),
      this.api.settings('downloads'), this.api.settings('automation'), this.api.settings('metadata'),
      this.api.settings('general'), this.api.systemStatus(),
    ]);
    this.rootFolders.set(roots);
    this.profiles.set(profiles);
    this.qualities.set(qualities);
    this.media.set(media);
    this.downloads.set(downloads);
    this.automation.set(automation);
    this.metadata.set(metadata);
    this.general.set(general);
    this.status.set(status);
  }

  protected qualitiesFor(p: QualityProfile) {
    return this.qualities().filter(q => p.allowedQualityIds.includes(q.id)).sort((a, b) => b.weight - a.weight);
  }

  async save<K extends 'media' | 'downloads' | 'automation' | 'metadata'>(section: K, value: Parameters<Api['saveSettings']>[1] & object) {
    try {
      await this.api.saveSettings(section, value as never);
      this.toasts.success('Settings saved');
    } catch (e) {
      this.toasts.error(errorMessage(e));
    }
  }

  // Root folders
  async openBrowser() {
    this.browsePath = this.rootFolders()[0]?.path ?? '/';
    await this.browse(this.browsePath);
  }

  async browse(path: string) {
    try {
      const listing = await this.api.browse(path);
      this.browsePath = listing.path;
      this.browser.set(listing);
    } catch {
      this.browser.set({ path, directories: [] });
    }
  }

  async addRoot() {
    try {
      await this.api.addRootFolder(this.browsePath);
      this.browser.set(null);
      this.rootFolders.set(await this.api.rootFolders());
    } catch (e) {
      this.toasts.error(errorMessage(e));
    }
  }

  async deleteRoot(r: RootFolder) {
    await this.api.deleteRootFolder(r.id);
    this.rootFolders.set(await this.api.rootFolders());
  }

  // Profiles
  editProfile(p: QualityProfile | null) {
    this.profileForm.set(p ? structuredClone(p) : {
      id: 0, name: '', allowedQualityIds: [], cutoffQualityId: 0, upgradeAllowed: true, minSizeMb: 0, maxSizeMb: 0,
    });
  }

  toggleQuality(p: QualityProfile, id: number) {
    p.allowedQualityIds = p.allowedQualityIds.includes(id) ? p.allowedQualityIds.filter(x => x !== id) : [...p.allowedQualityIds, id];
    if (!p.allowedQualityIds.includes(p.cutoffQualityId)) p.cutoffQualityId = this.qualitiesFor(p)[0]?.id ?? 0;
    this.profileForm.set({ ...p });
  }

  async saveProfile(p: QualityProfile) {
    try {
      await this.api.saveQualityProfile(p);
      this.profileForm.set(null);
      this.profiles.set(await this.api.qualityProfiles());
      this.toasts.success('Profile saved');
    } catch (e) {
      this.toasts.error(errorMessage(e));
    }
  }

  async deleteProfile(p: QualityProfile) {
    try {
      await this.api.deleteQualityProfile(p.id);
      this.profileForm.set(null);
      this.profiles.set(await this.api.qualityProfiles());
    } catch (e) {
      this.toasts.error(errorMessage(e));
    }
  }

  // General
  async regenerate() {
    if (!confirm('Regenerate the API key? Existing integrations will stop working.')) return;
    this.general.set(await this.api.regenerateApiKey());
  }

  copy(text: string) {
    navigator.clipboard?.writeText(text).then(() => this.toasts.success('Copied'));
  }

  async changeCredentials() {
    try {
      await this.api.changeCredentials(this.newUsername, this.newPassword);
      this.newPassword = '';
      this.toasts.success('Credentials updated');
    } catch (e) {
      this.toasts.error(errorMessage(e));
    }
  }
}
