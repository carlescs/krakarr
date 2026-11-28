import { DatePipe } from '@angular/common';
import { Component, computed, input } from '@angular/core';
import { PlayState } from '../../core/models';

/** One badge per media server with the title's watched state ("Watched", "40% watched" or "Unwatched"). */
@Component({
  selector: 'kr-play-status',
  imports: [DatePipe],
  templateUrl: './play-status.html',
  styleUrl: './play-status.scss',
  host: { class: 'contents' },
})
export class PlayStatus {
  readonly states = input<PlayState[]>([]);
  /** Names the server in each badge only when several report, so a single one stays short. */
  protected readonly showServer = computed(() => this.states().length > 1);
  protected percent(state: PlayState) { return Math.round((state.progress ?? 0) * 100); }
}
