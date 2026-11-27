import { Component, computed, input } from '@angular/core';
import { MediaInfo } from '../../core/models';

/** What a library file's headers say: video, audio tracks, duration, and warnings (fake, sample, unwanted audio). */
@Component({
  selector: 'kr-media-info',
  templateUrl: './media-info.html',
  styleUrl: './media-info.scss',
  host: { class: 'block' },
})
export class MediaInfoView {
  readonly info = input<MediaInfo | null | undefined>(null);

  protected readonly video = computed(() => {
    const i = this.info();
    if (!i) return null;
    const parts = [
      i.videoCodec,
      i.width > 0 && i.height > 0 ? `${i.width}×${i.height}` : null,
      i.resolution,
      i.bitDepth ? `${i.bitDepth}-bit` : null,
    ].filter(Boolean);
    return parts.length > 0 ? parts.join(' · ') : null;
  });

  protected readonly duration = computed(() => {
    const seconds = this.info()?.durationSeconds;
    if (!seconds) return null;
    const minutes = Math.round(seconds / 60);
    return minutes >= 60 ? `${Math.floor(minutes / 60)} h ${minutes % 60} min` : minutes > 0 ? `${minutes} min` : `${seconds} s`;
  });

  protected track(a: MediaInfo['audio'][number]) {
    const codec = [a.codec, a.channels].filter(Boolean).join(' ');
    return [codec || 'Unknown codec', a.languageName ?? 'language not tagged'].join(' · ');
  }
}
