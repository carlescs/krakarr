import { Component, computed, input } from '@angular/core';
import { OmdbInfo } from '../../core/models';

/** Credits, awards and box office from OMDb, as a compact list under a title's overview. Renders nothing without data. */
@Component({
  selector: 'kr-omdb-facts',
  templateUrl: './omdb-facts.html',
  styleUrl: './omdb-facts.scss',
  host: { class: 'block empty:hidden' },
})
export class OmdbFacts {
  readonly info = input<OmdbInfo | null | undefined>(null);

  protected readonly facts = computed(() => {
    const info = this.info();
    if (!info) return [];
    const facts: { label: string; value: string }[] = [];
    if (info.director) facts.push({ label: info.director.includes(',') ? 'Directors' : 'Director', value: info.director });
    if (info.writer) facts.push({ label: info.writer.includes(',') ? 'Writers' : 'Writer', value: info.writer });
    if (info.awards) facts.push({ label: 'Awards', value: info.awards });
    if (info.boxOffice) facts.push({ label: 'Box office', value: info.boxOffice });
    return facts;
  });
}
