import { Component, input } from '@angular/core';

@Component({
  selector: 'kr-poster',
  templateUrl: './poster.html',
  styleUrl: './poster.scss',
  host: { class: 'block aspect-[2/3] overflow-hidden rounded-lg bg-surface-800' },
})
export class Poster {
  readonly url = input<string | null | undefined>();
  readonly title = input<string>('');
}
