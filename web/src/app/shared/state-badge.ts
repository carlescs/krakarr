import { Component, computed, input } from '@angular/core';
import { DownloadState } from '../core/models';

const STYLES: Record<DownloadState, string> = {
  Queued: 'bg-slate-500/15 text-slate-300',
  FetchingMetadata: 'bg-violet-500/15 text-violet-300',
  Downloading: 'bg-accent-500/15 text-accent-300',
  Paused: 'bg-amber-500/15 text-amber-300',
  Seeding: 'bg-emerald-500/15 text-emerald-300',
  Completed: 'bg-emerald-500/10 text-emerald-400',
  Error: 'bg-rose-500/15 text-rose-300',
};

const LABELS: Partial<Record<DownloadState, string>> = { FetchingMetadata: 'Metadata' };

@Component({
  selector: 'kr-state-badge',
  template: `<span class="badge" [class]="style()">{{ label() }}</span>`,
})
export class StateBadge {
  readonly state = input.required<DownloadState>();
  protected readonly style = computed(() => STYLES[this.state()] ?? STYLES.Queued);
  protected readonly label = computed(() => LABELS[this.state()] ?? this.state());
}
