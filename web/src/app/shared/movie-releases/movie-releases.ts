import { DatePipe } from '@angular/common';
import { Component, input } from '@angular/core';
import { RELEASE_LABELS } from '../../core/calendar';
import { MovieRelease, ReleaseDateType } from '../../core/models';

/** Theatrical, digital and physical release dates card, with region + earliest worldwide fallback. */
@Component({
  selector: 'kr-movie-releases',
  imports: [DatePipe],
  templateUrl: './movie-releases.html',
  host: { class: 'block' },
})
export class MovieReleases {
  readonly releases = input.required<MovieRelease[]>();
  readonly releaseRegion = input<string | null | undefined>(null);

  protected readonly releaseLabels = RELEASE_LABELS;
  protected readonly releaseBar: Record<ReleaseDateType, string> = {
    Theatrical: 'border-l-amber-400', Digital: 'border-l-violet-400', Physical: 'border-l-pink-400',
  };
}
