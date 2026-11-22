import { Component, input } from '@angular/core';
import { RouterLink } from '@angular/router';
import { CastMember, CrewMember } from '../../core/models';

/** Key crew and a scrolling row of the cast, each linking to the person's page. */
@Component({
  selector: 'kr-credits',
  imports: [RouterLink],
  templateUrl: './credits.html',
  styleUrl: './credits.scss',
  host: { class: 'block' },
})
export class CreditsView {
  readonly cast = input<CastMember[]>([]);
  readonly crew = input<CrewMember[]>([]);
  readonly loading = input(false);
  /** Heading of the cast row ("Guest stars" for an episode). */
  readonly castTitle = input('Cast');

  protected readonly placeholders = [1, 2, 3, 4, 5, 6, 7, 8];

  protected initials(name: string) {
    return name.split(/\s+/).filter(Boolean).slice(0, 2).map(p => p[0]).join('').toUpperCase();
  }
}
