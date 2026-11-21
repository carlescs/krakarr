import { Component, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Api, errorMessage } from '../../core/api';
import { Auth } from '../../core/auth';
import { Library } from '../../core/library';
import { BulkResult, MediaType, QualityProfile } from '../../core/models';
import { Selection } from '../../core/selection';
import { Subscriptions } from '../../core/subscriptions';
import { Toasts } from '../../core/toast';
import { Modal } from '../modal/modal';
import { MoreMenu } from '../more-menu/more-menu';

/** Searches above this many titles ask first: each one is a separate indexer search. */
const CONFIRM_SEARCH_ABOVE = 25;

/**
 * Toolbar for a multi-selection of library titles: every user follows and unfollows; staff also change monitoring and
 * quality profile, search, refresh and delete.
 */
@Component({
  selector: 'kr-bulk-actions',
  imports: [FormsModule, Modal, MoreMenu],
  templateUrl: './bulk-actions.html',
  styleUrl: './bulk-actions.scss',
})
export class BulkActions {
  readonly mediaType = input.required<MediaType>();
  readonly selection = input.required<Selection>();
  /** Total titles that "Select all" would select. */
  readonly total = input(0);
  /** Shows a "Done" button that emits `closed` (grid selection mode). */
  readonly closable = input(false);
  readonly closed = output<void>();

  protected readonly auth = inject(Auth);
  private readonly api = inject(Api);
  private readonly library = inject(Library);
  private readonly subscriptions = inject(Subscriptions);
  private readonly toasts = inject(Toasts);

  protected readonly busy = signal(false);
  protected readonly confirmDelete = signal(false);
  protected deleteFiles = false;
  protected readonly profiles = signal<QualityProfile[] | null>(null);
  protected readonly editingProfile = signal(false);
  protected profileId: number | null = null;

  protected readonly count = computed(() => this.selection().count());

  protected noun(n: number) {
    return this.mediaType() === 'Movie' ? (n === 1 ? 'movie' : 'movies') : 'series';
  }

  protected follow(subscribed: boolean) {
    return this.run(async ids => {
      const changed = await this.subscriptions.setMany(this.mediaType(), ids, subscribed);
      this.toasts.success(subscribed ? `Following ${changed} more ${this.noun(changed)}` : `Unfollowed ${changed} ${this.noun(changed)}`);
    });
  }

  protected monitor(monitored: boolean) {
    return this.run(async ids => {
      const result = await this.api.bulkEdit(this.mediaType(), ids, { monitored });
      this.toasts.success(`${monitored ? 'Monitoring' : 'Stopped monitoring'} ${result.affected} ${this.noun(result.affected)}`);
      await this.reload();
    });
  }

  protected search() {
    const n = this.count();
    if (n > CONFIRM_SEARCH_ABOVE && !confirm(`Search indexers for ${n} ${this.noun(n)}? Each one is a separate search.`)) return;
    return this.run(async ids => {
      const command = await this.api.bulkSearch(this.mediaType(), ids);
      this.toasts.success(`Queued: ${command.name}`);
    });
  }

  protected refresh() {
    return this.run(async ids => {
      const command = await this.api.bulkRefresh(this.mediaType(), ids);
      this.toasts.success(`Queued: ${command.name}`);
    });
  }

  protected async openProfile() {
    this.editingProfile.set(true);
    if (this.profiles()) return;
    try {
      const profiles = await this.api.qualityProfiles();
      this.profiles.set(profiles);
      this.profileId ??= profiles[0]?.id ?? null;
    } catch (e) {
      this.toasts.error(errorMessage(e));
      this.editingProfile.set(false);
    }
  }

  protected saveProfile() {
    const qualityProfileId = this.profileId;
    if (qualityProfileId == null) return;
    return this.run(async ids => {
      const result = await this.api.bulkEdit(this.mediaType(), ids, { qualityProfileId });
      this.editingProfile.set(false);
      this.toasts.success(`Changed the quality profile of ${result.affected} ${this.noun(result.affected)}`);
      await this.reload();
    });
  }

  protected delete() {
    return this.run(async ids => {
      const result = await this.api.bulkDelete(this.mediaType(), ids, this.deleteFiles);
      this.confirmDelete.set(false);
      this.deleteFiles = false;
      this.report(`Removed ${result.affected} ${this.noun(result.affected)}`, result);
      await this.reload();
    });
  }

  private report(message: string, result: BulkResult) {
    if (result.errors.length === 0) this.toasts.success(message);
    else this.toasts.error(`${message}; ${result.errors.length} failed: ${result.errors.slice(0, 3).join(' · ')}`);
  }

  private reload() {
    if (this.mediaType() === 'Movie') {
      return Promise.all([this.library.movies.reload(), this.library.movieCollections.reload()]);
    }
    return this.library.series.reload();
  }

  private async run(action: (ids: number[]) => Promise<void>) {
    const ids = this.selection().selected();
    if (ids.length === 0 || this.busy()) return;
    this.busy.set(true);
    try {
      await action(ids);
    } catch (e) {
      this.toasts.error(errorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }
}
