import { Component, computed, input } from '@angular/core';

@Component({
  selector: 'kr-progress',
  template: `
    <div class="h-1.5 w-full overflow-hidden rounded-full bg-surface-700">
      <div class="h-full rounded-full transition-[width] duration-500" [class]="color()" [style.width.%]="clamped()"></div>
    </div>
  `,
  host: { class: 'block' },
})
export class Progress {
  readonly value = input(0);
  readonly color = input('bg-accent-500');
  protected readonly clamped = computed(() => Math.max(0, Math.min(100, this.value())));
}
