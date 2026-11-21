import { Component, computed, input } from '@angular/core';

@Component({
  selector: 'kr-progress',
  templateUrl: './progress.html',
  styleUrl: './progress.scss',
  host: { class: 'block' },
})
export class Progress {
  readonly value = input(0);
  readonly color = input('bg-accent-500');
  protected readonly clamped = computed(() => Math.max(0, Math.min(100, this.value())));
}
