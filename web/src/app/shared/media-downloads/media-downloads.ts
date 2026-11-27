import { Component, computed, inject, input, signal } from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import { RouterLink } from '@angular/router';
import { Auth } from '../../core/auth';
import { DurationPipe, SpeedPipe } from '../../core/format';
import { TorrentStatus } from '../../core/models';
import { Progress } from '../progress/progress';
import { StateBadge } from '../state-badge/state-badge';

/** Imported torrents listed before "Show all": a long-running series can keep many season packs seeding. */
const SEEDING_SHOWN = 3;
/** Whether the viewer collapsed the section: one preference for every movie and series page. */
const HIDDEN_KEY = 'krakarr.mediaDownloads.hidden';

function readHidden(): boolean {
  try { return localStorage.getItem(HIDDEN_KEY) === 'true'; } catch { return false; }
}

/**
 * The torrents of one movie or series on its page: the ones still downloading (or waiting to be imported) with their
 * progress, then the imported ones still seeding. Staff can open each one on the downloads page. Anyone can collapse
 * the section to its heading; the choice is remembered in this browser.
 */
@Component({
  selector: 'kr-media-downloads',
  imports: [NgTemplateOutlet, RouterLink, Progress, StateBadge, SpeedPipe, DurationPipe],
  templateUrl: './media-downloads.html',
  styleUrl: './media-downloads.scss',
})
export class MediaDownloads {
  protected readonly auth = inject(Auth);

  readonly downloads = input.required<readonly TorrentStatus[]>();
  /** Extra text per torrent (by info hash), such as the episodes it contains. */
  readonly labels = input<ReadonlyMap<string, string>>(new Map());

  protected readonly active = computed(() => this.downloads().filter(d => !d.imported));
  protected readonly seeding = computed(() => this.downloads().filter(d => d.imported));
  protected readonly showAllSeeding = signal(false);
  protected readonly seedingShown = computed(() => this.showAllSeeding() ? this.seeding() : this.seeding().slice(0, SEEDING_SHOWN));
  protected readonly hidden = signal(readHidden());

  protected toggleHidden(): void {
    const hidden = !this.hidden();
    this.hidden.set(hidden);
    try { localStorage.setItem(HIDDEN_KEY, String(hidden)); } catch { /* storage unavailable */ }
  }
}
