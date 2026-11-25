import { NgTemplateOutlet } from '@angular/common';
import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api, errorMessage } from '../../../core/api';
import { EmbySettings, JellyfinSettings, MediaServerTestResult, PathMapping, PlexSettings } from '../../../core/models';
import { Toasts } from '../../../core/toast';

type Server = 'jellyfin' | 'emby' | 'plex';

/**
 * Admin-only media servers. Jellyfin adds an "Open in Jellyfin" button to titles; Jellyfin, Emby and Plex are asked to
 * rescan a folder right after Krakarr imports, renames or deletes files in it. Each server can map Krakarr's library
 * paths to the paths it sees.
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

  protected readonly jellyfin = signal<JellyfinSettings | null>(null);
  protected readonly emby = signal<EmbySettings | null>(null);
  protected readonly plex = signal<PlexSettings | null>(null);
  protected readonly results = signal<Partial<Record<Server, MediaServerTestResult>>>({});
  protected readonly testing = signal<Server | null>(null);
  protected readonly saving = signal(false);

  async ngOnInit() {
    try {
      const [jellyfin, emby, plex] = await Promise.all([this.api.settings('jellyfin'), this.api.settings('emby'), this.api.settings('plex')]);
      this.jellyfin.set({ ...jellyfin, refreshLibrary: jellyfin.refreshLibrary ?? true, pathMappings: jellyfin.pathMappings ?? [] });
      this.emby.set({ ...emby, pathMappings: emby.pathMappings ?? [] });
      this.plex.set({ ...plex, pathMappings: plex.pathMappings ?? [] });
    } catch (e) {
      this.toasts.error(errorMessage(e));
    }
  }

  protected resultFor(server: Server) { return this.results()[server]; }

  protected addMapping(list: PathMapping[]) { list.push({ from: '', to: '' }); }

  protected removeMapping(list: PathMapping[], index: number) { list.splice(index, 1); }

  async save() {
    const [jellyfin, emby, plex] = [this.jellyfin(), this.emby(), this.plex()];
    if (!jellyfin || !emby || !plex || this.saving()) return;
    this.saving.set(true);
    try {
      const [j, e, p] = await Promise.all([
        this.api.saveSettings('jellyfin', jellyfin), this.api.saveSettings('emby', emby), this.api.saveSettings('plex', plex),
      ]);
      this.jellyfin.set({ ...j, pathMappings: j.pathMappings ?? [] });
      this.emby.set({ ...e, pathMappings: e.pathMappings ?? [] });
      this.plex.set({ ...p, pathMappings: p.pathMappings ?? [] });
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
}
