import { Component } from '@angular/core';

@Component({
  selector: 'kr-subtitles',
  template: `
    <h1 class="page-title mb-2">Subtitles</h1>
    <div class="card mt-6 px-6 py-12 text-center">
      <p class="text-base font-medium text-white">Subtitle management is on the roadmap</p>
      <p class="mx-auto mt-2 max-w-lg text-sm text-slate-400">
        Krakarr will search providers such as OpenSubtitles for missing subtitles in your preferred languages
        and store them next to your media (Bazarr replacement).
      </p>
    </div>
  `,
})
export class SubtitlesPage {}
