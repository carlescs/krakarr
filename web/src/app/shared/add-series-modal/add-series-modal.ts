import { Component, OnInit, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Api, errorMessage } from '../../core/api';
import { MediaRequest, MonitorMode, QualityProfile, RootFolder, Series, SeriesLookup } from '../../core/models';
import { Toasts } from '../../core/toast';
import { Modal } from '../modal/modal';

/** Root folder / monitoring / profile options for adding a show to the library, directly or by approving a request. */
@Component({
  selector: 'kr-add-series-modal',
  imports: [FormsModule, RouterLink, Modal],
  templateUrl: './add-series-modal.html',
  styleUrl: './add-series-modal.scss',
})
export class AddSeriesModal implements OnInit {
  private readonly api = inject(Api);
  private readonly toasts = inject(Toasts);

  readonly show = input.required<Pick<SeriesLookup, 'tmdbId' | 'tvdbId' | 'title'>>();
  /** When set, approves this request with the chosen options instead of adding the show directly. */
  readonly request = input<MediaRequest | null>(null);
  readonly added = output<Series>();
  readonly approved = output<MediaRequest>();
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
      const request = this.request();
      if (request) {
        const approved = await this.api.approveRequest(request.id, {
          qualityProfileId: this.profileId, rootFolderPath: this.rootFolder, monitor: this.monitor, seasonFolder: this.seasonFolder,
          searchNow: this.searchNow,
        });
        this.toasts.success(`Approved ${approved.title}`);
        this.approved.emit(approved);
        return;
      }
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
