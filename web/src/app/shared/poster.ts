import { Component, input } from '@angular/core';

@Component({
  selector: 'kr-poster',
  template: `
    @if (url()) {
      <img [src]="url()" [alt]="title()" loading="lazy" class="h-full w-full object-cover" />
    } @else {
      <div class="flex h-full w-full items-center justify-center bg-gradient-to-br from-surface-700 to-surface-850 p-3 text-center text-sm font-medium text-slate-400">
        {{ title() }}
      </div>
    }
  `,
  host: { class: 'block aspect-[2/3] overflow-hidden rounded-lg bg-surface-800' },
})
export class Poster {
  readonly url = input<string | null | undefined>();
  readonly title = input<string>('');
}
