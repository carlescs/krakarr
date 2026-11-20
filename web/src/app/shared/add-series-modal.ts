import { Component, OnInit, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Api, errorMessage } from '../core/api';
import { MonitorMode, QualityProfile, RootFolder, Series, SeriesLookup } from '../core/models';
import { Toasts } from '../core/toast';
import { Modal } from './modal';

/** Root folder / monitoring / profile options for adding a show to the library. */
@Component({
  selector: 'kr-add-series-modal',
  imports: [FormsModule, RouterLink, Modal],
  template: `
    <kr-modal [title]="'Add ' + show().title" (closed)="closed.emit()">
      @if (rootFolders().length === 0) {
        <p class="text-sm text-amber-300">No TV root folders configured. <a routerLink="/settings" class="underline">Add one in Settings</a> (type “Series”) first.</p>
      } @else {
        <div class="space-y-4">
          <div>
            <label class="label">Root folder</label>
            <select class="input" [(ngModel)]="rootFolder">
              @for (f of rootFolders(); track f.id) { <option [value]="f.path">{{ f.path }}</option> }
            </select>
          </div>
          <div>
            <label class="label">Monitor</label>
            <select class="input" [(ngModel)]="monitor">
              @for (m of monitorModes; track m.id) { <option [value]="m.id">{{ m.label }}</option> }
            </select>
          </div>
          <div>
            <label class="label">Quality profile</label>
            <select class="input" [(ngModel)]="profileId">
              @for (p of profiles(); track p.id) { <option [ngValue]="p.id">{{ p.name }}</option> }
            </select>
          </div>
          <label class="flex items-center gap-2 text-sm"><input type="checkbox" [(ngModel)]="seasonFolder" class="accent-accent-500" /> Use season folders</label>
          <label class="flex items-center gap-2 text-sm"><input type="checkbox" [(ngModel)]="searchNow" class="accent-accent-500" /> Start search for missing episodes</label>
        </div>
      }
      <ng-container footer>
        <button class="btn-ghost" (click)="closed.emit()">Cancel</button>
        <button class="btn-primary" [disabled]="adding() || rootFolders().length === 0" (click)="add()">{{ adding() ? 'Adding…' : 'Add series' }}</button>
      </ng-container>
    </kr-modal>
  `,
})
export class AddSeriesModal implements OnInit {
  private readonly api = inject(Api);
  private readonly toasts = inject(Toasts);

  readonly show = input.required<SeriesLookup>();
  readonly added = output<Series>();
  readonly closed = output<void>();

  protected readonly monitorModes: { id: MonitorMode; label: string }[] = [
    { id: 'All', label: 'All episodes' },
    { id: 'Future', label: 'Future episodes' },
    { id: 'Missing', label: 'Missing episodes' },
    { id: 'FirstSeason', label: 'First season' },
    { id: 'LatestSeason', label: 'Latest season' },
    { id: 'None', label: 'None' },
  ];
  protected readonly adding = signal(false);
  protected readonly rootFolders = signal<RootFolder[]>([]);
  protected readonly profiles = signal<QualityProfile[]>([]);
  protected rootFolder = '';
  protected profileId = 0;
  protected monitor: MonitorMode = 'All';
  protected seasonFolder = true;
  protected searchNow = true;

  async ngOnInit() {
    const [folders, profiles] = await Promise.all([this.api.rootFolders(), this.api.qualityProfiles()]);
    this.rootFolders.set(folders.filter(f => f.mediaType === 'Series'));
    this.profiles.set(profiles);
    this.rootFolder = this.rootFolders()[0]?.path ?? '';
    this.profileId = profiles[0]?.id ?? 0;
  }

  async add() {
    this.adding.set(true);
    try {
      const show = this.show();
      const created = await this.api.addSeries({
        tmdbId: show.tmdbId, tvdbId: show.tvdbId, qualityProfileId: this.profileId, rootFolderPath: this.rootFolder, monitor: this.monitor,
        seasonFolder: this.seasonFolder, searchForMissing: this.searchNow,
      });
      this.toasts.success(`Added ${created.title}`);
      this.added.emit(created);
    } catch (e) {
      this.toasts.error(errorMessage(e));
    } finally {
      this.adding.set(false);
    }
  }
}
