import { Component, OnInit, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Api, errorMessage } from '../../core/api';
import { AVAILABILITY_OPTIONS } from '../../core/calendar';
import { MediaRequest, MinimumAvailability, Movie, MovieLookup, QualityProfile, RootFolder } from '../../core/models';
import { Toasts } from '../../core/toast';
import { Modal } from '../modal/modal';

/** Root folder / profile / monitoring options for adding a TMDB movie to the library, directly or by approving a request. */
@Component({
  selector: 'kr-add-movie-modal',
  imports: [FormsModule, RouterLink, Modal],
  templateUrl: './add-movie-modal.html',
  styleUrl: './add-movie-modal.scss',
})
export class AddMovieModal implements OnInit {
  private readonly api = inject(Api);
  private readonly toasts = inject(Toasts);

  readonly movie = input.required<Pick<MovieLookup, 'tmdbId' | 'title'>>();
  /** When set, approves this request with the chosen options instead of adding the movie directly. */
  readonly request = input<MediaRequest | null>(null);
  readonly added = output<Movie>();
  readonly approved = output<MediaRequest>();
  readonly closed = output<void>();

  protected readonly adding = signal(false);
  protected readonly rootFolders = signal<RootFolder[]>([]);
  protected readonly profiles = signal<QualityProfile[]>([]);
  protected rootFolder = '';
  protected profileId = 0;
  protected readonly availabilityOptions = AVAILABILITY_OPTIONS;
  protected monitored = true;
  protected searchNow = true;
  protected minimumAvailability: MinimumAvailability = 'Released';

  async ngOnInit() {
    const [folders, profiles] = await Promise.all([this.api.rootFolders(), this.api.qualityProfiles()]);
    this.rootFolders.set(folders.filter(f => f.mediaType === 'Movie'));
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
          qualityProfileId: this.profileId, rootFolderPath: this.rootFolder, monitored: this.monitored, searchNow: this.searchNow,
          minimumAvailability: this.minimumAvailability,
        });
        this.toasts.success(`Approved ${approved.title}`);
        this.approved.emit(approved);
        return;
      }
      const created = await this.api.addMovie({
        tmdbId: this.movie().tmdbId, qualityProfileId: this.profileId, rootFolderPath: this.rootFolder,
        monitored: this.monitored, searchForMovie: this.searchNow, minimumAvailability: this.minimumAvailability,
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
