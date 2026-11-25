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

/**
 * The torrents of one movie or series on its page: the ones still downloading (or waiting to be imported) with their
 * progress, then the imported ones still seeding. Staff can open each one on the downloads page.
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
}
